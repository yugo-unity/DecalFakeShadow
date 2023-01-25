Shader "UTJ/FakeShadowCaster"
{
    Properties
    {
        [HideInInspector] _FakeShadowOffset("Scale", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FakeClipRect("Clip", Vector) = (0, 0, 0, 0)
        [Toggle(FAKE_CLIP)]_FakeClip("Fake Shadow Clipping", Float) = 1
    }

    SubShader
    {
        Tags {"RenderType" = "Opaque" "IgnoreProjector" = "True" "RenderPipeline" = "UniversalPipeline" "ShaderModel" = "4.5"}
        LOD 100

        Pass
        {
            Name "ShadowCaster"
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
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            #pragma multi_compile_local _ FAKE_CLIP

CBUFFER_START(UnityPerMaterial)
            float4 _FakeShadowOffset;
            float4 _FakeClipRect;
            float4x4 _FakeShadowView;
            float4x4 _FakeShadowProj;
CBUFFER_END

            // Global properties
            float _FakeShadowLine;
            half4 _FakeShadowColor;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
#ifdef FAKE_CLIP
                float2 screenPos : TEXCOORD0;
#endif
            };

            Varyings vert(Attributes input)
            {
                Varyings output;

                float4 pos = mul(mul(_FakeShadowProj, mul(_FakeShadowView, unity_ObjectToWorld)), float4(input.positionOS.xyz, 1));

                // 似非Viewport計算によるグリッド対応
                pos.xyz /= pos.w;
                pos.xy /= _FakeShadowLine;      // グリッド分割数なので0が来ることはない
                pos.xy += _FakeShadowOffset.xy; // 指定位置
                pos.xyz *= pos.w;

                output.positionCS = pos;

#ifdef FAKE_CLIP
                output.screenPos.xy = ComputeScreenPos(pos).xy;
#endif

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 color = _FakeShadowColor;
#ifdef FAKE_CLIP
                clip(step(_FakeClipRect.x, input.screenPos.x)* step(input.screenPos.x, _FakeClipRect.x + _FakeClipRect.z)*
                    step(_FakeClipRect.y, input.screenPos.y)* step(input.screenPos.y, _FakeClipRect.y + _FakeClipRect.w) - 0.001);
#endif
                return color;
            }
            ENDHLSL
        }
    }
}
