using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UTJ {
    public class FakeShadow : MonoBehaviour {
        [SerializeField]
        private DecalProjector decalProjector = null; // RuntimeでDecalProjectorを生成してもいい

        private int fakeIndex = -1;

        void Start() {
            this.fakeIndex = FakeShadowManager.Request(out var fakeMaterial, out var uvScale, out var uvBias);
            if (this.fakeIndex >= 0) {
                var renderer = this.GetComponent<Renderer>();

                // SubMesh対応
                // SkinnedMeshはMeshRendererを分けずにSubMeshの方がオーバーヘッドが低いのでMulti Renderer対応はしない
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; ++i)
                    materials[i] = fakeMaterial;
                renderer.materials = materials;

                this.decalProjector.uvScale = uvScale;
                this.decalProjector.uvBias = uvBias;
            } else {
                Debug.LogError("Failed to request for FakeShadow. Increase the max count.");
            }
        }

        private void OnDestroy() {
            // 与えられたインデックスを返却する、OnEnable/OnDisableでもOK
            if (this.fakeIndex >= 0)
                FakeShadowManager.Return(this.fakeIndex);
            this.fakeIndex = -1;
        }
    }
}
