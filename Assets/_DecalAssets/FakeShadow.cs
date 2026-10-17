using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Runtime.CompilerServices;

namespace UTJ {
    /// <summary>
    /// DecalProjectorを利用したトップビューの疑似影
    /// RendererのUser Valueに割当インデックスを書き込み、FakeShadowPassFeatureが共通Materialで描画する
    /// Edit ModeでもScene View/Game Viewに描画する
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    [ExecuteAlways]
    public class FakeShadow : MonoBehaviour {
        [Tooltip("DecalProjector that draws this shadow.")]
        public DecalProjector projector = null; // NOTE: 暗黙(HideInspector)でDecalProjectorを生成しても良さそう
        [Tooltip("Call Wakeup() automatically in OnEnable.")]
        public bool activeOnEnable = false;
        [Tooltip("The projector does not move (skips view matrix updates).")]
        public bool isStatic = false;
        [SerializeField, Tooltip("Use this renderer only for the fake shadow (its materials are replaced). Do not change at runtime.")]
        private bool shadowMesh = false;
        [SerializeField, Tooltip("Clip the shadow to its own cell of the decal map.")]
        private bool _gridClipping = false;

        [System.Flags]
        private enum STATE {
            NONE,
            REQUEST,    // リクエスト済
            AVAILABLE,  // 設定済
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
        // Shadow Meshの共通Material割当前のMaterial
        // NOTE: Domain Reload時のホットリロードではprivateフィールドもシリアライズされ、nullが空配列で復元されるので対象外にする
        [System.NonSerialized]
        private Material[] originalMaterials = null;

        internal bool isShadowMesh { get => this.shadowMesh; }
        internal bool isRequested { get => this.state.HasFlag(STATE.REQUEST); }
        public bool gridClipping {
            get => this._gridClipping;
            set {
                if (this._gridClipping == value)
                    return;
                this._gridClipping = value;
                if (this.shadowIndex >= 0)
                    FakeShadowManager.SetShadowClip(this.shadowIndex, this._gridClipping);
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
            if (this.projector == null) {
                Debug.LogError($"Set the reference of DecalProjector : {this.name}", this);
                return;
            }

            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();
            if (this.root == null)
                this.root = this.projector.transform;

            // NOTE: Projectorの有効化はSetupで行う
            // OnEnableはScene/Domainのロード中に呼ばれ、ここで有効化するとUnityによるProjectorの有効化と重複してOnEnableが2回走る
            // 割当数の不足はFakeShadowManager.ResolveRequestsで判定する
            FakeShadowManager.Request(this);
            this.state |= STATE.REQUEST;
        }

        /// <summary>
        /// 停止
        /// </summary>
        public void Sleep() {
            this.Stop(returnShadow: true);
        }

        /// <summary>
        /// FakeShadowManagerによる打ち切り（割当数の不足等）
        /// インデックスはManager側で回収済み、または未割当なので返却しない
        /// </summary>
        internal void Cancel() {
            this.Stop(returnShadow: false);
        }

        private void Stop(bool returnShadow) {
            if (returnShadow && this.state.HasFlag(STATE.AVAILABLE))
                FakeShadowManager.Return(this);

            this.state = 0; // cleared REQUEST/AVAILABLE
            // Scene破棄時はDecalProjectorが先に破棄されている場合がある
            if (this.projector != null)
                this.projector.enabled = false;
            this.ClearUserValue();
            this.RestoreMaterials();
        }

        void LateUpdate() {
#if UNITY_EDITOR
            // Edit ModeではLateUpdateが毎フレーム呼ばれないので、FakeShadowPassFeatureが描画前に更新する
            if (!Application.isPlaying)
                return;
#endif
            this.UpdateShadow();
        }

        /// <summary>
        /// パラメータ更新
        /// </summary>
        internal void UpdateShadow() {
            if (!this.state.HasFlag(STATE.AVAILABLE))
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
        /// 既に稼働中でAvailableCountが変更された場合にも再設定される
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
                if (this.originalMaterials == null) {
                    this.originalMaterials = (Material[])materials.Clone();
#if UNITY_EDITOR
                    s_AssignedShadowMeshes.Add(this);
#endif
                }
                for (var i = 0; i < materials.Length; ++i)
                    materials[i] = param.material;
                this.renderer.sharedMaterials = materials;
            }

            this.shadowIndex = param.index;
            this.ApplyUserValue(param.userValueShift);
            FakeShadowManager.SetShadowMatrix(this.shadowIndex, this.projection * this.view);
            FakeShadowManager.SetShadowClip(this.shadowIndex, this.gridClipping);

            // NOTE: renderingLayerMaskのsetterはDecalの描画データを更新しないので、更新が走るuvScale/uvBiasより先に設定する
            this.projector.renderingLayerMask = param.receiverRenderingLayers;
            this.projector.uvScale = param.uvScale;
            this.projector.uvBias = param.uvBias;
            this.projector.enabled = true;

            this.state |= STATE.AVAILABLE;
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

        /// <summary>
        /// Shadow Meshに割り当てた共通Materialを元に戻す
        /// </summary>
        private void RestoreMaterials() {
            if (this.originalMaterials == null)
                return;
            if (this.renderer != null)
                this.renderer.sharedMaterials = this.originalMaterials;
            this.originalMaterials = null;
#if UNITY_EDITOR
            s_AssignedShadowMeshes.Remove(this);
#endif
        }

#if UNITY_EDITOR
        // 共通MaterialはHideAndDontSaveなので、Edit Modeで割り当てたままSceneに保存させない
        private static readonly System.Collections.Generic.HashSet<FakeShadow> s_AssignedShadowMeshes = new System.Collections.Generic.HashSet<FakeShadow>(FakeShadowReferenceComparer.instance);
        private static readonly System.Collections.Generic.Dictionary<FakeShadow, Material[]> s_SavingMaterials = new System.Collections.Generic.Dictionary<FakeShadow, Material[]>(FakeShadowReferenceComparer.instance);

        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterSceneSaveCallbacks() {
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += (scene, path) => {
                s_AssignedShadowMeshes.RemoveWhere(shadow => shadow == null);
                foreach (var shadow in s_AssignedShadowMeshes) {
                    if (shadow.gameObject.scene != scene || shadow.renderer == null)
                        continue;
                    s_SavingMaterials.Add(shadow, shadow.renderer.sharedMaterials);
                    shadow.renderer.sharedMaterials = shadow.originalMaterials;
                }
            };
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += scene => {
                foreach (var pair in s_SavingMaterials) {
                    if (pair.Key != null && pair.Key.renderer != null)
                        pair.Key.renderer.sharedMaterials = pair.Value;
                }
                s_SavingMaterials.Clear();
            };
        }
#endif

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
