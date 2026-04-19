Shader "Custom/PressureField"
{
    Properties
    {
        _SmoothingRadius ("Smoothing Radius", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-1" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Positions;
            StructuredBuffer<float4> _PressureColors;
            float _SmoothingRadius;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float3 worldPos = float3(v.vertex.xy * _SmoothingRadius * 2.0, 0) + _Positions[instanceID].xyz;
                o.pos = UnityWorldToClipPos(worldPos);
                o.uv = v.uv;
                o.color = _PressureColors[instanceID];
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float r = length(i.uv - 0.5) * 2.0;
                float t = max(0.0, 1.0 - r * r);
                float kernel = t * t * t;
                return half4(i.color.rgb, i.color.a * kernel);
            }
            ENDCG
        }
    }
}
