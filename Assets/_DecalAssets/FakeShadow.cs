using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Runtime.CompilerServices;

namespace UTJ {
    [RequireComponent(typeof(Renderer))]
    /// <summary>
    /// DecalProjectorを利用したトップビューの疑似影
    /// </summary>
    public class FakeShadow : MonoBehaviour {
        [Tooltip("対応するDecal Projector参照")]
        public DecalProjector projector = null; // NOTE: 暗黙(HideInspector)でDecalProjectorを生成しても良さそう
        [Tooltip("OnEnableで自動起動")]
        public bool activeOnEnable = false;
        [Tooltip("Projectorは動かない")]
        public bool isStatic = false;
        [SerializeField, Tooltip("Shadow Meshである、Runtimeでの変更をしてはいけない")]
        private bool shadowMesh = false;
        [SerializeField, Tooltip("DecalProjector範囲からはみ出た部分をClipする")]
        private bool _glidClipping = false;

        [System.Flags]
        private enum STATE {
            NONE,
            REQUEST,    // リクエスト済
            AVAIRABLE,  // 設定済
        }

        private new Renderer renderer = null;
        private Transform root = null;
        private Material[] materials = null;
        private STATE state = 0;

        private int updateMaterialCount = 0;
        //private bool prevClipping = false;
        private Vector3 prevSize = Vector3.zero;
        private Vector3 prevPivot = Vector3.zero;
        private Matrix4x4 projection = Matrix4x4.identity;
        private UnityEngine.Rendering.LocalKeyword[] clipKeywords;

        internal bool isShadowMesh { get => this.shadowMesh; }
        internal bool isRequested { get => this.state.HasFlag(STATE.REQUEST); }
        public bool glidClipping {
            get => this._glidClipping;
            set {
                if (this._glidClipping == value)
                    return;
                this._glidClipping = value;
                for (var i = 0; i < this.updateMaterialCount; ++i)
                    this.materials[i].SetKeyword(this.clipKeywords[i], this._glidClipping);
            }
        }

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

            if (this.renderer == null) {
                this.renderer = this.GetComponent<Renderer>();

                if (this.shadowMesh) {
                    this.materials = this.renderer.sharedMaterials;
                    this.updateMaterialCount = 1;
                } else {
                    this.materials = this.renderer.materials;
                    this.updateMaterialCount = this.materials.Length;
                }
                this.clipKeywords = new UnityEngine.Rendering.LocalKeyword[this.materials.Length];
            }
            if (this.root == null)
                this.root = this.projector.transform;

            var ret = FakeShadowManager.Request(this);
            if (ret) {
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
        public void Sleep(bool returnShadow=true) {
            if (returnShadow && this.state.HasFlag(STATE.AVAIRABLE))
                FakeShadowManager.Return(this);

            this.state = 0; // cleared REQUEST/AVAIRABLE
            this.projector.enabled = false;
        }

        /// <summary>
        /// パラメータ更新
        /// </summary>
        void LateUpdate() {
            if (!this.state.HasFlag(STATE.AVAIRABLE))
                return;

            if (this.UpdateProjection()) {
                for (var i = 0; i < this.updateMaterialCount; ++i)
                    this.materials[i].SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
            }

            // 静的なProjectorはView行列の更新不要
            if (this.isStatic)
                return;

            // Scaleはモデル行列に反映されるのでView行列でつぶさない
            //var view = this.root.worldToLocalMatrix;

            // x3ぐらい速くなる（ShadowTextureの都合上、数十程度しかこないので雀の涙）
            //var view = Matrix4x4.TRS(this.root.position, this.root.rotation, Vector3.one).inverse;
            TR_Inverse(this.root, out var view);

            for (var i = 0; i < this.updateMaterialCount; ++i)
                this.materials[i].SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
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
            TR_Inverse(this.root, out var view);

            if (this.shadowMesh) {
                for (var i = 0; i < this.materials.Length; ++i)
                    this.materials[i] = param.material;
                this.renderer.materials = this.materials;

                // NOTE: FakeShadowManagerでキャッシュしてもいい
                this.clipKeywords[0] = new UnityEngine.Rendering.LocalKeyword(param.material.shader, FakeShadowManager.CLIP_RECT_KEYWORD);

                param.material.SetKeyword(this.clipKeywords[0], this.glidClipping);
                param.material.SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                param.material.SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
            } else {
                for (var i = 0; i < this.materials.Length; ++i) {
                    this.clipKeywords[i] = new UnityEngine.Rendering.LocalKeyword(this.materials[i].shader, FakeShadowManager.CLIP_RECT_KEYWORD);

                    this.materials[i].SetKeyword(this.clipKeywords[i], this.glidClipping);
                    this.materials[i].SetMatrix(FakeShadowManager.PROP_ID_PROJ, this.projection);
                    this.materials[i].SetMatrix(FakeShadowManager.PROP_ID_VIEW, view);
                    // 1度設定すればいいのでFakeShadowManagerからMaterialを借りる時は不要
                    this.materials[i].SetVector(FakeShadowManager.PROP_ID_OFFSET, param.offset);
                    this.materials[i].SetVector(FakeShadowManager.PROP_ID_CLIP, param.clipRect);
                }
            }
            this.projector.uvScale = param.uvScale;
            this.projector.uvBias = param.uvBias;

            this.state |= STATE.AVAIRABLE;
        }

        /// <summary>
        /// Projection行列の更新
        /// </summary>
        /// <param name="force">force to update</param>
        /// <returns>updated</returns>
        private bool UpdateProjection(bool force = false) {
            var size = this.projector.size;
            var pivot = this.projector.pivot;
            if (!force && this.prevSize == size && this.prevPivot == pivot)
                return false;

            var width = size.x * 0.5f;
            var height = size.y * 0.5f;
            // NOTE: rangeは必要十分な値でハードコードしてもいい
            var range = (size.z + this.projector.pivot.magnitude);
            this.projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-width, width, -height, height, -range, range), true
            );
            this.prevSize = size;
            this.prevPivot = pivot;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TR_Inverse(Transform tr, out Matrix4x4 m) {
            tr.GetPositionAndRotation(out var p, out var q);
            
            // NOTE:
            // Quaternionは正規化前提
            // Inverseなのでpとqは反転

            var x = -q.x * 2f;
            var y = -q.y * 2f;
            var z = -q.z * 2f;
            var xx = -q.x * x;
            var yy = -q.y * y;
            var zz = -q.z * z;
            var xy = -q.x * y;
            var xz = -q.x * z;
            var yz = -q.y * z;
            var wx = q.w * x;
            var wy = q.w * y;
            var wz = q.w * z;

		    m.m00 = 1f - (yy + zz);
		    m.m10 = xy + wz;
		    m.m20 = xz - wy;
		    m.m30 = 0f;

		    m.m01 = xy - wz;
		    m.m11 = 1f - (xx + zz);
		    m.m21 = yz + wx;
		    m.m31 = 0f;

		    m.m02 = xz + wy;
		    m.m12 = yz - wx;
		    m.m22 = 1f - (xx + yy);
		    m.m32 = 0f;

		    //m.m03 = p.x;
		    //m.m13 = p.y;
		    //m.m23 = p.z;
		    //m.m33 = 1f;
		    m.m03 = m.m00 * -p.x + m.m01 * -p.y + m.m02 * -p.z; // + m.m03;
		    m.m13 = m.m10 * -p.x + m.m11 * -p.y + m.m12 * -p.z; // + m.m13;
		    m.m23 = m.m20 * -p.x + m.m21 * -p.y + m.m22 * -p.z; // + m.m23;
		    //m.m33 = m.m30 * -p.x + m.m31 * -p.y + m.m32 * -p.z + m.m33;
		    m.m33 = 1f;
        }
    }
}
