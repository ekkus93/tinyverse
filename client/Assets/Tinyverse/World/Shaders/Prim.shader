// Lit shader for prims. Per-object colour and highlight are GPU-instanced
// properties set through a MaterialPropertyBlock, so thousands of prims that
// share a mesh and a preset material draw in a few instanced batches.
// Renderers with a MaterialPropertyBlock are excluded from the SRP Batcher by
// design; the material parameters still live in UnityPerMaterial.
Shader "Tinyverse/World/Prim"
{
    Properties
    {
        _PrimColor("Colour", Color) = (1, 1, 1, 1)
        [HideInInspector] _Highlight("Highlight", Float) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0.2
        _Metallic("Metallic", Range(0, 1)) = 0
        _Opacity("Opacity (transparent only)", Range(0, 1)) = 1
        _EmissionStrength("Emission strength", Float) = 0
        _HighlightColor("Highlight colour", Color) = (1, 0.72, 0.05, 1)
        _HighlightFill("Highlight fill strength", Range(0, 0.2)) = 0.03

        [HideInInspector] _Surface("__surface", Float) = 0
        [HideInInspector] _SrcBlend("__src", Float) = 1
        [HideInInspector] _DstBlend("__dst", Float) = 0
        [HideInInspector] _ZWrite("__zw", Float) = 1
        [HideInInspector] _Cull("__cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half _Smoothness;
            half _Metallic;
            half _Opacity;
            half _EmissionStrength;
            half4 _HighlightColor;
            half _HighlightFill;
            half _Surface;
        CBUFFER_END

        UNITY_INSTANCING_BUFFER_START(PrimProps)
            UNITY_DEFINE_INSTANCED_PROP(float4, _PrimColor)
            UNITY_DEFINE_INSTANCED_PROP(float, _Highlight)
        UNITY_INSTANCING_BUFFER_END(PrimProps)
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex PrimVertex
            #pragma fragment PrimFragment

            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _EMISSION

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "TinyverseFog.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half3 vertexSH : TEXCOORD3;
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                half3 vertexLight : TEXCOORD4;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings PrimVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalWS;
                output.vertexSH = SampleSH(normalWS);
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                output.vertexLight = VertexLighting(vertexInput.positionWS, normalWS);
            #endif
                return output;
            }

            half4 PrimFragment(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(PrimProps, _PrimColor);
                float highlight = UNITY_ACCESS_INSTANCED_PROP(PrimProps, _Highlight);

                float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                // Planes are drawn double-sided: light the back face with the flipped normal.
                normalWS = isFrontFace ? normalWS : -normalWS;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif
                inputData.fogCoord = 0;
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                inputData.vertexLighting = input.vertexLight;
            #endif
                inputData.bakedGI = input.vertexSH;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseColor.rgb;
                surfaceData.metallic = _Metallic;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1;
            #if defined(_SURFACE_TYPE_TRANSPARENT)
                surfaceData.alpha = saturate(baseColor.a * _Opacity);
            #else
                surfaceData.alpha = 1;
            #endif

                half3 emission = half3(0, 0, 0);
            #if defined(_EMISSION)
                emission = baseColor.rgb * _EmissionStrength;
            #endif
                // Selection is marked by the outline renderer (PrimOutline.shader); the body only
                // gets a very faint pulse so its true colour stays readable while recolouring.
                if (highlight > 0)
                {
                    half pulse = 0.5 + 0.5 * sin(_TimeParameters.x * 4.0);
                    emission += _HighlightColor.rgb * highlight * _HighlightFill * pulse;
                }
                surfaceData.emission = emission;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
            #if defined(_ALPHAPREMULTIPLY_ON)
                color.rgb = TinyverseApplyFogPremultiplied(color.rgb, color.a, input.positionWS);
            #else
                color.rgb = TinyverseApplyFog(color.rgb, input.positionWS);
            #endif
                color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
