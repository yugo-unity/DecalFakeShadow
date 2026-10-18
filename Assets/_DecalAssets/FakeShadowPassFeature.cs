
#if DEVELOPMENT_BUILD || UNITY_EDITOR
#define ENABLE_PROFILING
#endif

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace UTJ {
    #region FAKE SHADOW
    /// <summary>
    /// FakeShadowを参照で比較する
    /// Play Mode終了時のSceneの復元ではInstance IDが引き継がれ、UnityEngine.Objectの等値比較(Instance ID)では
    /// 破棄済みのPlay Modeのインスタンスと復元されたインスタンスを区別できない
    /// </summary>
    internal sealed class FakeShadowReferenceComparer : IEqualityComparer<FakeShadow> {
        public static readonly FakeShadowReferenceComparer instance = new FakeShadowReferenceComparer();
        public bool Equals(FakeShadow x, FakeShadow y) => ReferenceEquals(x, y);
        public int GetHashCode(FakeShadow obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// FakeShadowの割当管理 (Domain内で1つ)
    /// FakeShadowPassFeatureは設定値を渡すだけで、稼働状態はFeatureの生成・破棄 (Renderer設定の変更等) に依らず保持する
    ///
    /// NOTE: Domain Reloadなし (Enter Play Mode Options) ではstaticの値がPlay Modeを跨いで残る
    ///   - 稼働状態 (requests/available) はFakeShadowのOnEnable/OnDisableで整合するのでクリアしない
    ///     (Scene Reloadなしでは Edit Modeで稼働中のFakeShadowが再OnEnableされないので、クリアすると取りこぼす)
    ///   - 破棄済みのFakeShadowは参照比較で区別し、ResolveRequests/UpdateShadowsで読み捨てる
    ///   - ランタイムで変更される値 (color) はPlay Mode開始時に初期化する
    /// Shaderへの値はグローバルに設定せず、FakeShadowPassFeatureのPassが描画の度にCommandBufferで設定する
    /// (グローバル値はDomain Reloadを跨いで残るので、古い値が残らないようにする)
    /// </summary>
    internal static class FakeShadowManager {
        #region DEFINE
        public struct MatParam {
            public int index;
            public Vector2 uvScale;
            public Vector2 uvBias;
            public Material material;   // Shadow Mesh用の共通Material
            public int shadowCasterLayer; // Shadow Mesh用の内部RendererのLayer
            public int userValueShift;
            public RenderingLayerMask receiverRenderingLayers; // Decal ProjectorのRendering Layer Mask
        }

        // 同時に有効な影の上限 (Inspectorで設定できる最大数)
        // NOTE: 仕組み上の限界は31 (User Value 5bitの0は未割当、FakeShadowDataの末尾は番兵、Grid Clippingのビットマスクは32bit)
        //       25は5x5のグリッド、26以上は6x6になり影1つあたりの解像度が下がる
        public const int SHADOW_LIMIT = 25;
        public const uint USER_VALUE_MASK = 0x1Fu; // User Valueの32bit中、5bitを使用する（割当インデックス+1、0は未割当）
        public const int USER_VALUE_SHIFT_MAX = 27; // 32 - 5
        // FakeShadowData (FakeShadowCaster.shaderの定数バッファ) の要素数、FAKE_SHADOW_DATA_COUNTと合わせる
        // User Valueの5bitで引ける数で、末尾(31)はUser Value 0 (未割当) が引く番兵
        public const int SHADOW_DATA_COUNT = (int)USER_VALUE_MASK + 1;
        // FakeShadowDataのレイアウト: float4x4[SHADOW_DATA_COUNT] + (line, mapTexel, clipMask, userValueShift)
        public const int SHADOW_DATA_STRIDE = 16; // float4単位
        public const int SHADOW_DATA_SIZE = SHADOW_DATA_COUNT * 64 + 16;
        public const int SHADOW_DATA_PARAMS_OFFSET = SHADOW_DATA_COUNT * 16; // uint単位
        public static readonly int PROP_ID_SHADOW_DATA = Shader.PropertyToID("FakeShadowData");
        public static readonly int PROP_ID_COLOR = Shader.PropertyToID("_FakeShadowColor");
        public static readonly Quaternion TOP_ROT = Quaternion.Euler(90f, 0f, 0f);
        private static readonly Color DEFAULT_COLOR = Color.gray;
        // 未使用のインデックスと番兵の行列: 全頂点をクリップ範囲外 (0, 0, -2, 1) に置く
        private static readonly Matrix4x4 CLIP_OUT_MATRIX = new Matrix4x4(Vector4.zero, Vector4.zero, Vector4.zero, new Vector4(0f, 0f, -2f, 1f));
        #endregion


        #region MEMBER
        // NOTE: requestsはRenderPassの生成より先にMonoBehaviourのOnEnableが飛ぶのでFeatureに依らず保持する
        private static readonly Stack<FakeShadow> requests = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static readonly Dictionary<FakeShadow, int> available = new Dictionary<FakeShadow, int>(SHADOW_LIMIT, FakeShadowReferenceComparer.instance);
        private static readonly Stack<FakeShadow> cancels = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static readonly Stack<int> indexStack = new Stack<int>(SHADOW_LIMIT);
        private static readonly MatParam[] matParams = new MatParam[SHADOW_LIMIT];
        // Character毎のデータ（インデックスはRenderer User Valueの値 - 1）
        private static readonly Matrix4x4[] s_Matrices = CreateClipOutMatrices(); // Projection * View
        // Grid Clipping有効フラグ (bit i: インデックスiの影)、グリッド位置とクリップ矩形はシェーダーで計算する
        private static uint s_ClipMask = 0u;
        private static float s_Line = 1f; // グリッドの分割数 (1辺)
        // 登録中のFeature、先頭のFeatureの設定を使用する
        private static readonly List<ScriptableRendererFeature> owners = new List<ScriptableRendererFeature>(1);
        private static Material s_Material = null;
        private static Color s_Color = DEFAULT_COLOR;
        private static Color s_DefaultColor = DEFAULT_COLOR; // FakeShadowPassFeatureのGUIで設定された色
        private static int availableCount = 0; // 0は未設定 (次のSetAvailableCountで全Shadowを再設定する)
        private static float mapSize = 1f;
        private static int userValueShift = 0;
        private static int shadowCasterLayer = 0; // Shadow Caster Layerの最下位ビットのLayer
        private static RenderingLayerMask receiverRenderingLayers = 1u; // Default
        #endregion


        #region MAIN FUNCTION
        /// <summary>
        /// Play Mode開始時の初期化 (Domain Reloadなしでも前回のPlay Modeでの変更を持ち越さない)
        /// Play Mode終了時もEdit Modeに持ち越さないように呼ぶ
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeValues() {
            color = s_DefaultColor;
        }

        /// <summary>
        /// FakeShadowPassFeatureのGUIで設定された影色
        /// GUIで変更された場合のみ反映し、ランタイムでcolorに設定された色は上書きしない
        /// </summary>
        internal static void SetDefaultColor(Color defaultColor) {
            if (s_DefaultColor == defaultColor)
                return;
            s_DefaultColor = defaultColor;
            color = defaultColor;
        }

        /// <summary>
        /// FakeShadowPassFeatureの登録
        /// </summary>
        internal static void Attach(ScriptableRendererFeature owner) {
            owners.RemoveAll(o => o == null); // Disposeされずに破棄されたFeature
            if (owners.Contains(owner))
                return;
            owners.Add(owner);
        }

        /// <summary>
        /// FakeShadowPassFeatureの登録解除
        /// 稼働中のShadowと共通Materialはそのまま保持し、Renderer再生成後のFeatureで引き続き使用する
        /// (Materialを破棄するとShadow Meshが再設定されるまで描画されない)
        /// </summary>
        internal static void Detach(ScriptableRendererFeature owner) {
            owners.Remove(owner);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterEditorCallbacks() {
            // 共通MaterialはHideAndDontSaveなので、Domain Reloadでstatic参照を失う前に破棄する
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseMaterial;
            // Play Modeでの変更をEdit Modeに持ち越さない
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // Play開始時のFeatureのGUI上の影色 (FeatureはアセットなのでPlay中のGUIでの変更がPlay終了後も残る)
        private static FakeShadowPassFeature s_PlayFeature = null;
        private static Color s_PlayFeatureColor = DEFAULT_COLOR;

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state) {
            switch (state) {
                case UnityEditor.PlayModeStateChange.ExitingEditMode:
                    s_PlayFeature = owners.Count > 0 ? owners[0] as FakeShadowPassFeature : null;
                    if (s_PlayFeature != null)
                        s_PlayFeatureColor = s_PlayFeature.shadowColor;
                    break;
                case UnityEditor.PlayModeStateChange.EnteredEditMode:
                    // Play中にGUIで変更された影色をPlay開始時の色に戻す
                    if (s_PlayFeature != null) {
                        s_PlayFeature.shadowColor = s_PlayFeatureColor;
                        s_DefaultColor = s_PlayFeatureColor;
                    }
                    s_PlayFeature = null;
                    ResetRuntimeValues();
                    break;
            }
        }
#endif

        /// <summary>
        /// 設定を反映するFeatureか (複数のFeatureが稼働している場合は先頭のFeatureの設定を使用する)
        /// </summary>
        internal static bool IsOwner(ScriptableRendererFeature feature) {
            return owners.Count > 0 && ReferenceEquals(owners[0], feature);
        }

        /// <summary>
        /// 共通Material
        /// </summary>
        internal static Material material { get => s_Material; }

        /// <summary>
        /// Fake Shadow描画用のShaderの設定 (overrideMaterialとShadow Meshで共通)
        /// </summary>
        internal static void SetShader(Shader shader) {
            var current = s_Material != null ? s_Material.shader : null;
            if (current == shader)
                return;

            ReleaseMaterial();
            if (shader != null) {
                s_Material = new Material(shader);
                s_Material.hideFlags = HideFlags.HideAndDontSave;
            }
            availableCount = 0; // Shadow MeshにMaterialを割り当て直す為、全Shadowを再設定する
        }

        private static void ReleaseMaterial() {
            if (s_Material == null)
                return;
            Object.DestroyImmediate(s_Material);
            s_Material = null;
            availableCount = 0; // 再登録時にShadow MeshへMaterialを割り当て直す
        }

        /// <summary>
        /// 上限設定
        /// </summary>
        /// <param name="count">上限値</param>
        /// <param name="decalMapSize">Decal Shadowバッファの解像度</param>
        internal static void SetAvailableCount(int count, float decalMapSize) {
            if (availableCount == count && mapSize == decalMapSize)
                return;

            availableCount = count;
            mapSize = decalMapSize;

            // NOTE: グリッド位置とクリップ矩形はFakeShadowCaster.shaderで同じ式で計算する
            var line = Mathf.Ceil(Mathf.Sqrt(count)); // 累乗でグリッド生成
            var uvScale = 1f / line; // 0~1

            indexStack.Clear();
            // 全Shadowを再設定するので、行列とGrid ClippingのフラグはSetupで改めて設定する
            ClearShadowData();
            s_Line = line;
            for (var i = 0; i < availableCount; ++i) {
                var uvBias = new Vector2(uvScale * Mathf.Floor((float)i % line), uvScale * Mathf.Floor((float)i / line));

                indexStack.Push(i);
                matParams[i].index = i;
                matParams[i].uvScale = new Vector2(uvScale, uvScale);
                matParams[i].uvBias = uvBias;
            }

            // 現在有効なShadowの更新
            if (available.Count > 0) {
                foreach (var shadow in available.Keys) {
                    if (shadow == null)
                        continue;
                    if (requests.Count >= availableCount) {
                        Debug.LogError($"Canceled FakeShadow [{shadow.name}]. The max count is insufficient.");
                        cancels.Push(shadow);
                        continue;
                    }
                    requests.Push(shadow);
                }
                available.Clear();
                // インデックスはindexStackの再構築で回収済みなので返却しない
                foreach (var shadow in cancels)
                    shadow.Cancel();
                cancels.Clear();
            }
        }

        /// <summary>
        /// Shadow Caster Layerの設定 (Shadow Mesh用の内部Rendererは最下位ビットのLayerに置く)
        /// </summary>
        /// <param name="mask">Shadow Caster Layer (0以外)</param>
        internal static void SetShadowCasterLayer(LayerMask mask) {
            var layer = 0;
            while (layer < 31 && (mask.value & (1 << layer)) == 0)
                ++layer;
            if (shadowCasterLayer == layer)
                return;

            shadowCasterLayer = layer;
            availableCount = 0; // 内部RendererのLayerを変更する為、全Shadowを再設定する
        }

        /// <summary>
        /// Fake Shadowを受けるRendering Layer Maskの設定 (Decal ProjectorのRendering Layer Maskを上書きする)
        /// </summary>
        internal static void SetReceiverRenderingLayers(RenderingLayerMask mask) {
            if (receiverRenderingLayers.value == mask.value)
                return;

            receiverRenderingLayers = mask;
            foreach (var shadow in available.Keys) {
                if (shadow != null)
                    shadow.ChangeReceiverRenderingLayers(mask);
            }
        }

        /// <summary>
        /// User Valueのbit位置の設定
        /// </summary>
        /// <param name="shift">User Valueの何bit目から5bitを使うか</param>
        internal static void SetUserValueShift(int shift) {
            shift = Mathf.Clamp(shift, 0, USER_VALUE_SHIFT_MAX);
            if (userValueShift == shift)
                return;

            userValueShift = shift;
            foreach (var shadow in available.Keys) {
                if (shadow != null)
                    shadow.ChangeUserValueShift(shift);
            }
        }

        /// <summary>
        /// 稼働中のShadowをすべて一時停止する (FakeShadowが無効になる設定を検出した場合)
        /// DecalProjectorを無効にしないと、停止した影が描画され続ける
        /// 一時停止したShadowはリクエストとして積み直すので、設定が戻ると自動で復帰する
        /// (設定が無効な間はFakeShadowPassFeatureがResolveRequestsを呼ばないので、リクエストは保留される)
        /// </summary>
        internal static void SuspendAll() {
            if (available.Count == 0)
                return;
            foreach (var shadow in available.Keys) {
                if (shadow != null)
                    cancels.Push(shadow);
            }
            available.Clear();
            // インデックスは次回のSetAvailableCountで全再構築するので返却しない
            availableCount = 0;
            ClearShadowData();
            foreach (var shadow in cancels)
                shadow.Suspend();
            cancels.Clear();
        }

        /// <summary>
        /// 新規リクエストの対応
        /// </summary>
        internal static void ResolveRequests() {
            while (requests.Count > 0) {
                var req = requests.Pop();
                // 破棄済み・キャンセルされた場合は無視
                if (req == null || !req.isRequested)
                    continue;
                if (available.ContainsKey(req)) {
                    Debug.LogError($"Already available : {req.name}");
                    continue;
                }
                if (indexStack.Count == 0) {
                    Debug.LogError($"Over requests : {req.name}");
                    req.Cancel();
                    continue;
                }
                // SkinnedMeshはMeshRendererを分けずにSubMeshの方がオーバーヘッドが低いのでMulti Renderer対応はしない
                if (req.isShadowMesh && s_Material == null) {
                    Debug.LogError("Not supported FakeShadow by ShadowMesh. Need the Shader.");
                    req.Cancel();
                    continue;
                }
                if (!req.ValidateShadowMesh()) {
                    req.Cancel();
                    continue;
                }

                var index = indexStack.Pop();
                available.Add(req, index);

                matParams[index].material = s_Material;
                matParams[index].shadowCasterLayer = shadowCasterLayer;
                matParams[index].userValueShift = userValueShift;
                matParams[index].receiverRenderingLayers = receiverRenderingLayers;
                req.Setup(in matParams[index]);
            }
        }

        /// <summary>
        /// 稼働中のShadowのパラメータ更新 (Edit Mode用、Play ModeではFakeShadow.LateUpdateで行う)
        /// </summary>
        internal static void UpdateShadows() {
            foreach (var shadow in available.Keys) {
                if (shadow != null)
                    shadow.UpdateShadow();
            }
        }

        /// <summary>
        /// FakeShadowData (FakeShadowCaster.shaderの定数バッファ) の内容を書き込む
        /// </summary>
        /// <param name="matrices">行列 (SHADOW_DATA_COUNT個)</param>
        /// <param name="parameters">line, mapTexel, clipMask, userValueShift (bit列をそのまま書くのでuint)</param>
        internal static void WriteShadowData(NativeArray<Matrix4x4> matrices, NativeArray<uint> parameters) {
            matrices.CopyFrom(s_Matrices);
            parameters[0] = (uint)System.BitConverter.SingleToInt32Bits(s_Line);
            parameters[1] = (uint)System.BitConverter.SingleToInt32Bits(1f / mapSize);
            parameters[2] = s_ClipMask;
            parameters[3] = (uint)userValueShift;
        }

        private static Matrix4x4[] CreateClipOutMatrices() {
            var matrices = new Matrix4x4[SHADOW_DATA_COUNT];
            for (var i = 0; i < matrices.Length; ++i)
                matrices[i] = CLIP_OUT_MATRIX;
            return matrices;
        }

        /// <summary>
        /// 全インデックスの行列とGrid Clippingのフラグを未使用の状態に戻す
        /// </summary>
        private static void ClearShadowData() {
            for (var i = 0; i < s_Matrices.Length; ++i)
                s_Matrices[i] = CLIP_OUT_MATRIX;
            s_ClipMask = 0u;
        }

        internal static void SetShadowMatrix(int index, in Matrix4x4 viewProj) {
            s_Matrices[index] = viewProj;
        }

        /// <summary>
        /// Grid Clipping有効フラグの設定 (割当時・FakeShadow.gridClippingの変更時)
        /// </summary>
        internal static void SetShadowClip(int index, bool clip) {
            if (clip)
                s_ClipMask |= 1u << index;
            else
                s_ClipMask &= ~(1u << index);
        }
        #endregion


        #region PUBLIC FUNCTION
        /// <summary>
        /// 影色
        /// </summary>
        public static Color color {
            get { return s_Color; }
            set { s_Color = value; } // Shaderへは次の描画でFakeShadowPassFeatureのPassが設定する
        }

        /// <summary>
        /// 有効リクエスト (割当はResolveRequestsで行う)
        /// </summary>
        /// <param name="shadow">FakeShadowインスタンス</param>
        public static void Request(FakeShadow shadow) {
            // Sleep後に解決前のWakeup(Edit ModeのOnDisable/OnEnableの連続等)は積まれたままのリクエストを再利用する
            if (ContainsRequest(shadow)) {
                if (shadow.isRequested)
                    Debug.LogError("Duplicated to request");
                return;
            }

            requests.Push(shadow);
        }

        private static bool ContainsRequest(FakeShadow shadow) {
            foreach (var req in requests) {
                if (ReferenceEquals(req, shadow))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// インデックス返却
        /// </summary>
        /// <param name="shadow">返却するFakeShadow</param>
        public static bool Return(FakeShadow shadow) {
            if (ReferenceEquals(shadow, null))
                return false;
            if (available.TryGetValue(shadow, out var index)) {
                indexStack.Push(index);
                available.Remove(shadow);
                // 次にこのインデックスを使う影にフラグを残さない、空いたインデックスは何も描画しない
                SetShadowClip(index, false);
                s_Matrices[index] = CLIP_OUT_MATRIX;
                return true;
            }
            Debug.LogError("Not found to return");
            return false;
        }
        #endregion
    }
    #endregion

    /// <summary>
    /// DecalRendererFeatureの"Use Rendering Layers"の有効を必須とする（無効時は警告を出し、稼働中の影をキャンセルして無効になる）
    ///   - CharacterはRenderer標準のPrepass/Opaque Passで描画する（Opaque/Prepass Layer MaskにShadow Caster Layerを含めること）
    ///   - Characterの Rendering Layer Mask を Receiver Rendering Layers から外し、Decal LayersでDecal Shadowから除外する
    /// Decal RendererFeatureのTechnique(Screen Space / DBuffer)は手動で切り替える（ランタイムでの切替は非対応、Editor上での切替は可）
    /// </summary>
    public class FakeShadowPassFeature : ScriptableRendererFeature {
        [Tooltip("Layers that cast fake shadows.")]
        public LayerMask shadowCasterLayer = 0;
        [Tooltip("Shader that draws casters into the decal map (UTJ/FakeShadowCaster).")]
        public Shader fakeShadowShader = null;
        [Range(0, FakeShadowManager.USER_VALUE_SHIFT_MAX), Tooltip("Start bit of the 5 bits FakeShadow uses in the renderer's User Value.")]
        public int userValueShift = 0;
        [Tooltip("Rendering layers that receive fake shadows.")]
        public RenderingLayerMask receiverRenderingLayers = 1u; // Default
        [Tooltip("Shadow color, multiplied by the Base Color of the DecalProjector's material. Alpha sets the opacity.")]
        public Color shadowColor = Color.gray;
        [Range(1, FakeShadowManager.SHADOW_LIMIT), Tooltip("Maximum number of fake shadows at the same time.")]
        public int maxShadowCount = 9; // 3x3
        [Tooltip("Resolution of the decal map shared by all fake shadows.")]
        public int decalMapSize = 512;

        private CharacterShadowPass shadowPass = null;


        #region FAKE SHADOW PASS
        /// <summary>
        /// CharacterレイヤーのFake Shadow pass
        /// NOTE: FakeShadowData (GraphicsBuffer) の更新をPass内のコマンドで行う為Unsafe Passにする
        ///       (RasterCommandBufferにはSetBufferDataが無い)
        ///       前後のPass (Shadow Map / カメラ解像度のPrepass) とはRender Targetのサイズが異なり、Native Render Passのマージ対象にならない
        /// </summary>
        class CharacterShadowPass : ScriptableRenderPass {
            public LayerMask layerMask = 0;
            public int decalMapSize = 512;
            public Material overrideMaterial = null;

            // CharacterのShaderに依らず描画する為、通常描画のPassを対象にMaterialを差し替える
            // FakeShadowはShadow Mesh(Fake Shadow描画専用)用
            // NOTE: 対象のPassを複数持つShaderはその数だけ描画される
            private static readonly List<ShaderTagId> SHADER_TAG_IDS = new List<ShaderTagId> {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("FakeShadow"),
            };
            private static readonly int DECAL_MAP_ID = Shader.PropertyToID("_DecalTexture");
            // DecalProjectorのPassはRender Graph上でこのTextureの利用を宣言しないので、
            // Transientにするとエイリアシングされうる。Featureで保持してImportする
            private RTHandle decalMap = null;

            class PassData {
                public RendererListHandle rendererList;
                public TextureHandle decalMap;
                public GraphicsBuffer shadowDataBuffer;
                public NativeArray<uint> shadowData;
                public Color color;
            }

            // FakeShadowCaster.shaderの定数バッファ (FakeShadowData)
            private GraphicsBuffer shadowDataBuffer = null;
            private NativeArray<uint> shadowData;              // 更新用 (bit列をそのまま書くのでuint)
            private NativeArray<Matrix4x4> shadowDataMatrices; // shadowDataの行列部分のビュー
            private NativeArray<uint> shadowDataParams;        // shadowDataのパラメータ部分のビュー

            public CharacterShadowPass() {
                // 他のPassに依存しないので、Main/Additional LightのShadow Map生成と連続させてRender Targetの切り替えを抑える
                this.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Fake Shadow - Shadow Pass");
#endif
                this.shadowDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant,
                    FakeShadowManager.SHADOW_DATA_SIZE / FakeShadowManager.SHADOW_DATA_STRIDE, FakeShadowManager.SHADOW_DATA_STRIDE);
                this.shadowData = new NativeArray<uint>(FakeShadowManager.SHADOW_DATA_SIZE / sizeof(uint), Allocator.Persistent);
                this.shadowDataMatrices = this.shadowData.GetSubArray(0, FakeShadowManager.SHADOW_DATA_PARAMS_OFFSET).Reinterpret<Matrix4x4>(sizeof(uint));
                this.shadowDataParams = this.shadowData.GetSubArray(FakeShadowManager.SHADOW_DATA_PARAMS_OFFSET, 4);
            }

            public void Dispose() {
                this.decalMap?.Release();
                this.decalMap = null;
                this.shadowDataBuffer?.Release();
                this.shadowDataBuffer = null;
                if (this.shadowData.IsCreated)
                    this.shadowData.Dispose();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();

                // Decal Mapは影の被覆率(0/1)のみを持ち、色はFakeShadowByDecalがグローバルの_FakeShadowColor (このPassで設定) から取る
                // NOTE: 色をDecal Mapに書くとクリア色(0,0,0,0)とのBilinear補間でエッジのRGBが黒に寄り、Decalのアルファブレンドと二重に掛かって暗い縁が出る
                var desc = new RenderTextureDescriptor(this.decalMapSize, this.decalMapSize, GraphicsFormat.R8_UNorm, 0, 0);
                desc.msaaSamples = 1;
                RenderingUtils.ReAllocateHandleIfNeeded(ref this.decalMap, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_DecalTexture");
                var decalMapHandle = renderGraph.ImportTexture(this.decalMap);

                // Character毎のデータ (Managerが保持) をFakeShadowDataに書き込む、GraphicsBufferへの転送はPass内で行う
                FakeShadowManager.WriteShadowData(this.shadowDataMatrices, this.shadowDataParams);

                using (var builder = renderGraph.AddUnsafePass<PassData>(this.passName, out var passData, this.profilingSampler)) {
                    // DecalをGPU Instancingする為にDecal Map一つにグリッドで描画する
                    // 共通Materialで描画し、Character毎のデータはRenderer User Valueのインデックスで定数バッファから引く（SRP Batcherで1バッチになる）
                    var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_IDS, renderingData, cameraData, lightData, SortingCriteria.OptimizeStateChanges);
                    drawSettings.overrideMaterial = this.overrideMaterial;
                    drawSettings.overrideMaterialPassIndex = 0;
                    drawSettings.perObjectData = PerObjectData.None;
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    passData.rendererList = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawSettings, filteringSettings));
                    passData.decalMap = decalMapHandle;
                    passData.shadowDataBuffer = this.shadowDataBuffer;
                    passData.shadowData = this.shadowData;
                    passData.color = FakeShadowManager.color;

                    builder.UseRendererList(passData.rendererList);
                    builder.UseTexture(decalMapHandle, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(decalMapHandle, DECAL_MAP_ID);
                    builder.AllowPassCulling(false);
                    // FakeShadowDataと_FakeShadowColor (FakeShadowByDecalが参照) をグローバルに設定する
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc<PassData>(static (data, context) => {
                        var cmd = context.cmd;
                        // 定数バッファを名前で丸ごとバインドすると、描画時に値毎の解決・コピーが発生しない
                        cmd.SetBufferData(data.shadowDataBuffer, data.shadowData);
                        cmd.SetGlobalConstantBuffer(data.shadowDataBuffer, FakeShadowManager.PROP_ID_SHADOW_DATA, 0, FakeShadowManager.SHADOW_DATA_SIZE);
                        cmd.SetGlobalColor(FakeShadowManager.PROP_ID_COLOR, data.color);
                        cmd.SetRenderTarget(data.decalMap, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                        cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1f, 0);
                        cmd.DrawRendererList(data.rendererList);
                    });
                }
            }
        }
        #endregion


        // protectedの為Reflectionで取得 (internalはURPRuntime.asmrefで直接参照)
        static readonly System.Reflection.PropertyInfo s_PropRendererFeatures = typeof(ScriptableRenderer).GetProperty("rendererFeatures", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private ScriptableRenderer cachedRenderer = null;
        private DecalTechnique cachedTechnique = DecalTechnique.Invalid;
        private bool cachedDecalLayers = false;

        // 警告は種類毎に1回だけ出し、原因が解消されたら再び出せるようにする
        [System.Flags]
        private enum WARNING {
            NONE = 0,
            NO_DECAL_FEATURE = 1 << 0,
            GBUFFER = 1 << 1,
            NO_SHADER = 1 << 2,
            NOT_OWNER = 1 << 3,
            NO_DECAL_LAYERS = 1 << 4,
            NO_CASTER_LAYER = 1 << 5,
        }
        private WARNING warned = WARNING.NONE;
#if UNITY_EDITOR
        private bool maskWarned = false;

        // Edit Modeの再描画要求
        // 設定の判定はAddRenderPasses、反映は次の描画のOnBeginContextRenderingで行うので、反映には描画が2回要る
        // Edit ModeはViewの再描画が変更時にしか走らないので、判定が反映済みの状態と違う場合は再描画を1回予約する
        private enum APPLIED {
            NONE,       // 未判定（DecalRendererFeatureが無い場合を含む）
            SUSPENDED,  // 一時停止（非対応のTechnique、Use Rendering Layers無効、Shadow Caster Layer未設定）
            ACTIVE,     // 稼働
        }
        private APPLIED applied = APPLIED.NONE;
        private bool repaintRequested = false;

        private void RequestRepaintIfChanged(APPLIED current) {
            if (Application.isPlaying || this.repaintRequested || current == this.applied)
                return;
            // 設定を反映するのは先頭のFeatureだけなので、それ以外は要求しない（反映されず再描画が続く）
            if (!FakeShadowManager.IsOwner(this))
                return;
            this.repaintRequested = true;
            // 描画中に再描画を呼ぶと再入するので、描画の後に行う
            // NOTE: delayCallはEditorが非フォーカスの間は実行されないことがあるので、updateで1回だけ行う
            UnityEditor.EditorApplication.update -= RepaintAllViewsOnce;
            UnityEditor.EditorApplication.update += RepaintAllViewsOnce;
        }

        private static void RepaintAllViewsOnce() {
            UnityEditor.EditorApplication.update -= RepaintAllViewsOnce;
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
#endif

        /// <summary>
        /// 同じRendererのDecalRendererFeatureからTechniqueとUse Rendering Layersを取得
        /// </summary>
        private DecalTechnique GetDecalTechnique(ScriptableRenderer renderer) {
#if !UNITY_EDITOR
            // ランタイムでの設定変更は非対応なのでキャッシュする（EditorではInspectorでの変更に追従させる）
            if (this.cachedRenderer == renderer)
                return this.cachedTechnique;
#endif
            this.cachedRenderer = renderer;
            this.cachedTechnique = DecalTechnique.Invalid;
            this.cachedDecalLayers = false;

            var features = (List<ScriptableRendererFeature>)s_PropRendererFeatures.GetValue(renderer);
            foreach (var feature in features) {
                if (feature is DecalRendererFeature decal) {
                    this.cachedTechnique = decal.GetTechnique(renderer);
                    this.cachedDecalLayers = decal.requiresDecalLayers;
                    this.UpdateWarning(WARNING.NO_DECAL_FEATURE, false, null);
                    return this.cachedTechnique;
                }
            }

            this.UpdateWarning(WARNING.NO_DECAL_FEATURE, true, "FakeShadowPassFeature requires DecalRendererFeature in the same renderer. FakeShadow is disabled.");
            return DecalTechnique.Invalid;
        }

        /// <summary>
        /// 警告の更新 (発生中は1回だけ出し、解消されたらフラグを戻す)
        /// </summary>
        /// <returns>発生中</returns>
        private bool UpdateWarning(WARNING warning, bool occurred, string message) {
            if (!occurred) {
                this.warned &= ~warning;
                return false;
            }
            if ((this.warned & warning) == 0) {
                Debug.LogWarning(message);
                this.warned |= warning;
            }
            return true;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Opaque/Prepass Layer MaskにShadow Caster Layerが含まれていない場合の警告
        /// </summary>
        private void ValidateLayerMask(ScriptableRenderer renderer) {
            if (!(renderer is UniversalRenderer universalRenderer))
                return;
            var casterMask = this.shadowCasterLayer.value;
            var opaqueMask = universalRenderer.opaqueLayerMask.value;
            // NOTE: prepassLayerMaskはCompatibility ModeではOpaque Layer Maskになる、実際にPrepassで使われる値を参照する
            var prepassMask = universalRenderer.prepassLayerMask.value;
            var missing = (opaqueMask & casterMask) != casterMask || (prepassMask & casterMask) != casterMask;
            if (missing != this.maskWarned) {
                this.maskWarned = missing;
                if (missing)
                    Debug.LogWarning($"FakeShadowPassFeature: include Shadow Caster Layer in \"Opaque Layer Mask\" and \"Prepass Layer Mask\" of the renderer. (Opaque Layer Mask: 0x{opaqueMask:X}, Prepass Layer Mask: 0x{prepassMask:X})");
            }
        }
#endif

        /// <summary>
        /// Rendering Layerの書き出しタイミングの申告 (URP内部API、URPRuntime.asmrefでURPのアセンブリに含めて上書きする)
        /// 既定実装はRendering Layerを使わない場合もDepthNormalPrePassを申告し、全Featureの申告の早い方が採用されるので
        /// Screen Space Decal (Opaqueで書き出し) でもDepthNormals Prepassが発生してしまう
        /// FakeShadowPassFeature自体はRendering Layerを使わないので、Opaqueを申告して他のFeatureの要求を妨げない
        /// </summary>
        internal override bool RequireRenderingLayers(bool isDeferred, bool needsGBufferAccurateNormals, out RenderingLayerUtils.Event atEvent, out RenderingLayerUtils.MaskSize maskSize) {
            atEvent = RenderingLayerUtils.Event.Opaque;
            maskSize = RenderingLayerUtils.MaskSize.Bits8;
            return false;
        }

        public override void Create() {
            if (this.shadowPass != null)
                return;

            this.shadowPass = new CharacterShadowPass();
            FakeShadowManager.Attach(this);
            if (FakeShadowManager.IsOwner(this)) {
                FakeShadowManager.SetShader(this.fakeShadowShader);
                FakeShadowManager.SetDefaultColor(this.shadowColor);
            }
            RenderPipelineManager.beginContextRendering += this.OnBeginContextRendering;
        }

        protected override void Dispose(bool disposing) {
            RenderPipelineManager.beginContextRendering -= this.OnBeginContextRendering;
            this.shadowPass?.Dispose();
            this.shadowPass = null;
            // 稼働中のShadowはManagerが保持し、Renderer再生成後のFeatureで再設定される
            FakeShadowManager.Detach(this);
            this.cachedRenderer = null;
            // 次のAddRenderPassesで判定するまで、古い設定でOnBeginContextRenderingを動かさない
            this.cachedTechnique = DecalTechnique.Invalid;
            this.cachedDecalLayers = false;
            this.warned = WARNING.NONE;
#if UNITY_EDITOR
            this.maskWarned = false;
            this.applied = APPLIED.NONE;
            this.repaintRequested = false;
#endif
        }

        /// <summary>
        /// リクエスト処理はDecalProjectorを変更するので、カメラ毎の描画処理より前に行う
        /// DBufferではDecalRendererFeature.AddRenderPassesでDrawCall Jobが発行されるので、AddRenderPasses内で変更すると競合する
        /// </summary>
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
            // 複数のFeatureが稼働している場合は先頭のFeatureの設定を使用する
            if (!FakeShadowManager.IsOwner(this))
                return;
            FakeShadowManager.SetShader(this.fakeShadowShader);
            FakeShadowManager.SetDefaultColor(this.shadowColor);

#if UNITY_EDITOR
            this.repaintRequested = false;
            this.applied = this.JudgeState(this.cachedTechnique);
#endif
            // DecalRendererFeatureが無い（未判定を含む）場合は何もしない（判定はAddRenderPassesで更新）
            if (this.cachedTechnique == DecalTechnique.Invalid)
                return;
            // 非対応のTechnique、またはUse Rendering Layers無効の場合は稼働中の影を一時停止する（設定が戻ると自動で復帰する）
            // NOTE: 停止しただけではDecalProjectorと前回のDecal Mapが残り、止まった影が描画され続ける
            //       DBufferではAddRenderPassesでDrawCall Jobが発行されるので、検出したAddRenderPassesではなく次の描画前に行う
            // Shadow Caster Layerが未設定の場合も同様（Shadow Mesh用の内部RendererのLayerを決められない）
            if (!this.IsSupported(this.cachedTechnique)) {
                FakeShadowManager.SuspendAll();
                return;
            }

            FakeShadowManager.SetShadowCasterLayer(this.shadowCasterLayer); // 変更時は全Shadowを再設定するのでSetAvailableCountより先に行う
            FakeShadowManager.SetAvailableCount(this.maxShadowCount, this.decalMapSize); // 有効数の変更、ランタイムで変更したい場合は要改変、実行数より有効数を減らされた場合の例外対応に注意
            FakeShadowManager.SetUserValueShift(this.userValueShift);
            FakeShadowManager.SetReceiverRenderingLayers(this.receiverRenderingLayers);
            FakeShadowManager.ResolveRequests(); // リクエスト処理
#if UNITY_EDITOR
            // Edit ModeではLateUpdateが毎フレーム呼ばれないので描画前に更新する
            if (!Application.isPlaying)
                FakeShadowManager.UpdateShadows();
#endif
        }

        /// <summary>
        /// FakeShadowを稼働できる設定か (Technique判定済みの場合)
        /// </summary>
        private bool IsSupported(DecalTechnique technique) {
            return technique != DecalTechnique.GBuffer && this.cachedDecalLayers && this.shadowCasterLayer.value != 0;
        }

#if UNITY_EDITOR
        private APPLIED JudgeState(DecalTechnique technique) {
            if (technique == DecalTechnique.Invalid)
                return APPLIED.NONE;
            return this.IsSupported(technique) ? APPLIED.ACTIVE : APPLIED.SUSPENDED;
        }
#endif

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            var technique = this.GetDecalTechnique(renderer);
#if UNITY_EDITOR
            this.RequestRepaintIfChanged(this.JudgeState(technique));
#endif
            if (technique == DecalTechnique.Invalid)
                return;
            if (this.UpdateWarning(WARNING.GBUFFER, technique == DecalTechnique.GBuffer, "FakeShadowPassFeature does not support Decal GBuffer technique (Deferred). FakeShadow is disabled."))
                return;
            // CharacterはDecal LayersでDecal Shadowから除外する
            if (this.UpdateWarning(WARNING.NO_DECAL_LAYERS, !this.cachedDecalLayers, "FakeShadowPassFeature requires \"Use Rendering Layers\" of the Decal Renderer Feature. FakeShadow is disabled."))
                return;
            if (this.UpdateWarning(WARNING.NO_CASTER_LAYER, this.shadowCasterLayer.value == 0, "FakeShadowPassFeature: \"Shadow Caster Layer\" is not set. FakeShadow is disabled."))
                return;
#if UNITY_EDITOR
            this.ValidateLayerMask(renderer);
#endif
            // 描画は行うが、Max Shadow Count等の設定は先頭のFeatureのものが使われる
            this.UpdateWarning(WARNING.NOT_OWNER, !FakeShadowManager.IsOwner(this), "FakeShadowPassFeature: multiple FakeShadowPassFeatures are active. The settings of the first one are used.");

            // support to modify in runtime
            this.shadowPass.layerMask = this.shadowCasterLayer;
            this.shadowPass.decalMapSize = this.decalMapSize;
            this.shadowPass.overrideMaterial = FakeShadowManager.material;
            // Materialを差し替えずに描画するとCharacterそのものがDecal Mapに描かれる
            if (this.UpdateWarning(WARNING.NO_SHADER, this.shadowPass.overrideMaterial == null, "FakeShadowPassFeature: \"Fake Shadow Shader\" is not set. FakeShadow is disabled."))
                return;

            // Decal Map生成(AfterRenderingShadows)はDecal Pass (DBuffer: AfterRenderingPrePasses + 1 / Screen Space: AfterRenderingSkybox) より前に行う
            renderer.EnqueuePass(this.shadowPass);
        }
    }
}
