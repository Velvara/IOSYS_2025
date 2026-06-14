Shader "Custom/Unlit_Noz"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        [Sprite] _MainTex("Sprite Texture", 2D) = "white"
        _MinSize("Min Size (px)", Float) = 10
        _MaxSize("Max Size (px)", Float) = 100
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Lighting Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Define PI since UNITY_PI is not available in URP
            #define PI 3.14159265359

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _MainTex_ST;
                float _MinSize;
                float _MaxSize;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                // Sprite center in world space (local origin)
                float3 worldCenter = TransformObjectToWorld(float3(0,0,0));

                // Camera vectors
                float3 camPos = _WorldSpaceCameraPos;
                float3 forward = normalize(worldCenter - camPos);
                float3 up = float3(0,1,0);
                float3 right = normalize(cross(up, forward));
                up = cross(forward, right);

                // Billboard local offset
                float3 localOffset = right * IN.positionOS.x + up * IN.positionOS.y;

                // Distance to camera
                float camDist = length(worldCenter - camPos);

                // Convert desired pixel size into world size
                float screenHeight = _ScreenParams.y;
                float fovY = PI * 0.5; // 90° FOV
                float worldMin = _MinSize / screenHeight * 2.0 * camDist * tan(fovY * 0.5);
                float worldMax = _MaxSize / screenHeight * 2.0 * camDist * tan(fovY * 0.5);

                // Compute current scale in world units (assuming sprite quad ~1 unit)
                float currentScale = length(float2(IN.positionOS.x, IN.positionOS.y));
                float scale = clamp(currentScale, worldMin, worldMax) / max(currentScale, 0.0001);

                // Apply uniform scale to offset
                localOffset *= scale;

                // Final world position
                float3 worldPos = worldCenter + localOffset;

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _BaseColor;
                return col;
            }
            ENDHLSL
        }
    }
}
