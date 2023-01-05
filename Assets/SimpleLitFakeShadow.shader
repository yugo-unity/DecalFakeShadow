Shader "UTJ/SimpleLitFakeShadow"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map (RGB) Smoothness / Alpha (A)", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        _Cutoff("Alpha Clipping", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SpecColor("Specular Color", Color) = (0.5, 0.5, 0.5, 0.5)
        _SpecGlossMap("Specular Map", 2D) = "white" {}
        _SmoothnessSource("Smoothness Source", Float) = 0.0
        _SpecularHighlights("Specular Highlights", Float) = 1.0

        [HideInInspector] _BumpScale("Scale", Float) = 1.0
        [NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}

        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0)
        [NoScaleOffset]_EmissionMap("Emission Map", 2D) = "white" {}

        // Blending state
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        // Editmode props
        _QueueOffset("Queue offset", Float) = 0.0

        // ObsoleteProperties
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _Shininess("Smoothness", Float) = 0.0
        [HideInInspector] _GlossinessSource("GlossinessSource", Float) = 0.0
        [HideInInspector] _SpecSource("SpecularHighlights", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}

        [ShowAsVector2] _FakeShadowOffset("Scale", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags{"RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "UniversalMaterialType" = "Lit" "IgnoreProjector" = "True" "ShaderModel" = "4.5"}

        UsePass "Universal Render Pipeline/Simple Lit/ForwardLit"
        UsePass "Universal Render Pipeline/Simple Lit/DepthOnly"

        Pass
        {
            Name "ShadowCaster"
            Tags{"LightMode" = "FakeShadow"}

            ZWrite Off
            Cull[_Cull]

            HLSLPROGRAM
            #pragma exclude_renderers gles gles3 glcore
            #pragma target 4.5

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            //#include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"

// SRP Batcher対応させたい場合はForwardLit/DepthOnly passの定義と合わせる必要がある
// 今回はサンプルなので非対応
//CBUFFER_START(UnityPerMaterial)
            float4 _FakeShadowOffset;
//CBUFFER_END

            // Global properties
            float _FakeShadowLine;
            half4 _FakeShadowColor;
            //float4x4 _ModelMat;
            float4x4 _FakeShadowView;
            float4x4 _FakeShadowProj;

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
                //float4 pos = mul(mul(_FakeShadowProj, mul(_FakeShadowView, ModelMat)), float4(input.positionOS.xyz, 1.0));
                float4 pos = mul(mul(_FakeShadowProj, _FakeShadowView), float4(input.positionOS.xyz, 1.0));

                // 似非Viewport計算によるグリッド対応
                pos.xyz /= pos.w;
                pos.xy /= _FakeShadowLine;      // グリッド分割数なので0が来ることはない
                pos.xy += _FakeShadowOffset.xy; // 指定位置
                pos.xyz *= pos.w;

                //pos = mul(UNITY_MATRIX_VP, mul(UNITY_MATRIX_M, float4(input.positionOS.xyz, 1.0)));

                output.positionCS = pos;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _FakeShadowColor;
            }
            ENDHLSL
        }
    }

    Fallback  "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.SimpleLitShader"
}
