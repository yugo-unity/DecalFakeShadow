Shader "UTJ/FakeShadowCaster"
{
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Line("Index", Float) = 0
        [ShowAsVector2] _Offset("Scale", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags {"RenderType" = "Opaque" "IgnoreProjector" = "True" "RenderPipeline" = "UniversalPipeline" "ShaderModel" = "4.5"}
        LOD 100

        Blend One Zero
        Cull Back
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "Forward"
            Tags{"LightMode" = "FakeShadow"}

            HLSLPROGRAM
            #pragma exclude_renderers gles gles3 glcore
            #pragma target 4.5

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float _Line;
            float4 _Offset;
CBUFFER_END
            //float4x4 _ModelMat;
            float4x4 _ViewMat;
            float4x4 _ProjMat;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;

                // モデル行列は単位行列なので乗算不要
                //float4 pos = mul(mul(_ProjMat, mul(_ViewMat, ModelMat)), float4(input.positionOS.xyz, 1.0));
                float4 pos = mul(mul(_ProjMat, _ViewMat), float4(input.positionOS.xyz, 1.0));

                // 似非Viewport計算によるグリッド対応
                pos.xyz /= pos.w;
                pos.xy /= _Line;      // 左下
                pos.xy += _Offset.xy; // 指定位置
                pos.xyz *= pos.w;

                output.positionCS = pos;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _BaseColor;
            }
            ENDHLSL
        }
    }
}
