
// ShaderGraphのDecalシェーダーからFake Shadowに必要なもののみに削ぎ落す
// Decal Technique: Screen Space / DBuffer 両対応、Decal Layers(Use Rendering Layers)対応
Shader "UTJ/FakeShadowByDecal"
{
    Properties
    {
        //[NoScaleOffset] Base_Map("Base Map", 2D) = "white" {} // _DecalTextureで飛んでくる
        _Base_Color("Base Color", Color) = (0, 0, 0, 0)
        [Toggle(DECAL_ANGLE_FADE)]_DecalAngleFadeSupported("Decal Angle Fade Supported", Float) = 1
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            // RenderType: <None>
            "PreviewType" = "Plane"
            // Queue: <None>
        }

        HLSLINCLUDE

        // Includes
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderVariablesDecal.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/NormalReconstruction.hlsl"
#ifdef _DECAL_LAYERS
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareRenderingLayerTexture.hlsl"
#endif

        // --------------------------------------------------
        // Structs and Packing

        // NOTE: Decal UVはDepthから復元するので、メッシュのUVは使わない
        struct Attributes
        {
             float3 positionOS : POSITION;
#if UNITY_ANY_INSTANCING_ENABLED
             uint instanceID : INSTANCEID_SEMANTIC;
#endif
        };
        struct Varyings
        {
             float4 positionCS : SV_POSITION;
#if UNITY_ANY_INSTANCING_ENABLED
             uint instanceID : CUSTOM_INSTANCE_ID;
#endif
        };


        // --------------------------------------------------
        // Properties

        CBUFFER_START(UnityPerMaterial)
        float4 _Base_Color;
        CBUFFER_END

        // FakeShadowPassFeatureがグローバルに設定する
        // Decal Map (R8) は影の被覆率のみで、色は全Shadow共通の_FakeShadowColorを使う
        // (色をDecal Mapに持たせるとクリア色とのBilinear補間でエッジに暗い縁が出る)
        TEXTURE2D(_DecalTexture);
        SAMPLER(sampler_DecalTexture);
        half4 _FakeShadowColor;

        // --------------------------------------------------
        // Functions

        half4 GetSurfaceColor(float2 texCoord, half angleFadeFactor)
        {
            half4x4 normalToWorld = UNITY_ACCESS_INSTANCED_PROP(Decal, _NormalToWorld);
            half fadeFactor = clamp(normalToWorld[0][3], 0.0f, 1.0f) * angleFadeFactor;
            // DecalProjectorのuvScale/uvBiasでDecal Mapのグリッドを参照する
            float2 scale = float2(normalToWorld[3][0], normalToWorld[3][1]);
            float2 offset = float2(normalToWorld[3][2], normalToWorld[3][3]);

            half coverage = SAMPLE_TEXTURE2D(_DecalTexture, sampler_DecalTexture, texCoord * scale + offset).r;
            return half4(_FakeShadowColor.rgb * _Base_Color.rgb, coverage * _FakeShadowColor.a * fadeFactor);
        }

        // --------------------------------------------------
        // Main

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            // Returns the camera relative position (if enabled)
            float3 positionWS = TransformObjectToWorld(input.positionOS);
            output.positionCS = TransformWorldToHClip(positionWS);

            return output;
        }

        /// <summary>
        /// Decal色の算出 (Screen Space / DBuffer 共通)
        /// </summary>
        /// <param name="screenSpace">Screen SpaceのみUVのFlipが必要</param>
        half4 FragDecal(Varyings input, bool screenSpace)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            UNITY_SETUP_INSTANCE_ID(input);

            half angleFadeFactor = 1.0;

            float2 positionCS = input.positionCS.xy;

            // Only screen space needs flip logic, other passes do not setup needed properties so we skip here
            if (screenSpace)
                TransformScreenUV(positionCS, _ScreenSize.y);

#ifdef _DECAL_LAYERS
            // 受け側のRendering Layerと一致しない場合は破棄
            uint surfaceRenderingLayer = LoadSceneRenderingLayer(positionCS.xy);
            uint projectorRenderingLayer = asuint(UNITY_ACCESS_INSTANCED_PROP(Decal, _DecalLayerMaskFromDecal));
            clip((surfaceRenderingLayer & projectorRenderingLayer) - 0.1);
#endif

#if UNITY_REVERSED_Z
            float depth = LoadSceneDepth(positionCS.xy);
#else
            // Adjust z to match NDC for OpenGL
            float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, LoadSceneDepth(positionCS.xy));
#endif

            float2 positionSS = input.positionCS.xy * _ScreenSize.zw;

            float3 positionWS = ComputeWorldSpacePosition(positionSS, depth, UNITY_MATRIX_I_VP);

            // Transform from relative world space to decal space (DS) to clip the decal
            float3 positionDS = TransformWorldToObject(positionWS);
            positionDS = positionDS * float3(1.0, -1.0, 1.0);

            // call clip as early as possible
            float clipValue = 0.5 - Max3(abs(positionDS).x, abs(positionDS).y, abs(positionDS).z);
            clip(clipValue);

            float2 texCoord = positionDS.xz + float2(0.5, 0.5);

#ifdef DECAL_ANGLE_FADE
    #if defined(_DECAL_NORMAL_BLEND_HIGH)
            half3 normalWS = half3(ReconstructNormalTap9(positionCS.xy));
    #elif defined(_DECAL_NORMAL_BLEND_MEDIUM)
            half3 normalWS = half3(ReconstructNormalTap5(positionCS.xy));
    #else
            half3 normalWS = half3(ReconstructNormalDerivative(input.positionCS.xy));
    #endif

            // Check if this decal projector require angle fading
            half4x4 normalToWorld = UNITY_ACCESS_INSTANCED_PROP(Decal, _NormalToWorld);
            half2 angleFade = half2(normalToWorld[1][3], normalToWorld[2][3]);

            if (angleFade.y < 0.0f) // if angle fade is enabled
            {
                half3 decalNormal = half3(normalToWorld[0].z, normalToWorld[1].z, normalToWorld[2].z);
                half dotAngle = dot(normalWS, decalNormal);
                // See equation in DecalCreateDrawCallSystem.cs - simplified to a madd mul add here
                angleFadeFactor = saturate(angleFade.x + angleFade.y * (dotAngle * (dotAngle - 2.0)));
            }
#endif

            return GetSurfaceColor(texCoord, angleFadeFactor);
        }

        ENDHLSL

        Pass
        {
            Name "DecalScreenSpaceProjector"
            Tags
            {
                "LightMode" = "DecalScreenSpaceProjector"
            }

            // Render State
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            ZTest Greater
            ZWrite Off

            HLSLPROGRAM

            // Pragmas
            #pragma target 2.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation

            // create shader variant
            #pragma shader_feature_local_fragment DECAL_ANGLE_FADE
            #pragma multi_compile_fragment _ _DECAL_LAYERS
            #pragma target 4.5 _DECAL_LAYERS

            void Frag(Varyings input, out half4 outColor : SV_Target0)
            {
                outColor = FragDecal(input, true);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DBufferProjector"
            Tags
            {
                "LightMode" = "DBufferProjector"
            }

            // Render State
            Cull Front
            Blend 0 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha
            Blend 1 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha
            Blend 2 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha
            ZTest Greater
            ZWrite Off
            // BaseColorのみ書き込む (Normal/MAOSは触らない)
            ColorMask RGBA
            ColorMask 0 1
            ColorMask 0 2

            HLSLPROGRAM

            // Pragmas
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation

            // create shader variant
            #pragma shader_feature_local_fragment DECAL_ANGLE_FADE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _DECAL_LAYERS
            #pragma target 4.5 _DECAL_LAYERS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

            void Frag(Varyings input, OUTPUT_DBUFFER(outDBuffer))
            {
                // DBuffer0のalphaは(1-alpha)の積算となり、Opaqueで baseColor * alpha + rgb として合成される
                outDBuffer0 = FragDecal(input, false);
#if defined(_DBUFFER_MRT2) || defined(_DBUFFER_MRT3)
                outDBuffer1 = 0;
#endif
#if defined(_DBUFFER_MRT3)
                outDBuffer2 = 0;
#endif
            }
            ENDHLSL
        }
    }
    //FallBack "Hidden/Shader Graph/FallbackError"
}
