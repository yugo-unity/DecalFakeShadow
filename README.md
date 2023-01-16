# DecalFakeShadow

Blob Shadow / Fake Shadow for URP12(Unity2021)

Fake ShadowはBlob Shadowと同様にTop-DownのCast Shadowを行います.

![image](https://user-images.githubusercontent.com/57246289/202939728-7e953744-3027-43f2-b39e-61908bb2d6fe.png)

# Setup
- ScriptableRendererFeatureとしてDecalとFakeShadowPassを追加します。（DecalはURPのプリセットです）
- Decal Featureの"Technique"を"Screen Space"に、"GBuffer"を無効にします。DBufferやGBufferはサポート外です。
- Fake Shadow Pass Featureの"Character Layer"は影を落とすMeshのLayerを用意し設定します。加えてOpaque Passからは除外します。
![image](https://user-images.githubusercontent.com/57246289/212609523-417e78ff-c16c-4dec-9e75-c6ca53786937.png)
- Decal ProjectorをSceneに追加してMaterialを"FakeShadowByDecal"にします。
![image](https://user-images.githubusercontent.com/57246289/212609901-544a4999-5457-4c3e-9dca-e8be8b43e3cc.png)


## Blob Shadow
  Decal Projectorに"BlobShadowByDecal"またはプリセットのDecal shaderを参照するMaterialを設定し、完了です。

## Fake Shadow (with Shadow-Mesh)
処理負荷の関係上Shadow Meshを別途用意することを推奨します。
Shadow Meshは1 MeshかつLow-Polygon、またWeight Boneも少なくすることが望ましいです。
- Skinned MeshをCtrl+DでDuplicateし、MeshをShadow Meshに差し替えます。
- MaterialはRuntimeで提供される為、設定は不要です。Materialの数はShadow MeshのSub Mesh数に依存します。
- Shadow MeshのGameObjectにFake Shadow Componentを追加します。
- Fake Shadow ComponentのProjectorに対応するDecal Projectorを設定します。
- Fake Shadow ComponentのShadow MeshのToggleをEnableにします。
![image](https://user-images.githubusercontent.com/57246289/212609970-c51e0b7c-02ad-49e7-bbbf-d5bd748ea792.png)

## Fake Shadow
Shadow Meshを準備できない場合、SkinnedMeshのGameObjectにFack Shadow Componentを追加します。
- MeshのShaderにFakeShadow passを追加します。
  本サンプルではSimpleLitFakeShadow shaderを使用していますが、プリセットのCBUFFERを使用するのでSRP Batcherが非対応となっています。
  SRP Batcher対応を行う場合は_FakeShadowOffsetをCBUFFERに追加してください。
- Skinned MeshのGameObjectにFake Shadow Componentを追加します。
- Fake Shadow ComponentのProjectorに対応するDecal Projectorを設定します。
![image](https://user-images.githubusercontent.com/57246289/212609941-19730ade-c925-4698-934a-90185b77b7bc.png)
