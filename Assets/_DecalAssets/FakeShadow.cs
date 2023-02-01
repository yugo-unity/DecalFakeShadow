using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    [RequireComponent(typeof(Renderer))]
    /// <summary>
    /// DecalProjectorを利用したトップビューの疑似影
    /// </summary>
    public class FakeShadow : MonoBehaviour {
        [Tooltip("Shadow Mesh用か")]
        public bool shadowMesh = false;
        [Tooltip("OnEnableで自動起動")]
        public bool activeOnEnable = false;
        [Tooltip("DecalProjector範囲からはみ出た部分をClipするか")]
        public bool glidClipping = false;
        [Tooltip("対応するDecal Projector参照")]
        public DecalProjector projector = null; // NOTE: 暗黙(HideInspector)でDecalProjectorを生成しても良さそう

        [System.Flags]
        private enum STATE {
            NONE,
            REQUEST,    // リクエスト済
            AVAIRABLE,  // 設定済
        }

        private new Renderer renderer = null;
        private Transform root = null;
        private bool isSkinned = false;
        private STATE state = 0;

        private bool prevClipping = false;
        private Vector3 prevSize = Vector3.zero;
        private Matrix4x4 projection = Matrix4x4.identity;
        private UnityEngine.Rendering.LocalKeyword clipKeyword;

        internal bool isShadowMesh { get => this.shadowMesh; }
        internal bool isRequested { get => this.state.HasFlag(STATE.REQUEST); }

        void OnEnable() {
            if (this.activeOnEnable)
                this.Wakeup();
        }
        void OnDisable() {
            this.Sleep();
        }

        /// <summary>
        /// 起動
        /// </summary>
        public void Wakeup() {
            Debug.Assert(this.projector != null, "Set the reference of DecalProjector");

            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();
            if (this.root == null)
                this.root = this.projector.transform;

            var ret = FakeShadowManager.Request(this);
            if (ret) {
                this.isSkinned = this.renderer is SkinnedMeshRenderer;
                this.state |= STATE.REQUEST;
                this.projector.enabled = true;
            } else {
                Debug.LogError("Failed to request for FakeShadow. Increase the max count.");
                this.projector.enabled = false;
            }
        }

        /// <summary>
        /// 停止
        /// </summary>
        public void Sleep() {
            if (this.state.HasFlag(STATE.AVAIRABLE))
                FakeShadowManager.Return(this);
            this.state = 0; // cleared REQUEST/AVAIRABLE
            this.projector.enabled = false;

            // NOTE: Lit(Instance)が暗黙で与えられるのでやらない
            //var materials = this.renderer.materials;
            //for (var i = 0; i < materials.Length; ++i)
            //    materials[i] = null;
            //this.renderer.materials = materials;

            //this.mat = null;
            //this.paramIndex = -1;
        }

        /// <summary>
        /// パラメータ更新
        /// </summary>
        void LateUpdate() {
            if (!this.state.HasFlag(STATE.AVAIRABLE))
                return;
            var updated = this.UpdateProjection();
            updated |= this.prevClipping != this.glidClipping;
            if (!updated && !this.isSkinned)
                return;

            this.prevClipping = this.glidClipping;
            var view = this.root.worldToLocalMatrix;
            foreach (var mat in this.renderer.materials) {
                mat.SetKeyword(this.clipKeyword, this.glidClipping);
                mat.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                mat.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
            }

            // for Debug
            //if (this.mat != this.renderer.material)
            //    Debug.LogError("do not set the material from external");
        }

        /// <summary>
        /// FakeShadowManagerからの設定
        /// 既に稼働中でAvairableCountが変更された場合にも再設定される
        /// </summary>
        /// <param name="param">設定パラメータ</param>
        internal void Setup(in FakeShadowManager.MatParam param) {
            if (!this.state.HasFlag(STATE.REQUEST))
                return;

            this.UpdateProjection(force : true);
            var view = this.root.worldToLocalMatrix;

            if (this.shadowMesh) {
                var materials = this.renderer.materials;
                for (var i = 0; i < materials.Length; ++i)
                    materials[i] = param.material;
                this.renderer.materials = materials;

                // ShadowMeshの有無でShaderが変わるので都度生成
                this.clipKeyword = new UnityEngine.Rendering.LocalKeyword(materials[0].shader, FakeShadowManager.CLIP_RECT_KEYWORD);
                param.material.SetKeyword(this.clipKeyword, this.glidClipping);
                param.material.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                param.material.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
            } else {
                var materials = this.renderer.materials;

                // ShadowMeshの有無でShaderが変わるので都度生成
                this.clipKeyword = new UnityEngine.Rendering.LocalKeyword(materials[0].shader, FakeShadowManager.CLIP_RECT_KEYWORD);
                foreach (var mat in materials) {
                    mat.SetKeyword(this.clipKeyword, this.glidClipping);
                    mat.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                    mat.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
                    mat.SetVector(FakeShadowManager.PROP_ID_OFFSET, param.offset);
                    mat.SetVector(FakeShadowManager.PROP_ID_CLIP, param.clipRect);
                }
            }
            this.projector.uvScale = param.uvScale;
            this.projector.uvBias = param.uvBias;
            this.prevClipping = this.glidClipping;

            ////this.mat = param.material;
            //this.mat = this.renderer.material;
            //this.paramIndex = param.index;

            this.state |= STATE.AVAIRABLE;
        }
        //Material mat;
        //int paramIndex = -1;

        /// <summary>
        /// Projection行列の更新
        /// </summary>
        /// <param name="force">force to update</param>
        /// <returns>updated</returns>
        private bool UpdateProjection(bool force = false) {
            var size = this.projector.size;
            if (!force && this.prevSize == size)
                return false;

            var width = size.x * 0.5f;
            var height = size.y * 0.5f;
            // NOTE: rangeは必要十分な値でハードコードしてもいい、サンプルではBoundingBoxにて判断
            var range = this.renderer.bounds.max.magnitude;
            this.projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-width, width, -height, height, -range, range), true
            );
            this.prevSize = size;

            return true;
        }
    }
}
