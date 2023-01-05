using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    #region FAKE SHADOW
    public class FakeShadowManager {
        #region DEFINE
        private struct MatParam {
            public Vector4 offset;
            public Vector2 uvBias;
        }

        public const int SHADOW_LIMIT = 25; // 1024の16分割で256を限界値として想定
        static readonly int PROP_ID_LINE = Shader.PropertyToID("_FakeShadowLine");
        static readonly int PROP_ID_OFFSET = Shader.PropertyToID("_FakeShadowOffset");
        static readonly int PROP_ID_COLOR = Shader.PropertyToID("_FakeShadowColor");
        static readonly int PROP_ID_VIEW = Shader.PropertyToID("_FakeShadowView");
        static readonly int PROP_ID_PROJ = Shader.PropertyToID("_FakeShadowProj");
        #endregion


        #region MEMBER
        private static FakeShadowManager instance = null;
        public static Stack<FakeShadow> requests = new Stack<FakeShadow>(SHADOW_LIMIT);
        private Dictionary<FakeShadow, int> available = new Dictionary<FakeShadow, int>(SHADOW_LIMIT);
        private Stack<int> indexStack = new Stack<int>(SHADOW_LIMIT);
        private Material[] materials = null;
        private MatParam[] matParams = new MatParam[SHADOW_LIMIT];
        private float uvScale = 1f;
        private Color _color = Color.black;
        private int availableCount = 0;
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
        /// <param name="availableCount">上限値</param>
        /// <param name="fakeShader">ShadowMesh用のShader</param>
        public FakeShadowManager(int availableCount, Shader fakeShader) {
            if (instance != null) {
                Debug.LogError("FakeShadowManager is duplicated.");
                return;
            }
            instance = this;

            if (fakeShader != null) {
                this.materials = new Material[SHADOW_LIMIT];
                for (var i = 0; i < SHADOW_LIMIT; ++i)
                    this.materials[i] = new Material(fakeShader);
            }

            var v = Matrix4x4.TRS(
                new Vector3(0f, 3f, 0f),        // 真上
                Quaternion.Euler(90f, 0f, 0f),  // 見下ろす
                new Vector3(1f, 1f, -1f)
            ).inverse;
            var p = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-1f, 1f, -1f, 1f, 0f, 4f), // -1~1m範囲で奥行4m
                true
            );
            Shader.SetGlobalMatrix(PROP_ID_VIEW, v);
            Shader.SetGlobalMatrix(PROP_ID_PROJ, p);
            Shader.SetGlobalColor(PROP_ID_COLOR, Color.gray);
            this.SetAvailableCount(availableCount);
        }

        /// <summary>
        /// 破棄
        /// </summary>
        public void Dispose() {
            if (this.materials != null) {
                foreach (var mat in this.materials)
                    Object.DestroyImmediate(mat);
                this.materials = null;
            }
            this.available.Clear();
            this.indexStack.Clear();
            instance = null;
        }

        /// <summary>
        /// 上限設定
        /// </summary>
        /// <param name="count">上限値</param>
        public void SetAvailableCount(int count) {
            if (this.availableCount == count)
                return;

            this.availableCount = count;

            var line = Mathf.Ceil(Mathf.Sqrt(count)); // 累乗でグリッド生成
            this.uvScale = 1f / line; // 0~1
            var block = 2f / line;      // -1~1

            this.indexStack.Clear();
            for (var i = 0; i < count; ++i) {
                var pos = Vector4.zero;
                pos.x = -1f + block * ((float)i % line + 0.5f);
                pos.y = 1f - block * (Mathf.Floor((float)i / line) + 0.5f);

                this.indexStack.Push(i);
                if (this.materials != null)
                    this.materials[i].SetVector(PROP_ID_OFFSET, pos);
                this.matParams[i].offset = pos;
                this.matParams[i].uvBias = new Vector2(this.uvScale * Mathf.Floor((float)i % line), this.uvScale * Mathf.Floor((float)i / line));
            }

            // 現在有効なShadowの更新
            if (this.available.Count > 0) {
                foreach (var shadow in this.available.Keys)
                    requests.Push(shadow);
                this.available.Clear();
            }

            Shader.SetGlobalFloat(PROP_ID_LINE, line);
        }

        /// <summary>
        /// 新規リクエストの対応
        /// </summary>
        public void ResolveRequests() {
            while (requests.Count > 0) {
                var req = requests.Pop();

                var index = this.indexStack.Pop();
                this.available.Add(req, index);

                // SkinnedMeshはMeshRendererを分けずにSubMeshの方がオーバーヘッドが低いのでMulti Renderer対応はしない
                if (req.isShadowMesh) {
                    if (instance.materials == null) {
                        Return(req);
                        Debug.LogError("Not supported FakeShadow by ShadowMesh. Need the Shader.");
                        continue;
                    }

                    req.UpdateUV(this.uvScale, this.matParams[index].uvBias, this.materials[index]);
                } else {
                    req.UpdateUV(this.uvScale, this.matParams[index].uvBias, PROP_ID_OFFSET, this.matParams[index].offset);
                }

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
                Shader.SetGlobalColor("_FakeShadowColor", value);
            }
        }

        /// <summary>
        /// インデックス取得
        /// </summary>
        /// <param name="renderer">SkinnedMeshRenderer</param>
        /// <param name="uvScale">DecalProjectorに渡す値</param>
        /// <param name="uvBias">DecalProjectorに渡す値</param>
        /// <returns>貸与されたインデックス</returns>
        public static bool Request(FakeShadow shadow) {
            if (instance == null)
                return false;

            // リクエスト済
            if (instance.available.TryGetValue(shadow, out var index))
                return true;

            // 上限
            if (instance.available.Count + requests.Count >= instance.indexStack.Count)
                return false;

            requests.Push(shadow);
            return true;
        }

        /// <summary>
        /// インデックス返却
        /// </summary>
        /// <param name="index">Requestで取得したインデックス</param>
        public static void Return(FakeShadow shadow) {
            if (instance == null || shadow == null)
                return;
            if (instance.available.TryGetValue(shadow, out var index)) {
                instance.indexStack.Push(index);
                instance.available.Remove(shadow);
            }
        }
        #endregion
    }
    #endregion

    public class FakeShadowPassFeature : ScriptableRendererFeature {
        public LayerMask characterLayer = 0;
        public LayerMask fakeShadowLayer = 0;
        public Shader fakeShadowShader = null;
        [Range(1, FakeShadowManager.SHADOW_LIMIT)]
        public int maxShadowCount = 9; // 3x3
        public int decalMapSize = 512;

        private FakeShadowManager fakeShadow = null;
        private CharacterDepthPass depthPass = null;
        private CharacterShadowPass shadowPass = null;
        private CharacterOpaquePass opaquePass = null;


        #region DEPTH PASS
        /// <summary>
        /// CharacterレイヤーのDepth pass
        /// </summary>
        class CharacterDepthPass : ScriptableRenderPass {
            public LayerMask layerMask = 0;

            private ShaderTagId SHADER_TAG_ID = new ShaderTagId("DepthOnly");
            private RenderStateBlock renderStateBlock;

            public CharacterDepthPass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses; // 
                this.renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

                base.profilingSampler = new ProfilingSampler("Character - Depth Pass");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
                //if (this.useDepthPriming && (renderingData.cameraData.renderType == CameraRenderType.Base || renderingData.cameraData.clearDepth))
                    ConfigureTarget(renderingData.cameraData.renderer.cameraDepthTarget); // DepthAttachmentに書いてからコピー
                //else
                //    ConfigureTarget(depthAttachmentHandle.Identifier()); // Depth Textureに別で書く
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
                var cmd = CommandBufferPool.Get();

                using (new ProfilingScope(cmd, this.profilingSampler)) {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    var depthDrawSettings = CreateDrawingSettings(SHADER_TAG_ID, ref renderingData, SortingCriteria.CommonOpaque);
                    depthDrawSettings.perObjectData = PerObjectData.None;
                    var depthFilteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    context.DrawRenderers(renderingData.cullResults, ref depthDrawSettings, ref depthFilteringSettings, ref this.renderStateBlock);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
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
            private RenderStateBlock renderStateBlock;
            private int DECAL_MAP_ID = Shader.PropertyToID("_DecalTexture");

            public CharacterShadowPass() {
                this.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
                this.renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

                base.profilingSampler = new ProfilingSampler("Character - FakeShadow Pass");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
                var desc = new RenderTextureDescriptor(this.decalMapSize, this.decalMapSize, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, 0, 0);
                desc.msaaSamples = 1;
                desc.sRGB = (QualitySettings.activeColorSpace == ColorSpace.Linear);
                cmd.GetTemporaryRT(DECAL_MAP_ID, desc, FilterMode.Bilinear);

                ConfigureTarget(DECAL_MAP_ID);
                ConfigureClear(ClearFlag.Color, Color.clear);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
                var cmd = CommandBufferPool.Get();

                using (new ProfilingScope(cmd, this.profilingSampler)) {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    // DecalをGPU Instancingする為にDecal Map一つにグリッドで描画する
                    var drawSettings = CreateDrawingSettings(FAKE_SHADER_TAG_ID, ref renderingData, SortingCriteria.OptimizeStateChanges);
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings, ref this.renderStateBlock);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            public override void OnCameraCleanup(CommandBuffer cmd) {
                if (cmd == null) {
                    throw new System.ArgumentNullException("cmd");
                }

                cmd.ReleaseTemporaryRT(DECAL_MAP_ID);
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
                this.renderPassEvent = RenderPassEvent.AfterRenderingSkybox; // DecalPassの後
                this.renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

                base.profilingSampler = new ProfilingSampler("Character - Opaque Pass");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
                if (this.useDepthPriming) {
                    this.renderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                    this.renderStateBlock.mask |= RenderStateMask.Depth;
                } else if (this.renderStateBlock.depthState.compareFunction == CompareFunction.Equal) {
                    this.renderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                    this.renderStateBlock.mask |= RenderStateMask.Depth;
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
                var cmd = CommandBufferPool.Get();

                using (new ProfilingScope(cmd, this.profilingSampler)) {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    var sortFlags = renderingData.cameraData.defaultOpaqueSortFlags;
                    if (this.useDepthPriming)
                        sortFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges;
                    var drawSettings = CreateDrawingSettings(SHADER_TAG_ID, ref renderingData, sortFlags);
                    var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, this.layerMask);
                    context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings, ref this.renderStateBlock);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
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

            this.fakeShadow = new FakeShadowManager(maxShadowCount, this.fakeShadowShader);

            var universalRendererType = typeof(UniversalRenderer);
            this.propUseDepthPriming = universalRendererType.GetProperty("useDepthPriming", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        }

        protected override void Dispose(bool disposing) {
            this.depthPass = null;
            this.shadowPass = null;
            this.opaquePass = null;
            this.fakeShadow.Dispose();
            this.fakeShadow = null;
            this.propUseDepthPriming = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            // Decalを利用するのでDepth Textureは必ずあるという前提
            // 本サンプルはDepth Priming有効で期待しているので決め打ちにしてもいい
            var useDepthPriming = (bool)this.propUseDepthPriming.GetValue(renderingData.cameraData.renderer);

#if UNITY_EDITOR
            // Depth Priming Modeが無効かつCopyDepthの為にDepthPrepassを走らせるとイベントが正常に差し込めないので非対応
            var memberCopyDepthMode = typeof(UniversalRenderer).GetField("m_CopyDepthMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var depthPrepass = useDepthPriming | (CopyDepthMode)memberCopyDepthMode.GetValue(renderingData.cameraData.renderer) == CopyDepthMode.ForcePrepass;
            if (!useDepthPriming && depthPrepass)
                Debug.LogError("not supported \"Depth Texture Mode\" to \"Force Prepass\" in URP Asset");
#endif

            // ランタイムで変更できるよう
            this.depthPass.layerMask = this.characterLayer;
            this.opaquePass.layerMask = this.characterLayer;

            this.shadowPass.layerMask = this.fakeShadowLayer;
            this.shadowPass.decalMapSize = this.decalMapSize;

            this.opaquePass.useDepthPriming = useDepthPriming;
            if (useDepthPriming)
                this.depthPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
            else
                this.depthPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques; // CopyDepthの為に書いてあげる必要がある

            this.fakeShadow.SetAvailableCount(this.maxShadowCount); // 有効数の変更、ランタイムで変更したい場合は要改変、実行数より有効数を減らされた場合の例外対応に注意
            this.fakeShadow.ResolveRequests(); // リクエスト処理

            renderer.EnqueuePass(this.depthPass);
            renderer.EnqueuePass(this.shadowPass);
            renderer.EnqueuePass(this.opaquePass);
        }
    }
}
