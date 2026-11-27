// The ground: a matte lit surface with a procedural world-space grid (thin lines every
// metre, stronger ones every 8 m, and a faint 8 m checker). Lines are anti-aliased with
// screen-space derivatives and fade out with distance, so there is no shimmer or moire and
// no texture to tile. Fog is keyword-free (see TinyverseFog.hlsl).
Shader "Tinyverse/World/Ground"
{
    Properties
    {
        _BaseColor("Colour", Color) = (0.463, 0.502, 0.439, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.05
        _MetreLine("Metre line darkening", Range(0, 1)) = 0.12
        _CellLine("8 m line darkening", Range(0, 1)) = 0.22
        _Checker("Checker darkening", Range(0, 0.5)) = 0.05
        _CellSize("Cell size (m)", Float) = 8
        _LineWidth("Line width (pixels)", Float) = 1.1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
            half _MetreLine;
            half _CellLine;
            half _Checker;
            float _CellSize;
            float _LineWidth;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GroundVertex
            #pragma fragment GroundFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
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
                half3 vertexSH : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings GroundVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs v = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = v.positionCS;
                output.positionWS = v.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.vertexSH = SampleSH(output.normalWS);
                return output;
            }

            // 1 on a grid line of the given spacing, 0 between; anti-aliased, and faded to its
            // average coverage once the lines get closer than a few pixels.
            float GridLine(float2 coord, float spacing, float widthPx)
            {
                float2 c = coord / spacing;
                float2 fw = max(fwidth(c), 1e-5);
                float2 dist = abs(frac(c - 0.5) - 0.5) / fw;
                float2 l = 1 - saturate(dist - widthPx * 0.5 + 0.5);
                float lineMask = max(l.x, l.y);
                float fade = saturate(1.6 - max(fw.x, fw.y) * 4.0);
                float average = saturate(widthPx * max(fw.x, fw.y));
                return lerp(average, lineMask, fade);
            }

            half4 GroundFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 xz = input.positionWS.xz;
                float2 cell = floor(xz / _CellSize);
                half checker = fmod(abs(cell.x + cell.y), 2.0) < 0.5 ? 0 : _Checker;
                half metre = GridLine(xz, 1.0, _LineWidth) * _MetreLine;
                half cellLine = GridLine(xz, _CellSize, _LineWidth * 1.8) * _CellLine;
                half shade = (1 - checker) * (1 - max(metre, cellLine));

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #endif
                inputData.bakedGI = input.vertexSH;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = _BaseColor.rgb * shade;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1;
                surfaceData.alpha = 1;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = TinyverseApplyFog(color.rgb, input.positionWS);
                return half4(color.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
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
