// Fake Shadow Caster
// FakeShadowPassFeatureがDrawingSettings.overrideMaterialとして全Characterを1つのMaterialで描画する
// Character毎のデータはRenderer User Value (Mesh/SkinnedMeshRenderer.SetShaderUserValue) のインデックスでグローバル配列から引く
// Material毎の値を持たないのでSRP Batcherで1つのバッチにまとまる
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

            #define FAKE_SHADOW_LIMIT 25 // FakeShadowManager.SHADOW_LIMIT

CBUFFER_START(UnityPerMaterial)
CBUFFER_END

            // Global properties
            float _FakeShadowLine;
            half4 _FakeShadowColor;
            int _FakeShadowUserValueShift;                   // User Valueの何bit目から8bitを使うか
            float4x4 _FakeShadowMatrices[FAKE_SHADOW_LIMIT]; // Projection * View
            float4 _FakeShadowOffsets[FAKE_SHADOW_LIMIT];    // xy: グリッド位置, z: Clip有効
            float4 _FakeShadowClipRects[FAKE_SHADOW_LIMIT];

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 screenPos : TEXCOORD0;
                nointerpolation uint index : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                // 0はFakeShadow未割当
                uint userValue = (unity_RendererUserValue >> (uint)_FakeShadowUserValueShift) & 0xFF;
                if (userValue == 0 || userValue > FAKE_SHADOW_LIMIT)
                {
                    output.positionCS = float4(0, 0, -2, 1); // clip out
                    return output;
                }
                uint index = userValue - 1;

                float4 pos = mul(_FakeShadowMatrices[index], mul(unity_ObjectToWorld, float4(input.positionOS.xyz, 1)));

                // 似非Viewport計算によるグリッド対応
                pos.xyz /= pos.w;
                pos.xy /= _FakeShadowLine;              // グリッド分割数なので0が来ることはない
                pos.xy += _FakeShadowOffsets[index].xy; // 指定位置
                pos.xyz *= pos.w;

                output.positionCS = pos;
                output.screenPos.xy = ComputeScreenPos(pos).xy;
                output.index = index;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Grid Clipping
                if (_FakeShadowOffsets[input.index].z > 0.5)
                {
                    float4 rect = _FakeShadowClipRects[input.index];
                    clip(step(rect.x, input.screenPos.x) * step(input.screenPos.x, rect.x + rect.z) *
                        step(rect.y, input.screenPos.y) * step(input.screenPos.y, rect.y + rect.w) - 0.001);
                }
                return _FakeShadowColor;
            }
            ENDHLSL
        }
    }
}
