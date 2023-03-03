
// ShaderGraphのDecalシェーダーからFake Shadowに必要なもののみに削ぎ落す
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

            // Debug
            // <None>

            // --------------------------------------------------
            // Pass

            HLSLPROGRAM

            // Pragmas
            #pragma target 2.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma editor_sync_compilation

            // create shader variant
            #pragma shader_feature_local_fragment DECAL_ANGLE_FADE

            // Includes
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderVariablesDecal.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/NormalReconstruction.hlsl"

            // --------------------------------------------------
            // Structs and Packing

            struct Attributes
            {
                 float3 positionOS : POSITION;
                 float4 uv0 : TEXCOORD0;
#if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : INSTANCEID_SEMANTIC;
#endif
            };
            struct Varyings
            {
                 float4 positionCS : SV_POSITION;
                 float4 texCoord0;
#if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : CUSTOM_INSTANCE_ID;
#endif
            };
            struct PackedVaryings
            {
                 float4 positionCS : SV_POSITION;
                 float4 interp0 : INTERP0;
#if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : CUSTOM_INSTANCE_ID;
#endif
            };

            PackedVaryings PackVaryings(Varyings input)
            {
                PackedVaryings output;
                ZERO_INITIALIZE(PackedVaryings, output);
                output.positionCS = input.positionCS;
                output.interp0.xyzw = input.texCoord0;
#if UNITY_ANY_INSTANCING_ENABLED
                output.instanceID = input.instanceID;
#endif
                return output;
            }

            Varyings UnpackVaryings(PackedVaryings input)
            {
                Varyings output;
                output.positionCS = input.positionCS;
                output.texCoord0 = input.interp0.xyzw;
#if UNITY_ANY_INSTANCING_ENABLED
                output.instanceID = input.instanceID;
#endif
                return output;
            }


            // --------------------------------------------------
            // Graph

            CBUFFER_START(UnityPerMaterial)
            float4 _DecalTexture_TexelSize;
            float4 _Base_Color;
            CBUFFER_END

            //SAMPLER(SamplerState_Linear_Repeat);
            TEXTURE2D(_DecalTexture);
            SAMPLER(sampler_DecalTexture);

            // --------------------------------------------------
            // Functions

            half4 GetSurfaceColor(Varyings input, uint2 positionSS, float angleFadeFactor)
            {
                half4x4 normalToWorld = UNITY_ACCESS_INSTANCED_PROP(Decal, _NormalToWorld);
                half fadeFactor = clamp(normalToWorld[0][3], 0.0f, 1.0f) * angleFadeFactor;
                float2 scale = float2(normalToWorld[3][0], normalToWorld[3][1]);
                float2 offset = float2(normalToWorld[3][2], normalToWorld[3][3]);
                input.texCoord0.xy = input.texCoord0.xy * scale + offset;

                UnityTexture2D _Property_Out_0 = UnityBuildTexture2DStructNoScale(_DecalTexture);
                float4 _SampleTexture2D_RGBA_0 = SAMPLE_TEXTURE2D(_Property_Out_0.tex, _Property_Out_0.samplerstate, _Property_Out_0.GetTransformedUV(input.texCoord0.xy));
                float3 surfaceColor = _SampleTexture2D_RGBA_0.rgb * _Base_Color.rgb;
                float surfaceAlpha = _SampleTexture2D_RGBA_0.a;

                return half4(surfaceColor.r, surfaceColor.g, surfaceColor.b, surfaceAlpha * fadeFactor);
            }

            // --------------------------------------------------
            // Main

            Varyings BuildVaryings(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);

                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // TODO: Avoid path via VertexPositionInputs (Universal)
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

                // Returns the camera relative position (if enabled)
                float3 positionWS = TransformObjectToWorld(input.positionOS);

                output.positionCS = TransformWorldToHClip(positionWS);

                output.texCoord0 = input.uv0;

#ifdef EDITOR_VISUALIZATION
                float2 VizUV = 0;
                float4 LightCoord = 0;
                UnityEditorVizData(input.positionOS, input.uv0, input.uv1, input.uv2, VizUV, LightCoord);
#endif

                return output;
            }

            PackedVaryings Vert(Attributes inputMesh)
            {
                Varyings output = (Varyings)0;
                output = BuildVaryings(inputMesh);

                PackedVaryings packedOutput = (PackedVaryings)0;
                packedOutput = PackVaryings(output);

                return packedOutput;
            }

            void Frag(PackedVaryings packedInput, out half4 outColor : SV_Target0)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(packedInput);
                UNITY_SETUP_INSTANCE_ID(packedInput);
                Varyings input = UnpackVaryings(packedInput);

                half angleFadeFactor = 1.0;

                float2 positionCS = input.positionCS.xy;

                // Only screen space needs flip logic, other passes do not setup needed properties so we skip here
                TransformScreenUV(positionCS, _ScreenSize.y);

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
                input.texCoord0.xy = texCoord;

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

                outColor = GetSurfaceColor(input, (uint2)positionSS, angleFadeFactor);
            }
            ENDHLSL
        }
    }
    //FallBack "Hidden/Shader Graph/FallbackError"
}