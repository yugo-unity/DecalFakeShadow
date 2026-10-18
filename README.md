# DecalFakeShadow

URP の Decal を使った Blob Shadow / Fake Shadow のサンプルです。

Fake Shadow は Blob Shadow と同じく、真上から落とす（Top-Down）擬似的な影です。キャラクターの形を影用テクスチャに描き、Decal Projector で地面に投影します。

![image](https://user-images.githubusercontent.com/57246289/202939728-7e953744-3027-43f2-b39e-61908bb2d6fe.png)

> 以下のスクリーンショットは旧バージョンの UI です。設定項目の名前と構成はこの README の本文を参照してください。

# 動作環境

- Unity 6000.3 / URP 17.3 (**RenderGraph**)
- Forward / Forward+ only
- `Assets/_DecalAssets/URPRuntime.asmref` 利用
  - URP の internal API（`ScriptableRendererFeature.RequireRenderingLayers`）を上書きするため（"URP の DepthNormals Prepass" を参照）。

# Setup

- Renderer に **Decal Renderer Feature** と **FakeShadowPassFeature** を追加します（Decal は URP 標準の Renderer Feature）。
- Decal Renderer Feature の **Use Rendering Layers を有効にしてください（必須）**。
- 影専用の Rendering Layer を 1 つ用意し、FakeShadowPassFeature の **Shadow Caster Rendering Layer** に設定します。
- キャラクター（影を落とす側）の Rendering Layer Mask を **Receiver Rendering Layers** から外します（自分の影を受けないようにするため）。
- Decal Projector をシーンに追加し、Material を `FakeShadowByDecal` にします。
　
![image](https://user-images.githubusercontent.com/57246289/212609523-417e78ff-c16c-4dec-9e75-c6ca53786937.png)
![image](https://user-images.githubusercontent.com/57246289/212609901-544a4999-5457-4c3e-9dca-e8be8b43e3cc.png)

## FakeShadowPassFeature の設定項目

- **Fake Shadow Shader**: キャラクターの Material をこの Shader で上書きして（overrideMaterial）影用テクスチャに描画します。
- **User Value Shift**: Renderer の User Value（32bit）のうち、FakeShadow が使う 5bit の開始位置（0〜27）です。5bit には割り当てたインデックス + 1 を書き込みます（0 は未割り当て扱い）。
- **Shadow Caster Rendering Layer**: 影用テクスチャに描く Renderer の目印にする Rendering Layer です。
  - FakeShadow は、影が割り当てられている間だけ、描画に使う Renderer にこのビットを付与し、停止時に外します。影用テクスチャへの描画はこのビットで絞り込むので、Sleep した影や、割当数を超えて Cancel された影は描かれません。ユーザーが Renderer に明示的に付ける必要はありません。
  - ライトや他の Decal と共有しない専用の Rendering Layer を用意してください。
- **Receiver Rendering Layers**: 影を受ける Rendering Layer です。FakeShadow が管理する Decal Projector の Rendering Layer Mask をこの値で上書きします。
- **Shadow Color**: 影の色です。Decal Projector の Material（`FakeShadowByDecal`）の Base Color と乗算します。Alpha は影の濃さです。
- **Max Shadow Count / Decal Map Size**: 同時に表示できる影の最大数と、すべての影で共有する影用テクスチャの解像度です。影は 1 枚のテクスチャをグリッドに分割して描くので、最大数を増やすと影 1 つあたりの解像度が下がります。
  - 最大数の上限は 25（5x5 のグリッド）です。
- **Verify ボタン**: Shadow Caster Rendering Layer のビットが意図せず残っているオブジェクトをコンソールに列挙します（後述）。
- FakeShadowPassFeature が複数ある場合は、先頭の Feature の設定を使います。

## Fake Shadow

キャラクターの Renderer（SkinnedMeshRenderer / MeshRenderer）と同じ GameObject に **FakeShadow** コンポーネントを追加し、Projector に対応する Decal Projector を設定します。

- キャラクターのシェーダーに専用のパスは不要です（URP 標準の Lit / Simple Lit、ShaderGraph などをそのまま使えます）。
  - 影用テクスチャへの描画は共通の Material で上書きして行い、キャラクターごとのデータは Renderer の User Value でインデックスを渡します。SRP Batcher で 1 つのバッチにまとまります。
  - Rendering Layer を書き出すため、DepthNormals パスを持つシェーダーが必要です（キャラクターも影を受ける側も。URP 標準のシェーダーは持っています）。
- 1 つの GameObject に付けられる FakeShadow は 1 つだけです（User Value と Shadow Caster Rendering Layer のビットを共有するため）。
　
![image](https://user-images.githubusercontent.com/57246289/212609941-19730ade-c925-4698-934a-90185b77b7bc.png)

### FakeShadow の設定項目

- **Projector**: この影を描く Decal Projector です。
- **Active On Enable**: OnEnable で自動的に `Wakeup()` します。Edit Mode（Prefab Mode を含む）では切り替えをその場で反映します（無効にすると影を停止します）。
- **Is Static**: Projector が動かない場合に有効にします（View 行列の更新を省きます）。
- **Shadow Mesh**: 影用テクスチャに、この Renderer の Mesh の代わりに描く Mesh です（後述）。未指定なら Renderer の Mesh で描きます。
- **Grid Clipping**: 影用テクスチャの自分のセルからはみ出した部分を切ります（はみ出すと隣の影のセルに描き込まれます）。既定で有効です。

### Shadow Mesh

処理負荷を下げるため、影専用の Shadow Mesh を設定できます。Shadow Mesh は 1 Mesh かつ低ポリゴンで、Weight の付いたボーンも少ないことが望ましいです。

- FakeShadow を元のキャラクターの SkinnedMeshRenderer に付け、Shadow Mesh に影用の Mesh を指定します。
- 指定すると、FakeShadow が内部で非表示の SkinnedMeshRenderer を 1 つ生成し、元の Renderer のボーンを共有して描画します。この Renderer は Hierarchy に表示されず、シーンにも保存されず、Sleep や無効化で破棄されます。
- **スケルトン（ボーンの数と順序）は元の Renderer と一致させてください。** SubMesh 数・Material 数・頂点数は一致しなくてかまいません。
- SkinnedMeshRenderer のみ対応します（MeshRenderer に付けて Shadow Mesh を指定するとエラーになります）。
- 内部の Renderer は元の Renderer と同じ GameObject の Layer に置かれ（カメラの Culling Mask で同じ扱いになります）、Rendering Layer は Shadow Caster Rendering Layer のビットだけを持ちます。User Value とビットは内部の Renderer にだけ書き込みます（元の Renderer には触れません）。
- 稼働中に変更した場合は、次に再設定されたとき（Wakeup、割当数の変更など）に反映されます。

### Decal Technique

- DBuffer は BaseColor に混ぜてからライティングされるので、明るいライトの下では影が薄く見えます。最終色を直接暗くする Screen Space の方が影らしく見える傾向があります。
- Technique のランタイムでの切り替えには対応しません。Editor 上での切り替え（Play 中を含む）には追従します。
　
# 注意事項

## URP の DepthNormals Prepass

- URP は、すべての Renderer Feature の「Rendering Layer をいつ書き出すか」を集計し、早い方（DepthNormalPrePass）を採用します。Rendering Layer を使わない Feature の既定値も集計に含まれ、その既定値は DepthNormalPrePass です。
- そのため Screen Space + Use Rendering Layers でも、他の Feature があるだけで DepthNormals Prepass が発生し、不要なNormal Textureを生成します。
- FakeShadowPassFeature は Assembly Defnition Referenceを利用し、internal の `RequireRenderingLayers` を上書きし、不要な DepthNormals Prepass の発生を回避しています。
- ただし、同じ Renderer に他の独自 Renderer Feature が同様に上書きをしていなければ、不要な DepthNormals Prepass が再び発生する可能性があります。
- 本件はURPの仕様（設計上の課題）となります。
- DBuffer では DepthNormals Prepass は設計上必須です（DBuffer のパスが法線を必要とするため）。

## Shadow Caster Rendering Layer のビットが残る場合と Verify

- ShadowMesh を使わない影は、シーン上の Renderer にビットを付けます。そのため、Edit Mode でシーンや Prefab を保存すると、ビットも保存されます。
- ビットが付いたまま読み込まれた・複製された Renderer は、FakeShadow が一度起動すれば停止時に外れます。外れないまま画面に映ると、影用テクスチャへの描画呼び出しと頂点処理が無駄に発生します（ピクセルは描かれないので暗黙的なパフォーマンスコストとなります）。該当するのは次の場合です。
  1. FakeShadow が起動されない（インスタンス側で Active On Enable を無効にオーバーライドしている、スクリプトから起動しない、など）。
  2. FakeShadow のコンポーネントだけが無効で、Renderer は表示されている（OnEnable / OnDisable が呼ばれない）。
- FakeShadowPassFeature の **Verify** ボタンで、この不要な頂点処理のコストが発生しているRendererを検出します（Play Mode 中での利用を想定しています）。対象は読み込み中のすべてのシーンです（Play Mode 中は DontDestroyOnLoad のシーン、Prefab Mode 中はその Prefab のシーンも含み、非アクティブなオブジェクトも含みます）。検出するもの:
  - ビットを持つが、どの FakeShadow からも参照されていない Renderer。
  - ビットを持つが、影が割り当てられていない FakeShadow の Renderer。
  - Shadow Mesh を指定した FakeShadow の元の Renderer にビットがある場合。
  - マスクにこのビットを含むライト（Rendering Layers と Custom Shadow Layers）と、FakeShadow 以外の Decal Projector。影が割り当てられている間だけ、キャラクターにそのライトが当たる・Decal が投影されるので、見た目が切り替わります。定義済みのすべてのビットを含むマスク（Everything）は問題にならないので除外します。
- 検出のみであり自動で修正する機能はありません。

## カリング

- 影を落とす Renderer（Shadow Mesh の内部 Renderer を含む）が画面外でカリングされると、影が画面内にあっても消えます。
- 必要に応じて Renderer の Bounds を広げる、`updateWhenOffscreen` を使う、などで対処してください。

## 制約

- 対象のタグ（UniversalForward / UniversalForwardOnly / SRPDefaultUnlit / FakeShadow）のパスを複数持つシェーダーは、その数だけ影用テクスチャに描画されます。
- GPU Resident Drawer / BatchRendererGroup 経由の Renderer は未検証です。
