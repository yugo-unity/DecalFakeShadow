
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

    internal class FakeShadowManager {
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
        #endregion


        #region MEMBER
        private static Stack<FakeShadow> requests = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static FakeShadowManager instance = null;
        // Character毎のデータ（インデックスはRenderer User Valueの値 - 1）
        private static Matrix4x4[] s_Matrices = new Matrix4x4[SHADOW_LIMIT]; // Projection * View
        private static Vector4[] s_Offsets = new Vector4[SHADOW_LIMIT];      // xy: グリッド位置, z: Clip有効
        private static Vector4[] s_ClipRects = new Vector4[SHADOW_LIMIT];
        private Dictionary<FakeShadow, int> available = new Dictionary<FakeShadow, int>(SHADOW_LIMIT, FakeShadowReferenceComparer.instance);
        private Stack<FakeShadow> cancels = new Stack<FakeShadow>(SHADOW_LIMIT);
        private Stack<int> indexStack = new Stack<int>(SHADOW_LIMIT);
        private MatParam[] matParams = new MatParam[SHADOW_LIMIT];
        private Material _material = null;
        private static Color s_Color = Color.gray; // Managerの生成前後に依らず保持する
        private int availableCount = 0;
        private float mapSize = 1f;
        private int userValueShift = 0;
        private RenderingLayerMask receiverRenderingLayers = 1u; // Default
        #endregion


        #region MAIN FUNCTION
        // NOTE: requestsはRenderPassの生成より先にMonoBehaviourのOnEnableが飛ぶのでstaticで保持する
        // Domain Reloadなしでは前回のPlay Mode/Edit Modeのリクエストが残るが、ResolveRequestsで破棄済み・キャンセル済みを読み捨てるのでクリアしない
        // (Edit Modeで稼働中のFakeShadowはScene Reloadなしだと再OnEnableされないので、ここでクリアすると取りこぼす)

        /// <summary>
        /// 初期化
        /// </summary>
        /// <param name="fakeShader">Fake Shadow描画用のShader (overrideMaterialとShadow Meshで共通)</param>
        internal FakeShadowManager(Shader fakeShader) {
            if (instance != null) {
                Debug.LogError("FakeShadowManager is duplicated.");
                return;
            }
            instance = this;

            if (fakeShader != null) {
                this._material = new Material(fakeShader);
                this._material.hideFlags = HideFlags.HideAndDontSave;
            }

            Shader.SetGlobalColor(PROP_ID_COLOR, s_Color);
            Shader.SetGlobalInteger(PROP_ID_USER_VALUE_SHIFT, this.userValueShift);
            this.availableCount = 0;
            this.mapSize = 1;
        }

        /// <summary>
        /// 共通Material
        /// </summary>
        internal Material material { get => this._material; }

        /// <summary>
        /// 破棄
        /// </summary>
        internal void Dispose() {
            // Editor上でRenderer設定を変更するとRendererが再生成されFeatureのDispose/Createが走る
            // 稼働中のShadowは停止せずリクエストに戻し、再生成後のManagerで再設定させる
            foreach (var shadow in this.available.Keys) {
                if (shadow == null)
                    continue;
                shadow.Suspend();
                if (!ContainsRequest(shadow))
                    requests.Push(shadow);
            }
            this.available.Clear();
            this.indexStack.Clear();
            Object.DestroyImmediate(this._material);
            this._material = null;
            instance = null;
        }

        /// <summary>
        /// 上限設定
        /// </summary>
        /// <param name="count">上限値</param>
        /// <param name="decalMapSize">Decal Shadowバッファの解像度</param>
        internal void SetAvailableCount(int count, float decalMapSize) {
            if (this.availableCount == count && this.mapSize == decalMapSize)
                return;

            this.availableCount = count;
            this.mapSize = decalMapSize;

            var line = Mathf.Ceil(Mathf.Sqrt(count)); // 累乗でグリッド生成
            var uvScale = 1f / line; // 0~1
            var block = 2f / line;    // -1~1
            var onePixel = 1f / decalMapSize;
            var onePixelx2 = 2f / decalMapSize;

            this.indexStack.Clear();
            for (var i = 0; i < this.availableCount; ++i) {
                // 描画位置、グリッドの中点
                s_Offsets[i].x = -1f + block * ((float)i % line + 0.5f);
                s_Offsets[i].y = 1f - block * (Mathf.Floor((float)i / line) + 0.5f);
                var uvBias = new Vector2(uvScale * Mathf.Floor((float)i % line), uvScale * Mathf.Floor((float)i / line));
                // Bilinerによってはみ出してくるので1pix内側で切る
                s_ClipRects[i] = new Vector4(uvBias.x + onePixel, uvBias.y + onePixel, uvScale - onePixelx2, uvScale - onePixelx2);

                this.indexStack.Push(i);
                this.matParams[i].index = i;
                this.matParams[i].uvScale = new Vector2(uvScale, uvScale);
                this.matParams[i].uvBias = uvBias;
            }

            // 現在有効なShadowの更新
            if (this.available.Count > 0) {
                foreach (var shadow in this.available.Keys) {
                    if (requests.Count >= this.availableCount) {
                        Debug.LogError($"Canceled FakeShadow [{shadow.name}]. The max count is insufficient.");
                        this.cancels.Push(shadow);
                        continue;
                    }
                    requests.Push(shadow);
                }
                this.available.Clear();
                // インデックスはindexStackの再構築で回収済みなので返却しない
                foreach (var shadow in this.cancels)
                    shadow.Cancel();
                this.cancels.Clear();
            }

            Shader.SetGlobalFloat(PROP_ID_LINE, line);
        }

        /// <summary>
        /// Fake Shadowを受けるRendering Layer Maskの設定 (Decal ProjectorのRendering Layer Maskを上書きする)
        /// </summary>
        internal void SetReceiverRenderingLayers(RenderingLayerMask mask) {
            if (this.receiverRenderingLayers.value == mask.value)
                return;

            this.receiverRenderingLayers = mask;
            foreach (var shadow in this.available.Keys)
                shadow.ChangeReceiverRenderingLayers(mask);
        }

        /// <summary>
        /// User Valueのbit位置の設定
        /// </summary>
        /// <param name="shift">User Valueの何bit目から8bitを使うか</param>
        internal void SetUserValueShift(int shift) {
            shift = Mathf.Clamp(shift, 0, USER_VALUE_SHIFT_MAX);
            if (this.userValueShift == shift)
                return;

            this.userValueShift = shift;
            foreach (var shadow in this.available.Keys)
                shadow.ChangeUserValueShift(shift);
            Shader.SetGlobalInteger(PROP_ID_USER_VALUE_SHIFT, shift);
        }

        /// <summary>
        /// 新規リクエストの対応
        /// </summary>
        internal void ResolveRequests() {
            while (requests.Count > 0) {
                var req = requests.Pop();
                // 破棄済み・キャンセルされた場合は無視
                if (req == null || !req.isRequested)
                    continue;
                if (this.available.ContainsKey(req)) {
                    Debug.LogError($"Already available : {req.name}");
                    continue;
                }
                if (this.indexStack.Count == 0) {
                    Debug.LogError($"Over requests : {req.name}");
                    req.Cancel();
                    continue;
                }
                // SkinnedMeshはMeshRendererを分けずにSubMeshの方がオーバーヘッドが低いのでMulti Renderer対応はしない
                if (req.isShadowMesh && this._material == null) {
                    Debug.LogError("Not supported FakeShadow by ShadowMesh. Need the Shader.");
                    req.Cancel();
                    continue;
                }

                var index = this.indexStack.Pop();
                this.available.Add(req, index);

                this.matParams[index].material = this._material;
                this.matParams[index].userValueShift = this.userValueShift;
                this.matParams[index].receiverRenderingLayers = this.receiverRenderingLayers;
                req.Setup(in this.matParams[index]);
            }
        }

        /// <summary>
        /// 稼働中のShadowのパラメータ更新 (Edit Mode用、Play ModeではFakeShadow.LateUpdateで行う)
        /// </summary>
        internal void UpdateShadows() {
            foreach (var shadow in this.available.Keys) {
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
        /// <param name="index">Requestで取得したインデックス</param>
        public static bool Return(FakeShadow shadow) {
            if (instance == null || shadow == null)
                return false;
            if (instance.available.TryGetValue(shadow, out var index)) {
                instance.indexStack.Push(index);
                instance.available.Remove(shadow);
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
        [Tooltip("Fake Shadowを落とすオブジェクトのLayer。Use Rendering Layers無効時はDecalより後に描画して上書きする対象にもなる")]
        public LayerMask shadowCasterLayer = 0;
        [Tooltip("Fake Shadowの描画に使うShader (UTJ/FakeShadowCaster)。Characterのマテリアルを差し替えて描画する")]
        public Shader fakeShadowShader = null;
        [Range(0, FakeShadowManager.USER_VALUE_SHIFT_MAX), Tooltip("RendererのUser Value(32bit)のうち、Fake Shadowが使う8bitの開始位置")]
        public int userValueShift = 0;
        [Tooltip("Fake Shadowを受けるRendering Layer。FakeShadowが管理するDecal ProjectorのRendering Layer Maskを上書きする (DecalRendererFeatureのUse Rendering Layersが有効な場合のみ機能する)")]
        public RenderingLayerMask receiverRenderingLayers = 1u; // Default
        [Range(1, FakeShadowManager.SHADOW_LIMIT)]
        public int maxShadowCount = 9; // 3x3
        public int decalMapSize = 512;

        private FakeShadowManager fakeShadow = null;
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

                var format = QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                var desc = new RenderTextureDescriptor(this.decalMapSize, this.decalMapSize, format, 0, 0);
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
        }
        private WARNING warned = WARNING.NONE;
#if UNITY_EDITOR
        // privateの為Reflectionで取得
        static readonly System.Reflection.FieldInfo s_FieldCopyDepthMode = typeof(UniversalRenderer).GetField("m_CopyDepthMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private int maskWarnState = 0;
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
        /// Use Rendering LayersとOpaque Layer Maskの不整合の警告
        /// </summary>
        private void ValidateLayerMask(ScriptableRenderer renderer) {
            if (!(renderer is UniversalRenderer universalRenderer))
                return;
            var opaqueMask = universalRenderer.opaqueLayerMask.value;
            var state = 0;
            if (this.cachedDecalLayers && (opaqueMask & this.shadowCasterLayer.value) != this.shadowCasterLayer.value)
                state = 1;
            else if (!this.cachedDecalLayers && (opaqueMask & this.shadowCasterLayer.value) != 0)
                state = 2;
            if (state == this.maskWarnState)
                return;
            this.maskWarnState = state;
            if (state == 1)
                Debug.LogWarning($"FakeShadowPassFeature: \"Use Rendering Layers\" is enabled, include Shadow Caster Layer in \"Opaque Layer Mask\" and \"Prepass Layer Mask\" of the renderer. (Opaque Layer Mask: 0x{opaqueMask:X})");
            else if (state == 2)
                Debug.LogWarning("FakeShadowPassFeature: \"Use Rendering Layers\" is disabled, Shadow Caster Layer is drawn twice. Exclude Shadow Caster Layer from \"Opaque Layer Mask\" of the renderer.");
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
            this.fakeShadow = new FakeShadowManager(this.fakeShadowShader);
            RenderPipelineManager.beginContextRendering += this.OnBeginContextRendering;
        }

        protected override void Dispose(bool disposing) {
            RenderPipelineManager.beginContextRendering -= this.OnBeginContextRendering;
            this.shadowPass?.Dispose();
            this.shadowPass = null;
            this.opaquePass = null;
            this.fakeShadow?.Dispose();
            this.fakeShadow = null;
            this.cachedRenderer = null;
            this.warned = WARNING.NONE;
#if UNITY_EDITOR
            this.maskWarnState = 0;
#endif
        }

        /// <summary>
        /// リクエスト処理はDecalProjectorを変更するので、カメラ毎の描画処理より前に行う
        /// DBufferではDecalRendererFeature.AddRenderPassesでDrawCall Jobが発行されるので、AddRenderPasses内で変更すると競合する
        /// </summary>
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras) {
            // DecalRendererFeatureが無い、または非対応のTechniqueの場合は何もしない（判定はAddRenderPassesで更新）
            if (this.cachedTechnique == DecalTechnique.Invalid || this.cachedTechnique == DecalTechnique.GBuffer)
                return;

            this.fakeShadow.SetAvailableCount(this.maxShadowCount, this.decalMapSize); // 有効数の変更、ランタイムで変更したい場合は要改変、実行数より有効数を減らされた場合の例外対応に注意
            this.fakeShadow.SetUserValueShift(this.userValueShift);
            this.fakeShadow.SetReceiverRenderingLayers(this.receiverRenderingLayers);
            this.fakeShadow.ResolveRequests(); // リクエスト処理
#if UNITY_EDITOR
            // Edit ModeではLateUpdateが毎フレーム呼ばれないので描画前に更新する
            if (!Application.isPlaying)
                this.fakeShadow.UpdateShadows();
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

            // support to modify in runtime
            this.shadowPass.layerMask = this.shadowCasterLayer;
            this.shadowPass.decalMapSize = this.decalMapSize;
            this.shadowPass.overrideMaterial = this.fakeShadow.material;
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
            // ATTENTION: Not supported the case that chaging DepthPrimingMode in runtime when available shadows exist.
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
