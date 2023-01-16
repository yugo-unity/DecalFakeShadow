using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    public class FakeShadow : MonoBehaviour {
        [SerializeField]
        bool shadowMesh = false;
        [SerializeField]
        DecalProjector projector = null; // NOTE: RuntimeでDecalProjectorを生成してもいいが...

        new Renderer renderer = null;

        public bool isShadowMesh { get => this.shadowMesh; }

        void OnEnable() {
            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();

            var ret = FakeShadowManager.Request(this);
            if (ret) {
                this.projector.enabled = true;
            } else {
                Debug.LogError("Failed to request for FakeShadow. Increase the max count.");
                this.projector.enabled = false;
            }
        }

        void OnDisable() {
            // 与えられたインデックスを返却
            FakeShadowManager.Return(this);
            this.projector.enabled = false;
        }

        public void UpdateUV(float uvScale, Vector2 uvBias, Material material) {
            this.projector.uvScale = new Vector2(uvScale, uvScale);
            this.projector.uvBias = uvBias;

            var materials = this.renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; ++i)
                materials[i] = material;
            this.renderer.materials = materials;
        }
        public void UpdateUV(float uvScale, Vector2 uvBias, int propertyId, Vector4 offset) {
            this.projector.uvScale = new Vector2(uvScale, uvScale);
            this.projector.uvBias = uvBias;

            var materials = this.renderer.materials;
            foreach (var mat in materials)
                mat.SetVector(propertyId, offset);
        }
        public void Cancel() {
            Debug.LogError("Canceled FakeShadow. The max count is insufficient.");
            this.projector.enabled = false;
        }
    }
}
