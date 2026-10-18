// Fake Shadow Caster
// FakeShadowPassFeatureがDrawingSettings.overrideMaterialとして全Characterを1つのMaterialで描画する
// Character毎のデータはRenderer User Value (Mesh/SkinnedMeshRenderer.SetShaderUserValue) のインデックスで定数バッファ(FakeShadowData)から引く
// Material毎の値を持たないのでSRP Batcherで1つのバッチにまとまる
// FakeShadowDataはFakeShadowPassFeatureのPass内でGraphicsBufferとして丸ごとバインドされる (値毎の解決・コピーが発生しない)
// Decal Map (R8) には影の被覆率のみを書き込む
Shader "UTJ/FakeShadowCaster"
{
    Properties
    {
    }

    SubShader
    {
        Tags {"RenderType" = "Opaque" "IgnoreProjector" = "True" "RenderPipeline" = "UniversalPipeline" "ShaderModel" = "4.5"}
        LOD 100

        Pass
        {
            Name "FakeShadow"
            Tags{"LightMode" = "FakeShadow"}

            Blend One Zero
            Cull Back
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma exclude_renderers gles gles3 glcore
            #pragma target 4.5

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define FAKE_SHADOW_DATA_COUNT 32        // FakeShadowManager.SHADOW_DATA_COUNT (User Valueの5bitで引ける数)
            #define FAKE_SHADOW_USER_VALUE_MASK 0x1F // FakeShadowManager.USER_VALUE_MASK

            // レイアウトはFakeShadowManager.WriteShadowDataと合わせる (std140相当、計2064byte)
            CBUFFER_START(FakeShadowData)
                float4x4 _FakeShadowMatrices[FAKE_SHADOW_DATA_COUNT]; // Projection * View、未使用と末尾(番兵)はクリップ範囲外に置く行列
                float _FakeShadowLine;                                // グリッドの分割数 (1辺)
                float _FakeShadowMapTexel;                            // 1 / Decal Mapの解像度
                uint _FakeShadowClipMask;                             // Grid Clipping有効フラグ (bit i: インデックスiの影)
                int _FakeShadowUserValueShift;                        // User Valueの何bit目から5bitを使うか
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 screenPos : TEXCOORD0;
                nointerpolation float4 clipRect : TEXCOORD1; // xy: 最小, zw: 幅 (screenPosの0~1の座標)
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                // User Valueは割当インデックス+1 (0はFakeShadow未割当)
                // 分岐を避けて5bitに丸め、未割当(0)は末尾の番兵(31)、未使用のインデックスも番兵と同じ行列でクリップ範囲外に置く
                uint index = ((unity_RendererUserValue >> (uint)_FakeShadowUserValueShift) - 1u) & FAKE_SHADOW_USER_VALUE_MASK;

                // グリッド上のセル (floatの剰余の誤差を避けて整数で計算する)
                uint gridLine = (uint)_FakeShadowLine;
                float2 cell = float2(index % gridLine, index / gridLine); // x: 列, y: 行

                float4 pos = mul(_FakeShadowMatrices[index], mul(unity_ObjectToWorld, float4(input.positionOS.xyz, 1)));

                // 似非Viewport計算によるグリッド対応
                // NOTE: クリップ空間(-1~1)ではインデックスが増えると下の行に置く (yは下向き)
                float block = 2.0 / _FakeShadowLine;
                float2 offset = float2(-1.0 + block * (cell.x + 0.5), 1.0 - block * (cell.y + 0.5));
                pos.xyz /= pos.w;
                pos.xy /= _FakeShadowLine; // グリッド分割数なので0が来ることはない
                pos.xy += offset;          // 指定位置
                pos.xyz *= pos.w;

                output.positionCS = pos;
                output.screenPos.xy = ComputeScreenPos(pos).xy;

                // Grid Clippingの矩形 (Bilinearによってはみ出してくるので1pix内側で切る)
                // NOTE: ComputeScreenPosの座標 (_ProjectionParams.xによる反転込み) ではyは上向き
                // 分岐を避けて常に矩形でクリップし、無効な影にはDecal Map全体を覆う矩形を使う
                float uvScale = 1.0 / _FakeShadowLine;
                float4 cellRect = float4(uvScale * cell + _FakeShadowMapTexel, (uvScale - 2.0 * _FakeShadowMapTexel).xx);
                bool clipEnabled = (_FakeShadowClipMask >> index) & 1u;
                output.clipRect = clipEnabled ? cellRect : float4(-1, -1, 3, 3);

                // TIPS: ハードウェアクリップ (未採用)
                //   セルの4辺までの距離をSV_ClipDistanceで出力すると、ラスタライザが三角形を切るので
                //   ピクセルシェーダーのclipが不要になり、セル外のピクセルも生成されない (無効時は大きな正の値を出す)
                //   - DX11+/Vulkan/Metalは対応、GLESは拡張が必要 (本シェーダーはgles/gles3/glcoreを除外済み)
                //   - Unityのクロスコンパイルでの出力は未確認
                //   - 頂点側の計算と補間値が4つ増える
                //   Decal Mapは小さくピクセル側のコストはもともと小さいので、実機で効果を計測してから採用を判断すること

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Grid Clipping
                float4 rect = input.clipRect;
                clip(step(rect.x, input.screenPos.x) * step(input.screenPos.x, rect.x + rect.z) *
                    step(rect.y, input.screenPos.y) * step(input.screenPos.y, rect.y + rect.w) - 0.001);
                // Decal Map(R8)には被覆率のみを書く、色はFakeShadowByDecalで_FakeShadowColorから取る
                return 1;
            }
            ENDHLSL
        }
    }
}
