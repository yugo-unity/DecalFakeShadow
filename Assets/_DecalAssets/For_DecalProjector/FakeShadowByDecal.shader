
// ShaderGraphのDecalシェーダーからFake Shadowに必要なもののみに削ぎ落す
// TODO : Normalとか削り落としたいが本筋と逸れるため中断...
Shader "UTJ/FakeShadowByDecal"
{
    Properties
    {
        //[NoScaleOffset] Base_Map("Base Map", 2D) = "white" {} // _DecalTextureで飛んでくる
        _Base_Color("Base Color", Color) = (0, 0, 0, 0)
        //[HideInInspector]_DrawOrder("Draw Order", Range(-50, 50)) = 0
        [Toggle(DECAL_ANGLE_FADE)]_DecalAngleFadeSupported("Decal Angle Fade Supported", Float) = 1
        //[HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        //[HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        //[HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            // RenderType: <None>
            "PreviewType" = "Plane"
            // Queue: <None>
            "ShaderGraphShader" = "true"
            "ShaderGraphTargetId" = ""
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
            //#pragma multi_compile_fog
            #pragma editor_sync_compilation


            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"

            // Defines
            #define ATTRIBUTES_NEED_NORMAL
            #define ATTRIBUTES_NEED_TEXCOORD0
            #define VARYINGS_NEED_NORMAL_WS
            #define VARYINGS_NEED_VIEWDIRECTION_WS
            #define VARYINGS_NEED_TEXCOORD0
            #define VARYINGS_NEED_FOG_AND_VERTEX_LIGHT
            #define VARYINGS_NEED_SH
            #define VARYINGS_NEED_STATIC_LIGHTMAP_UV
            #define VARYINGS_NEED_DYNAMIC_LIGHTMAP_UV

            #define HAVE_MESH_MODIFICATION

            #define SHADERPASS SHADERPASS_DECAL_SCREEN_SPACE_PROJECTOR
            #define _MATERIAL_AFFECTS_ALBEDO 1

            // create shader variant
            #pragma shader_feature_local_fragment DECAL_ANGLE_FADE

            // HybridV1InjectedBuiltinProperties: <None>

            //// -- Properties used by ScenePickingPass
            //#ifdef SCENEPICKINGPASS
            //float4 _SelectionID;
            //#endif

            // Includes
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureStack.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderGraphFunctions.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderPass.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DecalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderVariablesDecal.hlsl"

            // --------------------------------------------------
            // Structs and Packing

            struct Attributes
            {
                 float3 positionOS : POSITION;
                 float3 normalOS : NORMAL;
                 float4 uv0 : TEXCOORD0;
                #if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : INSTANCEID_SEMANTIC;
                #endif
            };
            struct Varyings
            {
                 float4 positionCS : SV_POSITION;
                 float3 normalWS;
                 float4 texCoord0;
                 float3 viewDirectionWS;
                #if !defined(LIGHTMAP_ON)
                 float3 sh;
                #endif
                 float4 fogFactorAndVertexLight;
                #if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : CUSTOM_INSTANCE_ID;
                #endif
                #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
                 FRONT_FACE_TYPE cullFace : FRONT_FACE_SEMANTIC;
                #endif
            };
            struct SurfaceDescriptionInputs
            {
                 float4 uv0;
            };
            struct VertexDescriptionInputs
            {
            };
            struct PackedVaryings
            {
                 float4 positionCS : SV_POSITION;
                 float3 interp0 : INTERP0;
                 float4 interp1 : INTERP1;
                 float3 interp2 : INTERP2;
                 float2 interp3 : INTERP3;
                 float2 interp4 : INTERP4;
                 float3 interp5 : INTERP5;
                 float4 interp6 : INTERP6;
                #if UNITY_ANY_INSTANCING_ENABLED
                 uint instanceID : CUSTOM_INSTANCE_ID;
                #endif
                #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
                 FRONT_FACE_TYPE cullFace : FRONT_FACE_SEMANTIC;
                #endif
            };

            PackedVaryings PackVaryings(Varyings input)
            {
                PackedVaryings output;
                ZERO_INITIALIZE(PackedVaryings, output);
                output.positionCS = input.positionCS;
                output.interp0.xyz = input.normalWS;
                output.interp1.xyzw = input.texCoord0;
                output.interp2.xyz = input.viewDirectionWS;
                output.interp5.xyz = input.sh;
                output.interp6.xyzw = input.fogFactorAndVertexLight;
                #if UNITY_ANY_INSTANCING_ENABLED
                output.instanceID = input.instanceID;
                #endif
                #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
                output.cullFace = input.cullFace;
                #endif
                return output;
            }

            Varyings UnpackVaryings(PackedVaryings input)
            {
                Varyings output;
                output.positionCS = input.positionCS;
                output.normalWS = input.interp0.xyz;
                output.texCoord0 = input.interp1.xyzw;
                output.viewDirectionWS = input.interp2.xyz;
                output.sh = input.interp5.xyz;
                output.fogFactorAndVertexLight = input.interp6.xyzw;
                #if UNITY_ANY_INSTANCING_ENABLED
                output.instanceID = input.instanceID;
                #endif
                #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
                output.cullFace = input.cullFace;
                #endif
                return output;
            }


            // --------------------------------------------------
            // Graph

            // Graph Properties
            CBUFFER_START(UnityPerMaterial)
            float4 _DecalTexture_TexelSize;
            float4 _Base_Color;
            //float _DrawOrder;
            //float _DecalMeshBiasType;
            //float _DecalMeshDepthBias;
            //float _DecalMeshViewBias;
            CBUFFER_END
            float _DecalMeshDepthBias; // 使ってないが定義で必要...Constant Bufferにいらないので出す

            // Object and Global properties
            SAMPLER(SamplerState_Linear_Repeat);
            TEXTURE2D(_DecalTexture);
            SAMPLER(sampler_DecalTexture);

            // Graph Functions

            void Unity_Multiply_float4_float4(float4 A, float4 B, out float4 Out)
            {
                Out = A * B;
            }

            // Graph Vertex
            struct VertexDescription
            {
            };

            VertexDescription VertexDescriptionFunction(VertexDescriptionInputs IN)
            {
                VertexDescription description = (VertexDescription)0;
                return description;
            }

            // Graph Pixel
            struct SurfaceDescription
            {
                float3 BaseColor;
                float Alpha;
            };

            SurfaceDescription SurfaceDescriptionFunction(SurfaceDescriptionInputs IN)
            {
                SurfaceDescription surface = (SurfaceDescription)0;
                UnityTexture2D _Property_9f1059a7a93a46ccab349515214f3ed2_Out_0 = UnityBuildTexture2DStructNoScale(_DecalTexture);
                float4 _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0 = SAMPLE_TEXTURE2D(_Property_9f1059a7a93a46ccab349515214f3ed2_Out_0.tex, _Property_9f1059a7a93a46ccab349515214f3ed2_Out_0.samplerstate, _Property_9f1059a7a93a46ccab349515214f3ed2_Out_0.GetTransformedUV(IN.uv0.xy));
                float _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_R_4 = _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0.r;
                float _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_G_5 = _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0.g;
                float _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_B_6 = _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0.b;
                float _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_A_7 = _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0.a;
                float4 _Property_b5ca5e985fac473f8a1fac133002e353_Out_0 = _Base_Color;
                float4 _Multiply_dbcba1ca2def4675be44c68d0bdb7a63_Out_2;
                Unity_Multiply_float4_float4(_SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_RGBA_0, _Property_b5ca5e985fac473f8a1fac133002e353_Out_0, _Multiply_dbcba1ca2def4675be44c68d0bdb7a63_Out_2);
                surface.BaseColor = (_Multiply_dbcba1ca2def4675be44c68d0bdb7a63_Out_2.xyz);
                surface.Alpha = _SampleTexture2D_7388a7ddbf6648ec92c3bb54ed055048_A_7;
                return surface;
            }

            // --------------------------------------------------
            // Build Graph Inputs


            //     $features.graphVertex:  $include("VertexAnimation.template.hlsl")
            //                                       ^ ERROR: $include cannot find file : VertexAnimation.template.hlsl. Looked into:
            // Packages/com.unity.shadergraph/Editor/Generation/Templates


            //     $features.graphPixel:   $include("SharedCode.template.hlsl")
            //                                       ^ ERROR: $include cannot find file : SharedCode.template.hlsl. Looked into:
            // Packages/com.unity.shadergraph/Editor/Generation/Templates

            SurfaceDescriptionInputs BuildSurfaceDescriptionInputs(Varyings input)
            {
                SurfaceDescriptionInputs output;
                ZERO_INITIALIZE(SurfaceDescriptionInputs, output);

                /* WARNING: $splice Could not find named fragment 'CustomInterpolatorCopyToSDI' */

                output.uv0 = input.texCoord0;
                #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
                #define BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN                output.FaceSign =                                   IS_FRONT_VFACE(input.cullFace, true, false);
                #else
                #define BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN
                #endif
                #undef BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN

                return output;
            }

            // --------------------------------------------------
            // Build Surface Data

            void GetSurfaceData(Varyings input, uint2 positionSS, float angleFadeFactor, out DecalSurfaceData surfaceData)
            {
                half4x4 normalToWorld = UNITY_ACCESS_INSTANCED_PROP(Decal, _NormalToWorld);
                half fadeFactor = clamp(normalToWorld[0][3], 0.0f, 1.0f) * angleFadeFactor;
                float2 scale = float2(normalToWorld[3][0], normalToWorld[3][1]);
                float2 offset = float2(normalToWorld[3][2], normalToWorld[3][3]);
                input.texCoord0.xy = input.texCoord0.xy * scale + offset;
                half3 normalWS = TransformObjectToWorldDir(half3(0, 1, 0));
                half3 tangentWS = TransformObjectToWorldDir(half3(1, 0, 0));
                half3 bitangentWS = TransformObjectToWorldDir(half3(0, 0, 1));
                half sign = dot(cross(normalWS, tangentWS), bitangentWS) > 0 ? 1 : -1;
                input.normalWS.xyz = normalWS;

                SurfaceDescriptionInputs surfaceDescriptionInputs = BuildSurfaceDescriptionInputs(input);
                SurfaceDescription surfaceDescription = SurfaceDescriptionFunction(surfaceDescriptionInputs);

                // setup defaults -- these are used if the graph doesn't output a value
                ZERO_INITIALIZE(DecalSurfaceData, surfaceData);
                surfaceData.occlusion = half(1.0);
                surfaceData.smoothness = half(0);

                #ifdef _MATERIAL_AFFECTS_NORMAL
                    surfaceData.normalWS.w = half(1.0);
                #else
                    surfaceData.normalWS.w = half(0.0);
                #endif

                // copy across graph values, if defined
                surfaceData.baseColor.xyz = half3(surfaceDescription.BaseColor);
                surfaceData.baseColor.w = half(surfaceDescription.Alpha * fadeFactor);

                #if defined(_MATERIAL_AFFECTS_NORMAL)
                #else
                    surfaceData.normalWS.xyz = normalToWorld[2].xyz;
                #endif


                // In case of Smoothness / AO / Metal, all the three are always computed but color mask can change
            }

            // --------------------------------------------------
            // Main

            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/Varyings.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderPassDecal.hlsl"

            ENDHLSL
        }
    }
    //FallBack "Hidden/Shader Graph/FallbackError"
}