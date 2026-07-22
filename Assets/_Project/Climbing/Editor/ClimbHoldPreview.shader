// Editor-only shader for the ClimbableSurface hold preview: unlit vertex colour, backface-culled
// so only holds FACING the camera render (the "front face" of the surface), depth-tested so the
// geometry itself hides the far side. Lives in an Editor folder — never shipped.
Shader "Hidden/Climb/HoldPreview"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
