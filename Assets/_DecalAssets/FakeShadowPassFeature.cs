
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
    ///   - Shaderのグローバル値はDomain Reloadを跨いで残るので、最初のFeature登録時に設定し直す
    /// </summary>
    internal static class FakeShadowManager {
        #region DEFINE
        public struct MatParam {
            public int index;
            public Vector2 uvScale;
            public Vector2 uvBias;
            public Material material;   // Shadow Mesh用の共通Material
            public int userValueShift;
            public RenderingLayerMask receiverRenderingLayers; // Decal ProjectorのRendering Layer Mask
        }

        public const int SHADOW_LIMIT = 25; // 1024の16分割で256を限界値として想定、FakeShadowCaster.shaderのFAKE_SHADOW_LIMITと合わせる
        public const int USER_VALUE_SHIFT_MAX = 24; // User Valueの32bit中、8bitを使用する
        public static readonly int PROP_ID_LINE = Shader.PropertyToID("_FakeShadowLine");
        public static readonly int PROP_ID_COLOR = Shader.PropertyToID("_FakeShadowColor");
        public static readonly int PROP_ID_USER_VALUE_SHIFT = Shader.PropertyToID("_FakeShadowUserValueShift");
        public static readonly int PROP_ID_MATRICES = Shader.PropertyToID("_FakeShadowMatrices");
        public static readonly int PROP_ID_OFFSETS = Shader.PropertyToID("_FakeShadowOffsets");
        public static readonly int PROP_ID_CLIP_RECTS = Shader.PropertyToID("_FakeShadowClipRects");
        public static readonly Quaternion TOP_ROT = Quaternion.Euler(90f, 0f, 0f);
        private static readonly Color DEFAULT_COLOR = Color.gray;
        #endregion


        #region MEMBER
        // NOTE: requestsはRenderPassの生成より先にMonoBehaviourのOnEnableが飛ぶのでFeatureに依らず保持する
        private static readonly Stack<FakeShadow> requests = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static readonly Dictionary<FakeShadow, int> available = new Dictionary<FakeShadow, int>(SHADOW_LIMIT, FakeShadowReferenceComparer.instance);
        private static readonly Stack<FakeShadow> cancels = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static readonly Stack<int> indexStack = new Stack<int>(SHADOW_LIMIT);
        private static readonly MatParam[] matParams = new MatParam[SHADOW_LIMIT];
        // Character毎のデータ（インデックスはRenderer User Valueの値 - 1）
        private static readonly Matrix4x4[] s_Matrices = new Matrix4x4[SHADOW_LIMIT]; // Projection * View
        private static readonly Vector4[] s_Offsets = new Vector4[SHADOW_LIMIT];      // xy: グリッド位置, z: Clip有効
        private static readonly Vector4[] s_ClipRects = new Vector4[SHADOW_LIMIT];
        // 登録中のFeature、先頭のFeatureの設定を使用する
        private static readonly List<ScriptableRendererFeature> owners = new List<ScriptableRendererFeature>(1);
        private static Material s_Material = null;
        private static Color s_Color = DEFAULT_COLOR;
        private static Color s_DefaultColor = DEFAULT_COLOR; // FakeShadowPassFeatureのGUIで設定された色
        private static int availableCount = 0; // 0は未設定 (次のSetAvailableCountで全Shadowを再設定する)
        private static float mapSize = 1f;
        private static int userValueShift = 0;
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
            if (owners.Count == 0) {
                Shader.SetGlobalColor(PROP_ID_COLOR, s_Color);
                Shader.SetGlobalInteger(PROP_ID_USER_VALUE_SHIFT, userValueShift);
            }
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

            var line = Mathf.Ceil(Mathf.Sqrt(count)); // 累乗でグリッド生成
            var uvScale = 1f / line; // 0~1
            var block = 2f / line;    // -1~1
            var onePixel = 1f / decalMapSize;
            var onePixelx2 = 2f / decalMapSize;

            indexStack.Clear();
            for (var i = 0; i < availableCount; ++i) {
                // 描画位置、グリッドの中点
                s_Offsets[i].x = -1f + block * ((float)i % line + 0.5f);
                s_Offsets[i].y = 1f - block * (Mathf.Floor((float)i / line) + 0.5f);
                var uvBias = new Vector2(uvScale * Mathf.Floor((float)i % line), uvScale * Mathf.Floor((float)i / line));
                // Bilinerによってはみ出してくるので1pix内側で切る
                s_ClipRects[i] = new Vector4(uvBias.x + onePixel, uvBias.y + onePixel, uvScale - onePixelx2, uvScale - onePixelx2);

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

            Shader.SetGlobalFloat(PROP_ID_LINE, line);
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
        /// <param name="shift">User Valueの何bit目から8bitを使うか</param>
        internal static void SetUserValueShift(int shift) {
            shift = Mathf.Clamp(shift, 0, USER_VALUE_SHIFT_MAX);
            if (userValueShift == shift)
                return;

            userValueShift = shift;
            foreach (var shadow in available.Keys) {
                if (shadow != null)
                    shadow.ChangeUserValueShift(shift);
            }
            Shader.SetGlobalInteger(PROP_ID_USER_VALUE_SHIFT, shift);
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

                var index = indexStack.Pop();
                available.Add(req, index);

                matParams[index].material = s_Material;
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
        /// Character毎のデータをグローバル配列で設定
        /// </summary>
        internal static void SetGlobalShadowData() {
            Shader.SetGlobalMatrixArray(PROP_ID_MATRICES, s_Matrices);
            Shader.SetGlobalVectorArray(PROP_ID_OFFSETS, s_Offsets);
            Shader.SetGlobalVectorArray(PROP_ID_CLIP_RECTS, s_ClipRects);
        }

        internal static void SetShadowMatrix(int index, in Matrix4x4 viewProj) {
            s_Matrices[index] = viewProj;
        }

        internal static void SetShadowClip(int index, bool clip) {
            s_Offsets[index].z = clip ? 1f : 0f;
        }
        #endregion


        #region PUBLIC FUNCTION
        /// <summary>
        /// 影色
        /// </summary>
        public static Color color {
            get { return s_Color; }
            set {
                s_Color = value;
                Shader.SetGlobalColor(PROP_ID_COLOR, value);
            }
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
                return true;
            }
            Debug.LogError("Not found to return");
            return false;
        }
        #endregion
    }
    #endregion

    /// <summary>
    /// Shadow Caster Layer(Character)をDecal Shadowから除外する方法はDecalRendererFeatureの"Use Rendering Layers"で切り替わる
    ///   - 有効: CharacterはRenderer標準のPrepass/Opaque Passで描画する（Opaque/Prepass Layer MaskにShadow Caster Layerを含めること）
    ///           Characterの Rendering Layer Mask を Receiver Rendering Layers から外す
    ///   - 無効: CharacterをDecalより後に描画して上書きする（Opaque Layer MaskからShadow Caster Layerを外すこと）
    /// Decal RendererFeatureのTechnique(Screen Space / DBuffer)は手動で切り替える（ランタイムでの切替は非対応、Editor上での切替は可）
    /// </summary>
    public class FakeShadowPassFeature : ScriptableRendererFeature {
        [Tooltip("Layers that cast fake shadows.")]
        public LayerMask shadowCasterLayer = 0;
        [Tooltip("Shader that draws casters into the decal map (UTJ/FakeShadowCaster).")]
        public Shader fakeShadowShader = null;
        [Range(0, FakeShadowManager.USER_VALUE_SHIFT_MAX), Tooltip("Start bit of the 8 bits FakeShadow uses in the renderer's User Value.")]
        public int userValueShift = 0;
        [Tooltip("Rendering layers that receive fake shadows. Requires Use Rendering Layers of the Decal Renderer Feature.")]
        public RenderingLayerMask receiverRenderingLayers = 1u; // Default
        [Tooltip("Shadow color, multiplied by the Base Color of the DecalProjector's material. Alpha sets the opacity.")]
        public Color shadowColor = Color.gray;
        [Range(1, FakeShadowManager.SHADOW_LIMIT), Tooltip("Maximum number of fake shadows at the same time.")]
        public int maxShadowCount = 9; // 3x3
        [Tooltip("Resolution of the decal map shared by all fake shadows.")]
        public int decalMapSize = 512;

        private CharacterShadowPass shadowPass = null;
        private CharacterOpaquePass opaquePass = null;


        #region RENDER GRAPH COMMON
        /// <summary>
        /// RendererListを描画するだけのRaster Pass共通データ
        /// </summary>
        class DrawPassData {
            public RendererListHandle rendererList;
        }

        static readonly ShaderTagId[] s_ShaderTagValues = new ShaderTagId[1];
        static readonly RenderStateBlock[] s_RenderStateBlocks = new RenderStateBlock[1];

        /// <summary>
        /// RenderStateBlockを全体に適用するRendererListの生成 (RenderingUtils.CreateRendererListWithRenderStateBlockがinternalの為)
        /// </summary>
        static RendererListHandle CreateRendererList(RenderGraph renderGraph, in CullingResults cullResults, in DrawingSettings ds, in FilteringSettings fs, in RenderStateBlock rsb) {
            if (rsb.mask == RenderStateMask.Nothing)
                return renderGraph.CreateRendererList(new RendererListParams(cullResults, ds, fs));

            s_ShaderTagValues[0] = ShaderTagId.none;
            s_RenderStateBlocks[0] = rsb;
            var param = new RendererListParams(cullResults, ds, fs) {
                tagValues = new NativeArray<ShaderTagId>(s_ShaderTagValues, Allocator.Temp),
                stateBlocks = new NativeArray<RenderStateBlock>(s_RenderStateBlocks, Allocator.Temp),
                isPassTagName = false
            };
            return renderGraph.CreateRendererList(param);
        }
        #endregion


        #region FAKE SHADOW PASS
        /// <summary>
        /// CharacterレイヤーのFake Shadow pass
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

            public CharacterShadowPass() {
                // 他のPassに依存しないので、Main/Additional LightのShadow Map生成と連続させてRender Targetの切り替えを抑える
                this.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Fake Shadow - Shadow Pass");
#endif
            }

            public void Dispose() {
                this.decalMap?.Release();
                this.decalMap = null;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();

                // Decal Mapは影の被覆率(0/1)のみを持ち、色はFakeShadowByDecalがグローバルの_FakeShadowColorから取る
                // NOTE: 色をDecal Mapに書くとクリア色(0,0,0,0)とのBilinear補間でエッジのRGBが黒に寄り、Decalのアルファブレンドと二重に掛かって暗い縁が出る
                var desc = new RenderTextureDescriptor(this.decalMapSize, this.decalMapSize, GraphicsFormat.R8_UNorm, 0, 0);
                desc.msaaSamples = 1;
                RenderingUtils.ReAllocateHandleIfNeeded(ref this.decalMap, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_DecalTexture");
                var decalMapHandle = renderGraph.ImportTexture(this.decalMap);

                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(this.passName, out var passData, this.profilingSampler)) {
                    // DecalをGPU Instancingする為にDecal Map一つにグリッドで描画する
                    // 共通Materialで描画し、Character毎のデータはRenderer User Valueのインデックスでグローバル配列から引く（SRP Batcherで1バッチになる）
                    var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_IDS, renderingData, cameraData, lightData, SortingCriteria.OptimizeStateChanges);
                    drawSettings.overrideMaterial = this.overrideMaterial;
                    drawSettings.overrideMaterialPassIndex = 0;
                    drawSettings.perObjectData = PerObjectData.None;
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    passData.rendererList = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawSettings, filteringSettings));

                    builder.UseRendererList(passData.rendererList);
                    builder.SetRenderAttachment(decalMapHandle, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(decalMapHandle, DECAL_MAP_ID);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<DrawPassData>(static (data, context) => {
                        context.cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1f, 0);
                        context.cmd.DrawRendererList(data.rendererList);
                    });
                }
            }
        }
        #endregion


        #region OPAQUE PASS
        /// <summary>
        /// CharacterレイヤーのOpaque pass (Use Rendering Layers無効時のみ)
        /// Decalの後に描画してDecal Shadowを上書きする
        /// </summary>
        class CharacterOpaquePass : ScriptableRenderPass {
            public LayerMask layerMask = 0;
            public bool useDepthPriming = false;
            public bool disableDBuffer = false;

            class OpaquePassData : DrawPassData {
                public bool disableDBuffer;
            }

            private static readonly ShaderTagId SHADER_TAG_ID = new ShaderTagId("UniversalForward");
            private RenderStateBlock renderStateBlock;
            // DBufferのKeywordはカメラ終了時まで有効なままなので、上書き時は明示的に無効化する
            private static readonly GlobalKeyword[] s_DBufferKeywords = {
                GlobalKeyword.Create("_DBUFFER_MRT1"),
                GlobalKeyword.Create("_DBUFFER_MRT2"),
                GlobalKeyword.Create("_DBUFFER_MRT3"),
            };

            public CharacterOpaquePass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingSkybox; // after DecalPass
                this.renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Fake Shadow - Opaque Pass");
#endif
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
                var resourceData = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();

                if (this.useDepthPriming) {
                    this.renderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                    this.renderStateBlock.mask |= RenderStateMask.Depth;
                } else if (this.renderStateBlock.depthState.compareFunction == CompareFunction.Equal) {
                    this.renderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    this.renderStateBlock.mask |= RenderStateMask.Depth;
                }

                using (var builder = renderGraph.AddRasterRenderPass<OpaquePassData>(this.passName, out var passData, this.profilingSampler)) {
                    var sortFlags = cameraData.defaultOpaqueSortFlags;
                    if (this.useDepthPriming)
                        sortFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges;
                    var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_ID, renderingData, cameraData, lightData, sortFlags);
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    passData.rendererList = CreateRendererList(renderGraph, renderingData.cullResults, drawSettings, filteringSettings, this.renderStateBlock);
                    passData.disableDBuffer = this.disableDBuffer;

                    builder.UseRendererList(passData.rendererList);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    if (this.disableDBuffer)
                        builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc<OpaquePassData>(static (data, context) => {
                        if (data.disableDBuffer) {
                            // 以降のTransparentはDBufferを参照しないので戻さない（URPもカメラ終了時に無効化する）
                            foreach (var keyword in s_DBufferKeywords)
                                context.cmd.SetKeyword(keyword, false);
                        }
                        context.cmd.DrawRendererList(data.rendererList);
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
        }
        private WARNING warned = WARNING.NONE;
#if UNITY_EDITOR
        // privateの為Reflectionで取得
        static readonly System.Reflection.FieldInfo s_FieldCopyDepthMode = typeof(UniversalRenderer).GetField("m_CopyDepthMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private int maskWarnState = 0;
        private bool prepassWarned = false;
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
        /// Use Rendering Layers・Depth PrimingとOpaque/Prepass Layer Maskの不整合の警告
        /// </summary>
        private void ValidateLayerMask(ScriptableRenderer renderer) {
            if (!(renderer is UniversalRenderer universalRenderer))
                return;
            var casterMask = this.shadowCasterLayer.value;
            var opaqueMask = universalRenderer.opaqueLayerMask.value;
            // NOTE: prepassLayerMaskはCompatibility ModeではOpaque Layer Maskになる、実際にPrepassで使われる値を参照する
            var prepassMask = universalRenderer.prepassLayerMask.value;
            var state = 0;
            if (this.cachedDecalLayers && ((opaqueMask & casterMask) != casterMask || (prepassMask & casterMask) != casterMask))
                state = 1;
            else if (!this.cachedDecalLayers && (opaqueMask & casterMask) != 0)
                state = 2;
            if (state != this.maskWarnState) {
                this.maskWarnState = state;
                if (state == 1)
                    Debug.LogWarning($"FakeShadowPassFeature: \"Use Rendering Layers\" is enabled, include Shadow Caster Layer in \"Opaque Layer Mask\" and \"Prepass Layer Mask\" of the renderer. (Opaque Layer Mask: 0x{opaqueMask:X}, Prepass Layer Mask: 0x{prepassMask:X})");
                else if (state == 2)
                    Debug.LogWarning("FakeShadowPassFeature: \"Use Rendering Layers\" is disabled, Shadow Caster Layer is drawn twice. Exclude Shadow Caster Layer from \"Opaque Layer Mask\" of the renderer.");
            }

            // Use Rendering Layers無効時のCharacterOpaquePassは、Depth Priming有効時にPrepassの深度に対してZTest Equalで描画する
            var prepassMissing = !this.cachedDecalLayers && universalRenderer.useDepthPriming && (prepassMask & casterMask) != casterMask;
            if (prepassMissing != this.prepassWarned) {
                this.prepassWarned = prepassMissing;
                if (prepassMissing)
                    Debug.LogWarning($"FakeShadowPassFeature: Depth Priming is enabled, include Shadow Caster Layer in \"Prepass Layer Mask\" of the renderer, or Shadow Casters are not drawn. (Prepass Layer Mask: 0x{prepassMask:X})");
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
            this.opaquePass = new CharacterOpaquePass();
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
            this.opaquePass = null;
            // 稼働中のShadowはManagerが保持し、Renderer再生成後のFeatureで再設定される
            FakeShadowManager.Detach(this);
            this.cachedRenderer = null;
            this.warned = WARNING.NONE;
#if UNITY_EDITOR
            this.maskWarnState = 0;
            this.prepassWarned = false;
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

            // DecalRendererFeatureが無い、または非対応のTechniqueの場合は何もしない（判定はAddRenderPassesで更新）
            if (this.cachedTechnique == DecalTechnique.Invalid || this.cachedTechnique == DecalTechnique.GBuffer)
                return;

            FakeShadowManager.SetAvailableCount(this.maxShadowCount, this.decalMapSize); // 有効数の変更、ランタイムで変更したい場合は要改変、実行数より有効数を減らされた場合の例外対応に注意
            FakeShadowManager.SetUserValueShift(this.userValueShift);
            FakeShadowManager.SetReceiverRenderingLayers(this.receiverRenderingLayers);
            FakeShadowManager.ResolveRequests(); // リクエスト処理
#if UNITY_EDITOR
            // Edit ModeではLateUpdateが毎フレーム呼ばれないので描画前に更新する
            if (!Application.isPlaying)
                FakeShadowManager.UpdateShadows();
#endif
            FakeShadowManager.SetGlobalShadowData();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            var technique = this.GetDecalTechnique(renderer);
            if (technique == DecalTechnique.Invalid)
                return;
            if (this.UpdateWarning(WARNING.GBUFFER, technique == DecalTechnique.GBuffer, "FakeShadowPassFeature does not support Decal GBuffer technique (Deferred). FakeShadow is disabled."))
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

            // Use Rendering Layers有効時はDecal LayersでCharacterを除外するので追加のPassは不要
            if (this.cachedDecalLayers)
                return;

            // Decalを利用するのでDepth Textureは必ずあるという前提
            // 本サンプルはDepth Priming有効で期待しているので決め打ちにしてもいい
            // Depth Primingは毎フレーム判定するので、ランタイムでのDepthPrimingModeの切替にも追従する
            // ATTENTION: Depth Priming有効時はPrepassの深度に対してZTest Equalで描画するので、
            //            RendererのPrepass Layer MaskにShadow Caster Layerを含めること（含めないとCharacterが描画されない）
            var useDepthPriming = renderer.useDepthPriming;

#if UNITY_EDITOR
            // Depth Priming Modeが無効かつCopyDepthの為にDepthPrepassを走らせるとイベントが正常に差し込めないので非対応
            var depthPrepass = useDepthPriming | (CopyDepthMode)s_FieldCopyDepthMode.GetValue(renderer) == CopyDepthMode.ForcePrepass;
            if (!useDepthPriming && depthPrepass)
                Debug.LogError("not supported \"Depth Texture Mode\" to \"Force Prepass\" in URP Asset");
#endif

            this.opaquePass.layerMask = this.shadowCasterLayer;

            this.opaquePass.useDepthPriming = useDepthPriming;
            this.opaquePass.disableDBuffer = technique == DecalTechnique.DBuffer;

            renderer.EnqueuePass(this.opaquePass);
        }
    }
}
