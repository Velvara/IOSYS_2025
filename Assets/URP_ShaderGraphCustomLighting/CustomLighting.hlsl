#ifndef CUSTOM_LIGHTING_INCLUDED
#define CUSTOM_LIGHTING_INCLUDED

// @Cyanilux | https://github.com/Cyanilux/URP_ShaderGraphCustomLighting
// Modified: Added smooth band control for Additional Lights Toon, fixed wrappers, corrected eclipse-ring bug.

//------------------------------------------------------------------------------------------------------
// Keyword Pragmas
//------------------------------------------------------------------------------------------------------

#ifndef SHADERGRAPH_PREVIEW
#if SHADERPASS != SHADERPASS_FORWARD && SHADERPASS != SHADERPASS_GBUFFER
    	#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
    	#pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
		#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
		#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
		#pragma multi_compile _ _CLUSTER_LIGHT_LOOP
#endif
#endif

//------------------------------------------------------------------------------------------------------
// Main Light
//------------------------------------------------------------------------------------------------------

void MainLight_float(out float3 Direction, out float3 Color, out float DistanceAtten)
{
#ifdef SHADERGRAPH_PREVIEW
		Direction = normalize(float3(1,1,-0.4));
		Color = float4(1,1,1,1);
		DistanceAtten = 1;
#else
    Light mainLight = GetMainLight();
    Direction = mainLight.direction;
    Color = mainLight.color;
    DistanceAtten = mainLight.distanceAttenuation;
#endif
}

void MainLightLayer_float(float3 Shading, out float3 Out)
{
#ifdef SHADERGRAPH_PREVIEW
		Out = Shading;
#else
    Out = 0;
    uint meshRenderingLayers = GetMeshRenderingLayer();
		#ifdef _LIGHT_LAYERS
			if (IsMatchingLightLayer(GetMainLight().layerMask, meshRenderingLayers))
		#endif
		{
        Out = Shading;
    }
#endif
}

void MainLightCookie_float(float3 WorldPos, out float3 Cookie)
{
    Cookie = 1;
#if defined(_LIGHT_COOKIES)
        Cookie = SampleMainLightCookie(WorldPos);
#endif
}

//------------------------------------------------------------------------------------------------------
// Main Light Shadows
//------------------------------------------------------------------------------------------------------

void MainLightShadows_float(float3 WorldPos, half4 Shadowmask, out float ShadowAtten)
{
#ifdef SHADERGRAPH_PREVIEW
		ShadowAtten = 1;
#else
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
		float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(WorldPos));
#else
    float4 shadowCoord = TransformWorldToShadowCoord(WorldPos);
#endif
    ShadowAtten = MainLightShadow(shadowCoord, WorldPos, Shadowmask, _MainLightOcclusionProbes);
#endif
}

void MainLightShadows_float(float3 WorldPos, out float ShadowAtten)
{
    MainLightShadows_float(WorldPos, half4(1, 1, 1, 1), ShadowAtten);
}

//------------------------------------------------------------------------------------------------------
// Baked GI
//------------------------------------------------------------------------------------------------------

void Shadowmask_half(float2 lightmapUV, out half4 Shadowmask)
{
#ifdef SHADERGRAPH_PREVIEW
		Shadowmask = half4(1,1,1,1);
#else
    OUTPUT_LIGHTMAP_UV(lightmapUV, unity_LightmapST, lightmapUV);
    Shadowmask = SAMPLE_SHADOWMASK(lightmapUV);
#endif
}

void SubtractiveGI_float(float ShadowAtten, float3 NormalWS, float3 BakedGI, out half3 result)
{
#ifdef SHADERGRAPH_PREVIEW
		result = half3(1,1,1);
#else
    Light mainLight = GetMainLight();
    mainLight.shadowAttenuation = ShadowAtten;
    MixRealtimeAndBakedGI(mainLight, NormalWS, BakedGI);
    result = BakedGI;
#endif
}

//------------------------------------------------------------------------------------------------------
// Default Additional Lights
//------------------------------------------------------------------------------------------------------

void AdditionalLights_float(float3 SpecColor, float Smoothness, float3 WorldPosition, float3 WorldNormal, float3 WorldView, half4 Shadowmask,
							out float3 Diffuse, out float3 Specular)
{
    float3 diffuseColor = 0;
    float3 specularColor = 0;
#ifndef SHADERGRAPH_PREVIEW
    Smoothness = exp2(10 * Smoothness + 1);
    uint pixelLightCount = GetAdditionalLightsCount();
    uint meshRenderingLayers = GetMeshRenderingLayer();

#if USE_CLUSTER_LIGHT_LOOP
	for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++) {
		CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
		Light light = GetAdditionalLight(lightIndex, WorldPosition, Shadowmask);
#ifdef _LIGHT_LAYERS
		if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
		{
			float3 attenuatedLightColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);
			diffuseColor += LightingLambert(attenuatedLightColor, light.direction, WorldNormal);
			specularColor += LightingSpecular(attenuatedLightColor, light.direction, WorldNormal, WorldView, float4(SpecColor, 0), Smoothness);
		}
	}
#endif

    InputData inputData = (InputData) 0;
    float4 screenPos = ComputeScreenPos(TransformWorldToHClip(WorldPosition));
    inputData.normalizedScreenSpaceUV = screenPos.xy / screenPos.w;
    inputData.positionWS = WorldPosition;

    LIGHT_LOOP_BEGIN(pixelLightCount)

    Light light = GetAdditionalLight(lightIndex, WorldPosition, Shadowmask);
	#ifdef _LIGHT_LAYERS
		if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
	#endif
		{
        float3 attenuatedLightColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);
        diffuseColor += LightingLambert(attenuatedLightColor, light.direction, WorldNormal);
        specularColor += LightingSpecular(attenuatedLightColor, light.direction, WorldNormal, WorldView, float4(SpecColor, 0), Smoothness);
    }
    LIGHT_LOOP_END
#endif

	Diffuse = diffuseColor;
    Specular = specularColor;
}

//------------------------------------------------------------------------------------------------------
// Additional Lights Toon (Smooth Bands)
//------------------------------------------------------------------------------------------------------

#ifndef SHADERGRAPH_PREVIEW
float ToonAttenuationSmooth(int lightIndex, float3 positionWS, float pointBands, float spotBands, float smoothness)
{
#if !USE_CLUSTER_LIGHT_LOOP
    lightIndex = GetPerObjectLightIndex(lightIndex);
#endif
#if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
		float4 lightPositionWS = _AdditionalLightsBuffer[lightIndex].position;
		half4 spotDirection = _AdditionalLightsBuffer[lightIndex].spotDirection;
		half4 distanceAndSpotAttenuation = _AdditionalLightsBuffer[lightIndex].attenuation;
#else
    float4 lightPositionWS = _AdditionalLightsPosition[lightIndex];
    half4 spotDirection = _AdditionalLightsSpotDir[lightIndex];
    half4 distanceAndSpotAttenuation = _AdditionalLightsAttenuation[lightIndex];
#endif

    float3 lightVector = lightPositionWS.xyz - positionWS * lightPositionWS.w;
    float distanceSqr = max(dot(lightVector, lightVector), HALF_MIN);
    float range = rsqrt(distanceAndSpotAttenuation.x);
    float dist = sqrt(distanceSqr) / range;

    half3 lightDirection = half3(lightVector * rsqrt(distanceSqr));
    half SdotL = dot(spotDirection.xyz, lightDirection);
    half spotAtten = saturate(SdotL * distanceAndSpotAttenuation.z + distanceAndSpotAttenuation.w);
    spotAtten *= spotAtten;
    float maskSpotToRange = step(dist, 1);

    bool isSpot = (distanceAndSpotAttenuation.z > 0);
    if (isSpot)
    {
        float bands = max(1.0, spotBands);
        float stepped = floor(spotAtten * bands) / bands;
        float next = (stepped + 1.0 / bands);
        float edge = smoothstep(next - (smoothness / bands), next, spotAtten);
        return (stepped + edge / bands) * maskSpotToRange;
    }
    else
    {
        float bands = max(1.0, pointBands);
        float stepped = floor(dist * bands) / bands;
        float next = (stepped + 1.0 / bands);
        float edge = smoothstep(next - (smoothness / bands), next, dist);
        return 1.0 - (stepped + edge / bands);
    }
}
#endif

void AdditionalLightsToon_float(float3 SpecColor, float Smoothness, float3 WorldPosition, float3 WorldNormal, float3 WorldView, half4 Shadowmask,
						float PointLightBands, float SpotLightBands, float BandSmoothness,
						out float3 Diffuse, out float3 Specular)
{
    float3 diffuseColor = 0;
    float3 specularColor = 0;

#ifndef SHADERGRAPH_PREVIEW
    Smoothness = exp2(10 * Smoothness + 1);
    uint pixelLightCount = GetAdditionalLightsCount();
    uint meshRenderingLayers = GetMeshRenderingLayer();

#if USE_CLUSTER_LIGHT_LOOP
	for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++) {
		CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
		Light light = GetAdditionalLight(lightIndex, WorldPosition, Shadowmask);
#ifdef _LIGHT_LAYERS
		if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
		{
			if (PointLightBands <= 1 && SpotLightBands <= 1){
				diffuseColor += light.color * step(0.0001, light.distanceAttenuation * light.shadowAttenuation);
			}else{
				diffuseColor += light.color * light.shadowAttenuation * ToonAttenuationSmooth(lightIndex, WorldPosition, PointLightBands, SpotLightBands, BandSmoothness);
			}
		}
	}
#endif

    InputData inputData = (InputData) 0;
    float4 screenPos = ComputeScreenPos(TransformWorldToHClip(WorldPosition));
    inputData.normalizedScreenSpaceUV = screenPos.xy / screenPos.w;
    inputData.positionWS = WorldPosition;

    LIGHT_LOOP_BEGIN(pixelLightCount)

    Light light = GetAdditionalLight(lightIndex, WorldPosition, Shadowmask);
	#ifdef _LIGHT_LAYERS
		if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
	#endif
		{
        if (PointLightBands <= 1 && SpotLightBands <= 1)
        {
            diffuseColor += light.color * step(0.0001, light.distanceAttenuation * light.shadowAttenuation);
        }
        else
        {
            diffuseColor += light.color * light.shadowAttenuation * ToonAttenuationSmooth(lightIndex, WorldPosition, PointLightBands, SpotLightBands, BandSmoothness);
        }
    }
    LIGHT_LOOP_END
#endif

	Diffuse = diffuseColor;
    Specular = specularColor;
}

// Wrappers
void AdditionalLightsToon_float(float3 SpecColor, float Smoothness, float3 WorldPosition, float3 WorldNormal, float3 WorldView, half4 Shadowmask,
						float PointLightBands, float SpotLightBands,
						out float3 Diffuse, out float3 Specular)
{
    AdditionalLightsToon_float(SpecColor, Smoothness, WorldPosition, WorldNormal, WorldView, Shadowmask, PointLightBands, SpotLightBands, 0.1, Diffuse, Specular);
}

void AdditionalLightsToon_float(float3 SpecColor, float Smoothness, float3 WorldPosition, float3 WorldNormal, float3 WorldView,
						float PointLightBands, float SpotLightBands,
						out float3 Diffuse, out float3 Specular)
{
    AdditionalLightsToon_float(SpecColor, Smoothness, WorldPosition, WorldNormal, WorldView, half4(1, 1, 1, 1), PointLightBands, SpotLightBands, 0.1, Diffuse, Specular);
}

//------------------------------------------------------------------------------------------------------
// Deprecated / Backwards compatibility (non-toon AdditionalLights wrappers)
//------------------------------------------------------------------------------------------------------

void AdditionalLights_float(float3 SpecColor, float Smoothness, float3 WorldPosition, float3 WorldNormal, float3 WorldView,
							out float3 Diffuse, out float3 Specular)
{
    AdditionalLights_float(SpecColor, Smoothness, WorldPosition, WorldNormal, WorldView, half4(1, 1, 1, 1), Diffuse, Specular);
}

//------------------------------------------------------------------------------------------------------
// Fog / Ambient
//------------------------------------------------------------------------------------------------------

void MixFog_float(float3 Colour, float Fog, out float3 Out)
{
#ifdef SHADERGRAPH_PREVIEW
		Out = Colour;
#else
    Out = MixFog(Colour, Fog);
#endif
}

void AmbientSampleSH_float(float3 WorldNormal, out float3 Ambient)
{
#ifdef SHADERGRAPH_PREVIEW
		Ambient = float3(0.1, 0.1, 0.1);
#else
    Ambient = SampleSH(WorldNormal);
#endif
}

#endif // CUSTOM_LIGHTING_INCLUDED

