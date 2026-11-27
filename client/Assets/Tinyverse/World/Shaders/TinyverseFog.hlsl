#ifndef TINYVERSE_FOG_INCLUDED
#define TINYVERSE_FOG_INCLUDED

// Exponential-squared distance fog that does not depend on URP's FOG_* keywords.
// The project strips fog variants automatically ("Fog modes: Automatic"), and the one
// scene has fog off because WorldEnvironment turns fog on from code, so keyword-based fog
// is compiled out of a player. This reads the same globals Unity sets from RenderSettings:
// unity_FogParams.x = density / sqrt(ln 2), unity_FogColor (already in shading space).
// WorldEnvironment always uses FogMode.ExponentialSquared.
float TinyverseFogKeep(float3 positionWS)
{
    float f = unity_FogParams.x * distance(positionWS, GetCameraPositionWS());
    return saturate(exp2(-f * f));
}

half3 TinyverseApplyFog(half3 color, float3 positionWS)
{
    return lerp(unity_FogColor.rgb, color, TinyverseFogKeep(positionWS));
}

// For premultiplied-alpha output: the fog colour is weighted by coverage too.
half3 TinyverseApplyFogPremultiplied(half3 color, half alpha, float3 positionWS)
{
    return lerp(unity_FogColor.rgb * alpha, color, TinyverseFogKeep(positionWS));
}

#endif
