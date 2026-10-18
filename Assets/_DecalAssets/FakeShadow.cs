using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
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
        [SerializeField, Tooltip("Mesh drawn into the decal map instead of this renderer's mesh. Must share this renderer's skeleton.")]
        private Mesh shadowMesh = null;
        [SerializeField, Tooltip("Clip the shadow to its own cell of the decal map.")]
        private bool _gridClipping = true;

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
        // Shadow Mesh描画用の非表示のSkinnedMeshRenderer (HideAndDontSave、Hierarchyのルートに置く)
        // NOTE: Domain Reload時のホットリロードではprivateフィールドもシリアライズされるので対象外にする
        [System.NonSerialized]
        private SkinnedMeshRenderer shadowRenderer = null;

        internal bool isShadowMesh { get => this.shadowMesh != null; }
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

        /// <summary>
        /// FakeShadowManagerによる一時停止（FakeShadowが無効になる設定を検出した場合）
        /// 停止したうえでリクエストを積み直し、設定が戻った後のResolveRequestsで再割当する
        /// インデックスはManager側で回収済みなので返却しない
        /// </summary>
        internal void Suspend() {
            this.Stop(returnShadow: false);
            FakeShadowManager.Request(this);
            this.state |= STATE.REQUEST;
        }

        private void Stop(bool returnShadow) {
            if (returnShadow && this.state.HasFlag(STATE.AVAILABLE))
                FakeShadowManager.Return(this);

            this.state = 0; // cleared REQUEST/AVAILABLE
            // Scene破棄時はDecalProjectorが先に破棄されている場合がある
            if (this.projector != null) {
                this.projector.enabled = false;
                ClearDecalEntity(this.projector);
            }
            this.ClearUserValue();
            this.ReleaseShadowRenderer();
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

            // Shadow Mesh指定時は内部のRendererでDecal Mapに描画する
            if (this.shadowMesh != null)
                this.SetupShadowRenderer(in param);
            else
                this.ReleaseShadowRenderer();

            this.shadowIndex = param.index;
            this.ApplyUserValue(param.userValueShift);
            FakeShadowManager.SetShadowMatrix(this.shadowIndex, this.projection * this.view);
            FakeShadowManager.SetShadowClip(this.shadowIndex, this.gridClipping);

            // NOTE: renderingLayerMaskのsetterはDecalの描画データを更新しないので、更新が走るuvScale/uvBiasより先に設定する
            this.projector.renderingLayerMask = param.receiverRenderingLayers;
            this.projector.uvScale = param.uvScale;
            this.projector.uvBias = param.uvBias;
            // FakeShadow以外で無効化された場合も古いハンドルが残っているので消してから有効化する
            ClearDecalEntity(this.projector);
            this.projector.enabled = true;

            this.state |= STATE.AVAILABLE;
        }

        /// <summary>
        /// 無効なDecalProjectorに残るURPのDecal管理のハンドルを消す (URP内部API、URPRuntime.asmrefで参照)
        /// URPのDecal管理 (SharedDecalEntityManager) はRendererの再生成やPlay Mode開始で作り直され、
        /// その時点で無効なProjectorは登録されずに古い管理のハンドル (index, version) を持ったまま残る
        /// ハンドルは管理毎に同じ番号から振り直されるので、有効化した時に他のProjectorのハンドルと一致すると
        /// 登録済みと判定されて描画されない (その状態で無効化すると他のProjectorの登録を消す)
        /// 既定値 (version 0) はどの管理でも無効と判定されるので、次の有効化で必ず新規に登録される
        /// NOTE: 登録中 (isActiveAndEnabled) のProjectorのハンドルは消さないこと (登録が残ったまま参照を失う)
        /// </summary>
        private static void ClearDecalEntity(DecalProjector projector) {
            if (!projector.isActiveAndEnabled)
                projector.decalEntity = default;
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
        /// 割当インデックス+1をUser Valueの指定位置の5bitに書き込む（他のbitは保持する）
        /// Shadow Mesh指定時は内部のRendererにのみ書き込み、元のRendererには触れない
        /// </summary>
        private void ApplyUserValue(int shift) {
            var target = this.drawRenderer;
            var value = GetUserValue(target);
            if (this.userValueShift >= 0)
                value &= ~(FakeShadowManager.USER_VALUE_MASK << this.userValueShift);
            value &= ~(FakeShadowManager.USER_VALUE_MASK << shift);
            value |= (uint)(this.shadowIndex + 1) << shift;
            this.SetUserValue(target, value);
            this.userValueShift = shift;
        }

        /// <summary>
        /// User Valueから割当インデックスを消去する
        /// </summary>
        private void ClearUserValue() {
            var target = this.drawRenderer;
            if (this.userValueShift >= 0 && target != null)
                this.SetUserValue(target, GetUserValue(target) & ~(FakeShadowManager.USER_VALUE_MASK << this.userValueShift));
            this.userValueShift = -1;
            this.shadowIndex = -1;
        }

        /// <summary>
        /// Decal Mapに描画するRenderer (Shadow Mesh指定時は内部のRenderer)
        /// </summary>
        private Renderer drawRenderer { get => this.shadowRenderer != null ? this.shadowRenderer : this.renderer; }

        /// <summary>
        /// Shadow Meshの検証 (割当前にFakeShadowManagerから呼ぶ)
        /// SubMesh数・Material数・頂点数は元のMeshと一致しなくてよいが、スケルトン (ボーン数・順序) は一致させること
        /// </summary>
        /// <returns>Shadow Meshで描画できる</returns>
        internal bool ValidateShadowMesh() {
            if (this.shadowMesh == null)
                return true;
            if (this.renderer == null)
                this.renderer = this.GetComponent<Renderer>();
            if (!(this.renderer is SkinnedMeshRenderer source)) {
                Debug.LogError($"Shadow Mesh supports only SkinnedMeshRenderer : {this.name}", this);
                return false;
            }
            var boneCount = source.bones.Length;
            if (this.shadowMesh.bindposeCount != boneCount) {
                Debug.LogError($"Shadow Mesh [{this.shadowMesh.name}] does not match the skeleton of the renderer (bindposes: {this.shadowMesh.bindposeCount}, bones: {boneCount}) : {this.name}", this);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Shadow Mesh描画用の内部Rendererの生成・更新
        /// Setupは割当数の変更やShader変更でStopを経ずに何度も呼ばれるので、生成済みならMaterial・Layerのみ更新する
        /// </summary>
        private void SetupShadowRenderer(in FakeShadowManager.MatParam param) {
            var source = (SkinnedMeshRenderer)this.renderer; // ValidateShadowMeshで検証済み
            if (this.shadowRenderer == null) {
                // 元のRendererに書き込み済みのインデックスを消す（Shadow Meshの指定が稼働中に変更された場合）
                if (this.userValueShift >= 0)
                    this.SetUserValue(source, GetUserValue(source) & ~(FakeShadowManager.USER_VALUE_MASK << this.userValueShift));

                // ボーンを共有するので親子関係は不要
                // 親を持たないことで、元のGameObjectのSetActive(false)やInstantiateによる複製の影響を受けない
                var go = new GameObject($"{this.name} (FakeShadow)");
                go.hideFlags = HideFlags.HideAndDontSave;
                // 加算Scene・Prefab ModeのPreview Sceneで描画対象のSceneを揃える
                // NOTE: HideAndDontSaveのGameObjectはSceneのアンロードでは破棄されないので、必ずStopで破棄する
                var scene = this.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded && go.scene != scene)
                    SceneManager.MoveGameObjectToScene(go, scene);

                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.bones = source.bones;
                smr.rootBone = source.rootBone;
                smr.sharedMesh = this.shadowMesh;
                smr.localBounds = source.localBounds;
                smr.updateWhenOffscreen = source.updateWhenOffscreen;
                // Decal Mapへの描画専用
                // NOTE: ShadowsOnlyはカメラの描画リストから外れるのでDecal Mapにも描画されない
                smr.shadowCastingMode = ShadowCastingMode.Off;
                smr.receiveShadows = false;
                smr.lightProbeUsage = LightProbeUsage.Off;
                smr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                smr.skinnedMotionVectors = false;
                smr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                this.shadowRenderer = smr;
            } else if (this.shadowRenderer.sharedMesh != this.shadowMesh) {
                this.shadowRenderer.sharedMesh = this.shadowMesh;
            }

            // overrideMaterialで描画するので、対象のPassを持つ共通Materialを割り当てる
            // NOTE: Materialは何であってもoverrideMaterialに差し替わるのでSRP Batcherのバッチは分かれない
            var materials = new Material[this.shadowMesh.subMeshCount];
            for (var i = 0; i < materials.Length; ++i)
                materials[i] = param.material;
            this.shadowRenderer.sharedMaterials = materials;
            this.shadowRenderer.gameObject.layer = param.shadowCasterLayer;
        }

        /// <summary>
        /// Shadow Mesh描画用の内部Rendererの破棄
        /// </summary>
        private void ReleaseShadowRenderer() {
            var smr = this.shadowRenderer;
            this.shadowRenderer = null;
            if (smr == null)
                return;
            var go = smr.gameObject;
            if (Application.isPlaying) {
                // Destroyはフレーム末まで残るので、それまで描画されないように無効化する
                go.SetActive(false);
                Destroy(go);
            } else {
                DestroyImmediate(go);
            }
        }

        private static uint GetUserValue(Renderer renderer) {
            if (renderer is SkinnedMeshRenderer smr)
                return smr.GetShaderUserValue();
            if (renderer is MeshRenderer mr)
                return mr.GetShaderUserValue();
            return 0u;
        }

        private void SetUserValue(Renderer renderer, uint value) {
            if (renderer is SkinnedMeshRenderer smr)
                smr.SetShaderUserValue(value);
            else if (renderer is MeshRenderer mr)
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
