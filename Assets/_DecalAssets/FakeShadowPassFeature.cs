
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
    internal class FakeShadowManager {
        #region DEFINE
        public struct MatParam {
            public int index;
            public Vector2 uvScale;
            public Vector4 offset;
            public Vector2 uvBias;
            public Vector4 clipRect;
            public Material material;
        }

        public const int SHADOW_LIMIT = 25; // 1024の16分割で256を限界値として想定
        public const string CLIP_RECT_KEYWORD = "FAKE_CLIP";
        public static readonly int PROP_ID_LINE = Shader.PropertyToID("_FakeShadowLine");
        public static readonly int PROP_ID_OFFSET = Shader.PropertyToID("_FakeShadowOffset");
        public static readonly int PROP_ID_COLOR = Shader.PropertyToID("_FakeShadowColor");
        public static readonly int PROP_ID_VIEW = Shader.PropertyToID("_FakeShadowView");
        public static readonly int PROP_ID_PROJ = Shader.PropertyToID("_FakeShadowProj");
        public static readonly int PROP_ID_CLIP = Shader.PropertyToID("_FakeClipRect");
        public static readonly Quaternion TOP_ROT = Quaternion.Euler(90f, 0f, 0f);
        #endregion


        #region MEMBER
        private static Stack<FakeShadow> requests = new Stack<FakeShadow>(SHADOW_LIMIT);
        private static FakeShadowManager instance = null;
        private Dictionary<FakeShadow, int> available = new Dictionary<FakeShadow, int>(SHADOW_LIMIT);
        private Stack<FakeShadow> cancels = new Stack<FakeShadow>(SHADOW_LIMIT);
        private Stack<int> indexStack = new Stack<int>(SHADOW_LIMIT);
        private MatParam[] matParams = new MatParam[SHADOW_LIMIT];
        private Color _color = Color.black;
        private int availableCount = 0;
        private float mapSize = 1f;
        #endregion


        #region MAIN FUNCTION
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Reload() {
            // static変数にしたくないがEnterPlayModeを有効にするとRenderPassの生成より先にMonoBehaviourのOnEnableが飛ぶので...
            FakeShadowManager.requests.Clear();
        }

        /// <summary>
        /// 初期化
        /// </summary>
        /// <param name="fakeShader">ShadowMesh用のShader</param>
        internal FakeShadowManager(Shader fakeShader) {
            if (instance != null) {
                Debug.LogError("FakeShadowManager is duplicated.");
                return;
            }
            instance = this;

            if (fakeShader != null) {
                for (var i = 0; i < SHADOW_LIMIT; ++i)
                    this.matParams[i].material = new Material(fakeShader);
            }

            Shader.SetGlobalColor(PROP_ID_COLOR, Color.gray);
            this.availableCount = 0;
            this.mapSize = 1;
        }

        /// <summary>
        /// 破棄
        /// </summary>
        internal void Dispose() {
            for (var i = 0; i < SHADOW_LIMIT; ++i) {
                Object.DestroyImmediate(this.matParams[i].material);
                this.matParams[i].material = null;
            }
            foreach (var shadow in this.available.Keys)
                shadow.Sleep(returnShadow:false);
            this.available.Clear();
            this.indexStack.Clear();
            requests.Clear();
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
                var pos = Vector4.zero;
                pos.x = -1f + block * ((float)i % line + 0.5f);
                pos.y = 1f - block * (Mathf.Floor((float)i / line) + 0.5f);
                var uvBias = new Vector2(uvScale * Mathf.Floor((float)i % line), uvScale * Mathf.Floor((float)i / line));
                // Bilinerによってはみ出してくるので1pix内側で切る
                var clipRect = new Vector4(uvBias.x + onePixel, uvBias.y + onePixel, uvScale - onePixelx2, uvScale - onePixelx2);

                this.indexStack.Push(i);
                if (this.matParams[i].material != null) {
                    this.matParams[i].material.SetVector(PROP_ID_OFFSET, pos);
                    this.matParams[i].material.SetVector(PROP_ID_CLIP, clipRect);
                }
                this.matParams[i].index = i;
                this.matParams[i].uvScale = new Vector2(uvScale, uvScale);
                this.matParams[i].offset = pos;
                this.matParams[i].uvBias = uvBias;
                this.matParams[i].clipRect = clipRect;
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
                foreach (var shadow in this.cancels)
                    shadow.Sleep();
                this.cancels.Clear();
            }

            Shader.SetGlobalFloat(PROP_ID_LINE, line);
        }

        /// <summary>
        /// 新規リクエストの対応
        /// </summary>
        internal void ResolveRequests() {
            while (requests.Count > 0) {
                var req = requests.Pop();
                // キャンセルされた場合は無視
                if (!req.isRequested)
                    continue;
                if (this.available.ContainsKey(req)) {
                    Debug.LogError($"Already available : {req.name}");
                    continue;
                }
                if (this.indexStack.Count == 0) {
                    Debug.LogError($"Over requests : {req.name}");
                    req.Sleep();
                    continue;
                }

                var index = this.indexStack.Pop();
                this.available.Add(req, index);

                // SkinnedMeshはMeshRendererを分けずにSubMeshの方がオーバーヘッドが低いのでMulti Renderer対応はしない
                if (req.isShadowMesh) {
                    if (this.matParams[index].material == null) {
                        Return(req);
                        Debug.LogError("Not supported FakeShadow by ShadowMesh. Need the Shader.");
                        continue;
                    }
                }
                req.Setup(in this.matParams[index]);
            }
        }
        #endregion


        #region PUBLIC FUNCTION
        /// <summary>
        /// 影色
        /// </summary>
        public static Color color {
            get { return instance._color; }
            set {
                instance._color = value;
                Shader.SetGlobalColor(PROP_ID_COLOR, value);
            }
        }

        /// <summary>
        /// 有効リクエスト
        /// </summary>
        /// <param name="shadow">FakeShadowインスタンス</param>
        /// <returns>完了</returns>
        public static bool Request(FakeShadow shadow) {
            if (requests.Contains(shadow)) {
                Debug.LogError("Duplicated to request");
                return true;
            }

            requests.Push(shadow);
            return true;
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

    public class FakeShadowPassFeature : ScriptableRendererFeature {
        public LayerMask characterLayer = 0;
        public Shader fakeShadowShader = null;
        [Range(1, FakeShadowManager.SHADOW_LIMIT)]
        public int maxShadowCount = 9; // 3x3
        public int decalMapSize = 512;

        private FakeShadowManager fakeShadow = null;
        private CharacterDepthPass depthPass = null;
        private CharacterShadowPass shadowPass = null;
        private CharacterOpaquePass opaquePass = null;


        #region RENDER GRAPH COMMON
        /// <summary>
        /// RendererListを描画するだけのRaster Pass共通データ
        /// </summary>
        class DrawPassData {
            public RendererListHandle rendererList;
        }

        static void ExecuteDrawPass(DrawPassData data, RasterGraphContext context) {
            context.cmd.DrawRendererList(data.rendererList);
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


        #region DEPTH PASS
        /// <summary>
        /// CharacterレイヤーのDepth pass
        /// </summary>
        class CharacterDepthPass : ScriptableRenderPass {
            public LayerMask layerMask = 0;

            private ShaderTagId SHADER_TAG_ID = new ShaderTagId("DepthOnly");

            public CharacterDepthPass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses; //
#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Character - Depth Pass");
#endif
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
                var resourceData = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();

                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(this.passName, out var passData, this.profilingSampler)) {
                    var depthDrawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_ID, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);
                    depthDrawSettings.perObjectData = PerObjectData.None;
                    var depthFilteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    passData.rendererList = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, depthDrawSettings, depthFilteringSettings));

                    builder.UseRendererList(passData.rendererList);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    builder.SetRenderFunc<DrawPassData>(ExecuteDrawPass);
                }
            }
        }
        #endregion


        #region FAKE SHADOW PASS
        /// <summary>
        /// CharacterレイヤーのFake Shadow pass
        /// </summary>
        class CharacterShadowPass : ScriptableRenderPass {
            public LayerMask layerMask = 0;
            public int decalMapSize = 512;

            private ShaderTagId FAKE_SHADER_TAG_ID = new ShaderTagId("FakeShadow");
            private int DECAL_MAP_ID = Shader.PropertyToID("_DecalTexture");
            // DecalProjectorのPassはRender Graph上でこのTextureの利用を宣言しないので、
            // Transientにするとエイリアシングされうる。Featureで保持してImportする
            private RTHandle decalMap = null;

            public CharacterShadowPass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Character - FakeShadow Pass");
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
                    var drawSettings = RenderingUtils.CreateDrawingSettings(FAKE_SHADER_TAG_ID, renderingData, cameraData, lightData, SortingCriteria.OptimizeStateChanges);
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
        /// CharacterレイヤーのOpaque pass
        /// </summary>
        class CharacterOpaquePass : ScriptableRenderPass {
            public LayerMask layerMask = 0;
            public bool useDepthPriming = false;

            private ShaderTagId SHADER_TAG_ID = new ShaderTagId("UniversalForward");
            private RenderStateBlock renderStateBlock;

            public CharacterOpaquePass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingSkybox; // after DecalPass
                this.renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

#if ENABLE_PROFILING
                base.profilingSampler = new ProfilingSampler("Character - Opaque Pass");
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

                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(this.passName, out var passData, this.profilingSampler)) {
                    var sortFlags = cameraData.defaultOpaqueSortFlags;
                    if (this.useDepthPriming)
                        sortFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges;
                    var drawSettings = RenderingUtils.CreateDrawingSettings(SHADER_TAG_ID, renderingData, cameraData, lightData, sortFlags);
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    passData.rendererList = CreateRendererList(renderGraph, renderingData.cullResults, drawSettings, filteringSettings, this.renderStateBlock);

                    builder.UseRendererList(passData.rendererList);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    builder.SetRenderFunc<DrawPassData>(ExecuteDrawPass);
                }
            }
        }
        #endregion


        System.Reflection.PropertyInfo propUseDepthPriming = null;

        public override void Create() {
            if (this.propUseDepthPriming != null)
                return;

            this.depthPass = new CharacterDepthPass();
            this.shadowPass = new CharacterShadowPass();
            this.opaquePass = new CharacterOpaquePass();

            this.fakeShadow = new FakeShadowManager(this.fakeShadowShader);

            var universalRendererType = typeof(UniversalRenderer);
            this.propUseDepthPriming = universalRendererType.GetProperty("useDepthPriming", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        }

        protected override void Dispose(bool disposing) {
            this.depthPass = null;
            this.shadowPass?.Dispose();
            this.shadowPass = null;
            this.opaquePass = null;
            this.fakeShadow?.Dispose();
            this.fakeShadow = null;
            this.propUseDepthPriming = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            // Decalを利用するのでDepth Textureは必ずあるという前提
            // 本サンプルはDepth Priming有効で期待しているので決め打ちにしてもいい
            // ATTENTION: Not supported the case that chaging DepthPrimingMode in runtime when available shadows exist.
            var useDepthPriming = (bool)this.propUseDepthPriming.GetValue(renderer);

#if UNITY_EDITOR
            // Depth Priming Modeが無効かつCopyDepthの為にDepthPrepassを走らせるとイベントが正常に差し込めないので非対応
            var memberCopyDepthMode = typeof(UniversalRenderer).GetField("m_CopyDepthMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var depthPrepass = useDepthPriming | (CopyDepthMode)memberCopyDepthMode.GetValue(renderer) == CopyDepthMode.ForcePrepass;
            if (!useDepthPriming && depthPrepass)
                Debug.LogError("not supported \"Depth Texture Mode\" to \"Force Prepass\" in URP Asset");
#endif

            // support to modify in runtime
            this.depthPass.layerMask = this.characterLayer;
            this.opaquePass.layerMask = this.characterLayer;
            this.shadowPass.layerMask = this.characterLayer;
            this.shadowPass.decalMapSize = this.decalMapSize;

            this.opaquePass.useDepthPriming = useDepthPriming;
            if (useDepthPriming)
                this.depthPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
            else
                this.depthPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques; // for CopyDepth

            this.fakeShadow.SetAvailableCount(this.maxShadowCount, this.decalMapSize); // 有効数の変更、ランタイムで変更したい場合は要改変、実行数より有効数を減らされた場合の例外対応に注意
            this.fakeShadow.ResolveRequests(); // リクエスト処理

            renderer.EnqueuePass(this.depthPass);
            renderer.EnqueuePass(this.shadowPass);
            renderer.EnqueuePass(this.opaquePass);
        }
    }
}
