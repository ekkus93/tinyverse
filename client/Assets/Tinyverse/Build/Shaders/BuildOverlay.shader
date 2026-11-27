// Unlit, alpha-blended shader for build gizmos and the placement preview.
// The gizmo material sets _ZTest to Always (8) and the Overlay queue (4000), so
// gizmos draw over every opaque and transparent object in URP's transparent
// pass; the preview material keeps LessEqual (4) so it sits in the scene.
// A fixed fake light keeps 3D shapes readable without lighting cost.
Shader "Tinyverse/Build/Overlay"
{
    Properties
    {
        _Color("Colour", Color) = (1, 1, 1, 1)
        _Shade("Shade", Range(0, 1)) = 0.35
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 8
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "BuildOverlay"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Shade;
                float _ZTest;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float3 n = normalize(input.normalWS);
                n = isFrontFace ? n : -n;
                float3 l = normalize(float3(0.35, 0.85, 0.4));
                half lambert = saturate(dot(n, l) * 0.5 + 0.5);
                half k = lerp(1.0h, lambert, _Shade);
                return half4(_Color.rgb * k, _Color.a);
            }
            ENDHLSL
        }
    }
}
