Shader "Custom/CircleParticle"
{
    Properties
    {
        _ParticleRadius ("Particle Radius", Float) = 0.2
        _SmoothingRadius ("Smoothing Radius", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Blend One One  // Additive: overlapping particles accumulate, dense regions get brighter
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
            StructuredBuffer<float4> _Colors;
            float _ParticleRadius;
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
                // Scale quad by smoothing diameter so the kernel covers the full influence area
                float3 worldPos = float3(v.vertex.xy * _SmoothingRadius * 2.0, 0) + _Positions[instanceID].xyz;
                o.pos = UnityWorldToClipPos(worldPos);
                o.uv = v.uv;
                o.color = _Colors[instanceID];
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // r: normalized distance from particle center (0 = center, 1 = smoothing radius edge)
                float r = length(i.uv - 0.5) * 2.0;

                // Poly6 kernel: W(r) = (1 - r²)³  — standard SPH density kernel
                float t = max(0.0, 1.0 - r * r);
                float kernel = t * t * t;

                return half4(i.color.rgb * kernel, 1.0);
            }
            ENDCG
        }
    }
}
