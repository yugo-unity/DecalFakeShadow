using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    #region FAKE SHADOW
    class FakeShadowManager {
        private static FakeShadowManager instance = null;
        private Stack<int> indexStack = null;
        private Material[] materialList = null;
        private Vector2[] uvBiasList = null;
        private Vector2 uvScale = Vector2.one;

        FakeShadowManager(int maxCount, Shader fakeShader) {
            this.indexStack = new Stack<int>(maxCount);
            this.materialList = new Material[maxCount];
            this.uvBiasList = new Vector2[maxCount];

            var v = Matrix4x4.TRS(
                new Vector3(0f, 3f, 0f),        // 真上
                Quaternion.Euler(90f, 0f, 0f),  // 見下ろす
                new Vector3(1f, 1f, -1f)
            ).inverse;
            var p = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-1f, 1f, -1f, 1f, 0f, 4f), // -1~1m範囲で奥行4m
                true
            );

            var line = Mathf.Ceil(Mathf.Sqrt(maxCount)); // 累乗でグリッド生成
            this.uvScale.x = this.uvScale.y = 1f / line;   // 0~1
            var block = 2f / line;      // -1~1

            for (var i = 0; i < maxCount; ++i) {
                this.indexStack.Push(i);

                // MaterialPropertyBlockだとSRPBatcherが使えないので明示的にインスタンスを作る
                this.materialList[i] = new Material(fakeShader);
                this.materialList[i].SetFloat("_Line", line);
                var pos = Vector4.zero;
                pos.x = -1f + block * ((float)i % line + 0.5f);
                pos.y =  1f - block * (Mathf.Floor((float)i / line) + 0.5f);
                this.materialList[i].SetVector("_Offset", pos);
                this.materialList[i].SetMatrix("_ViewMat", v);
                this.materialList[i].SetMatrix("_ProjMat", p);

                this.uvBiasList[i] = new Vector2(this.uvScale.x * Mathf.Floor((float)i % line), this.uvScale.y * Mathf.Floor((float)i / line));
            }
        }

        /// <summary>
        /// 初期化、SRFeatureで呼ばれるので外部は気にしなくてOK
        /// </summary>
        /// <param name="maxCount">FakeShadow最大値</param>
        /// <param name="shader">FakeShadow用Shader</param>
        public static void Iniitalize(int maxCount, Shader shader) {
            instance = new FakeShadowManager(maxCount, shader);
        }

        /// <summary>
        /// 破棄
        /// </summary>
        public static void Dispose() {
            foreach (var mat in instance.materialList)
                Object.DestroyImmediate(mat);
            instance.materialList = null;
            instance = null;
        }

        /// <summary>
        /// インデックス取得
        /// </summary>
        /// <param name="material">ShadowMeshRendererに与えるMaterial</param>
        /// <param name="uvScale">DecalProjectorに渡す値</param>
        /// <param name="uvBias">DecalProjectorに渡す値</param>
        /// <returns>貸与されたインデックス</returns>
        public static int Request(out Material material, out Vector2 uvScale, out Vector2 uvBias) {
            if (instance.indexStack.Count == 0) {
                material = null;
                uvScale = Vector2.zero;
                uvBias = Vector2.zero;
                return -1;
            }

            var index = instance.indexStack.Pop();
            material = instance.materialList[index];
            uvScale = instance.uvScale;
            uvBias = instance.uvBiasList[index];
            return index;
        }

        /// <summary>
        /// インデックス返却
        /// </summary>
        /// <param name="index">Requestで取得したインデックス</param>
        public static void Return(int index) {
            instance.indexStack.Push(index);
        }
    }
    #endregion

    public class FakeShadowPassFeature : ScriptableRendererFeature {
        public LayerMask characterLayer = 0;
        public LayerMask fakeShadowLayer = 0;
        public Shader fakeShadowShader = null;
        [Range(1, 16)]
        public int maxShadowCount = 9; // 3x3
        public int decalMapSize = 512;

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
            this.depthPass = new CharacterDepthPass();
            this.shadowPass = new CharacterShadowPass();
            this.opaquePass = new CharacterOpaquePass();

            FakeShadowManager.Iniitalize(this.maxShadowCount, this.fakeShadowShader);

            var universalRendererType = typeof(UniversalRenderer);
            this.propUseDepthPriming = universalRendererType.GetProperty("useDepthPriming", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        }

        protected override void Dispose(bool disposing) {
            FakeShadowManager.Dispose();
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

            renderer.EnqueuePass(this.depthPass);
            renderer.EnqueuePass(this.shadowPass);
            renderer.EnqueuePass(this.opaquePass);
        }
    }
}
