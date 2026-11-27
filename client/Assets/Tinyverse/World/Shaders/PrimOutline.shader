// Selection outline for prims, drawn by a separate child renderer that exists only while a
// prim is highlighted (unselected prims pay nothing). It uses three materials on the same
// mesh, ordered by render queue after transparents:
//   1. mask  (_Grow = 0, ColorMask 0): writes stencil where the prim itself is visible;
//   2. outer (_Grow = 1): a hull grown by a screen-relative width, drawn only outside the mask;
//   3. inner (_Grow ~ 0.4, dark): a thinner hull over the outer one, giving a dark line against
//      the silhouette and a bright band outside it, readable on any colour and background.
// The prim's own colour is never touched. Glass works because the mask keeps the hull from
// showing through the prim.
Shader "Tinyverse/World/PrimOutline"
{
    Properties
    {
        _OutlineColor("Colour", Color) = (1, 0.72, 0.05, 1)
        _PulseColor("Pulse colour", Color) = (1, 0.95, 0.6, 1)
        _PulseAmount("Pulse amount", Range(0, 1)) = 0.35
        _Grow("Width factor (0 = mask)", Float) = 1
        _WidthPerMetre("Width per metre of distance", Float) = 0.006
        _MinWidth("Minimum width (m)", Float) = 0.015
        _MaxWidth("Maximum width (m)", Float) = 0.6
        [HideInInspector] _ColorMask("__colormask", Float) = 15
        [HideInInspector] _StencilComp("__stencilcomp", Float) = 6
        [HideInInspector] _StencilPass("__stencilpass", Float) = 0
        [HideInInspector] _OffsetUnits("__offset", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+10"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Offset [_OffsetUnits], [_OffsetUnits]
            ColorMask [_ColorMask]
            Blend Off
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp [_StencilComp]
                Pass [_StencilPass]
            }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                half4 _PulseColor;
                half _PulseAmount;
                float _Grow;
                float _WidthPerMetre;
                float _MinWidth;
                float _MaxWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4x4 m = GetObjectToWorldMatrix();
                float3 scale = float3(length(m._m00_m10_m20), length(m._m01_m11_m21), length(m._m02_m12_m22));
                float3 centreWS = m._m03_m13_m23;
                float dist = distance(GetCameraPositionWS(), centreWS);
                float width = clamp(dist * _WidthPerMetre, _MinWidth, _MaxWidth) * _Grow;
                // Every prim mesh is centred in the unit cube, so scaling about the centre by a
                // fixed world margin per axis grows each face outwards by about `width` metres,
                // whatever the object's scale (a flat plane only grows sideways).
                float3 grown = input.positionOS.xyz * (1.0 + 2.0 * width / max(scale, 1e-4));
                output.positionCS = TransformObjectToHClip(grown);
                return output;
            }

            half4 OutlineFragment(Varyings input) : SV_Target
            {
                half pulse = 0.5 + 0.5 * sin(_TimeParameters.x * 4.0);
                return half4(lerp(_OutlineColor.rgb, _PulseColor.rgb, pulse * _PulseAmount), 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
