using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Runtime.CompilerServices;

namespace UTJ {
    [RequireComponent(typeof(Renderer))]
    /// <summary>
    /// DecalProjectorを利用したトップビューの疑似影
    /// RendererのUser Valueに割当インデックスを書き込み、FakeShadowPassFeatureが共通Materialで描画する
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
        private STATE state = 0;

        private Vector3 prevSize = Vector3.zero;
        private Vector3 prevPivot = Vector3.zero;
        private Matrix4x4 projection = Matrix4x4.identity;
        private Matrix4x4 view = Matrix4x4.identity;
        private int shadowIndex = -1;
        private int userValueShift = -1; // 書き込み済みのUser Valueのbit位置

        internal bool isShadowMesh { get => this.shadowMesh; }
        internal bool isRequested { get => this.state.HasFlag(STATE.REQUEST); }
        public bool glidClipping {
            get => this._glidClipping;
            set {
                if (this._glidClipping == value)
                    return;
                this._glidClipping = value;
                if (this.shadowIndex >= 0)
                    FakeShadowManager.SetShadowClip(this.shadowIndex, this._glidClipping);
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

            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();
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
            this.ClearUserValue();
        }

        /// <summary>
        /// FakeShadowManager破棄時の一時停止（リクエスト状態に戻して再設定を待つ）
        /// </summary>
        internal void Suspend() {
            this.state &= ~STATE.AVAIRABLE;
            this.ClearUserValue();
        }

        /// <summary>
        /// パラメータ更新
        /// </summary>
        void LateUpdate() {
            if (!this.state.HasFlag(STATE.AVAIRABLE))
                return;

            var updated = this.UpdateProjection();

            // 静的なProjectorはView行列の更新不要
            if (!this.isStatic) {
                // Scaleはモデル行列に反映されるのでView行列でつぶさない
                //var view = this.root.worldToLocalMatrix;

                // x3ぐらい速くなる（ShadowTextureの都合上、数十程度しかこないので雀の涙）
                //var view = Matrix4x4.TRS(this.root.position, this.root.rotation, Vector3.one).inverse;
                TR_Inverse(this.root, out this.view);
                updated = true;
            }

            if (updated)
                FakeShadowManager.SetShadowMatrix(this.shadowIndex, this.projection * this.view);
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
            TR_Inverse(this.root, out this.view);

            // Shadow MeshはFake Shadow描画専用なので共通Materialを割り当てる（通常描画のPassを持たない）
            if (this.shadowMesh) {
                var materials = this.renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; ++i)
                    materials[i] = param.material;
                this.renderer.sharedMaterials = materials;
            }

            this.shadowIndex = param.index;
            this.ApplyUserValue(param.userValueShift);
            FakeShadowManager.SetShadowMatrix(this.shadowIndex, this.projection * this.view);
            FakeShadowManager.SetShadowClip(this.shadowIndex, this.glidClipping);

            // NOTE: renderingLayerMaskのsetterはDecalの描画データを更新しないので、更新が走るuvScale/uvBiasより先に設定する
            this.projector.renderingLayerMask = param.receiverRenderingLayers;
            this.projector.uvScale = param.uvScale;
            this.projector.uvBias = param.uvBias;

            this.state |= STATE.AVAIRABLE;
        }

        /// <summary>
        /// Fake Shadowを受けるRendering Layer Maskの変更
        /// </summary>
        internal void ChangeReceiverRenderingLayers(RenderingLayerMask mask) {
            this.projector.renderingLayerMask = mask;
            // renderingLayerMaskのsetterはDecalの描画データを更新しないので、更新が走るuvBiasを再設定する
            this.projector.uvBias = this.projector.uvBias;
        }

        /// <summary>
        /// User Valueのbit位置の変更
        /// </summary>
        internal void ChangeUserValueShift(int shift) {
            if (this.shadowIndex >= 0)
                this.ApplyUserValue(shift);
        }

        /// <summary>
        /// 割当インデックス+1をUser Valueの指定位置の8bitに書き込む（他のbitは保持する）
        /// </summary>
        private void ApplyUserValue(int shift) {
            var value = this.GetUserValue();
            if (this.userValueShift >= 0)
                value &= ~(0xFFu << this.userValueShift);
            value &= ~(0xFFu << shift);
            value |= (uint)(this.shadowIndex + 1) << shift;
            this.SetUserValue(value);
            this.userValueShift = shift;
        }

        /// <summary>
        /// User Valueから割当インデックスを消去する
        /// </summary>
        private void ClearUserValue() {
            if (this.userValueShift >= 0 && this.renderer != null)
                this.SetUserValue(this.GetUserValue() & ~(0xFFu << this.userValueShift));
            this.userValueShift = -1;
            this.shadowIndex = -1;
        }

        private uint GetUserValue() {
            if (this.renderer is SkinnedMeshRenderer smr)
                return smr.GetShaderUserValue();
            if (this.renderer is MeshRenderer mr)
                return mr.GetShaderUserValue();
            return 0u;
        }

        private void SetUserValue(uint value) {
            if (this.renderer is SkinnedMeshRenderer smr)
                smr.SetShaderUserValue(value);
            else if (this.renderer is MeshRenderer mr)
                mr.SetShaderUserValue(value);
            else
                Debug.LogError($"FakeShadow supports only MeshRenderer and SkinnedMeshRenderer : {this.name}");
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
