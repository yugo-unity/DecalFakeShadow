using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    /// <summary>
    /// DecalProjectorを利用したトップビューの疑似影
    /// </summary>
    public class FakeShadow : MonoBehaviour {
        [SerializeField]
        bool shadowMesh = false;
        [SerializeField]
        DecalProjector projector = null; // NOTE: RuntimeでDecalProjectorを生成してもいいが...

        new Renderer renderer = null;

        private Transform root = null;
        private bool isSkinned = false;

        private Vector3 prevSize = Vector3.zero;
        private Matrix4x4 projection = Matrix4x4.identity;

        public bool isShadowMesh { get => this.shadowMesh; }

        void OnEnable() {
            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();
            if (this.root == null)
                this.root = this.projector.transform;

            var ret = FakeShadowManager.Request(this);
            if (ret) {
                this.projector.enabled = true;
                this.isSkinned = this.renderer is SkinnedMeshRenderer;
            } else {
                Debug.LogError("Failed to request for FakeShadow. Increase the max count.");
                this.projector.enabled = false;
            }
        }

        void OnDisable() {
            FakeShadowManager.Return(this);
            this.projector.enabled = false;
        }

        public void Cancel() {
            this.projector.enabled = false;
        }

        /// <summary>
        /// FakeShadowManagerからの設定
        /// </summary>
        /// <param name="uvScale">ProjectorのScale</param>
        /// <param name="uvBias">ProjectorのBias</param>
        /// <param name="material">ShadowMesh用のMaterial</param>
        /// <param name="offset">ShadowTextureのUV</param>
        public void UpdateUV(float uvScale, Vector2 uvBias, Material material, Vector4 offset) {
            this.projector.uvScale = new Vector2(uvScale, uvScale);
            this.projector.uvBias = uvBias;

            this.UpdateProjection(force : true);
            this.CalcViewMatrix(out var view);

            if (material != null) {
                material.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                material.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);

                var materials = this.renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; ++i)
                    materials[i] = material;
                this.renderer.materials = materials;
            } else {
                var materials = this.renderer.materials;
                foreach (var mat in materials) {
                    mat.SetVector(FakeShadowManager.PROP_ID_OFFSET, offset);
                    mat.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                    mat.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
                }
            }
        }

        /// <summary>
        /// パラメータ更新
        /// </summary>
        void LateUpdate() {
            var updated = this.UpdateProjection();
            if (!updated && !this.isSkinned)
                return;

            this.CalcViewMatrix(out var view);
            foreach (var mat in this.renderer.materials) {
                mat.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                mat.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
            }
        }

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

        /// <summary>
        /// View行列の更新
        /// </summary>
        private void CalcViewMatrix(out Matrix4x4 view) {
            view = Matrix4x4.TRS(
                this.root.position,         // centering
                FakeShadowManager.TOP_ROT,  // top view
                new Vector3(1f, 1f, -1f)
            ).inverse;
        }
    }
}
