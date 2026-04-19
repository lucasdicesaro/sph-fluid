Shader "Custom/CircleParticle"
{
    Properties
    {
        _ParticleRadius ("Particle Radius", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
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
            StructuredBuffer<float4> _Colors;
            float _ParticleRadius;

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
                // Scale unit quad by diameter, then offset to particle world position
                float3 worldPos = float3(v.vertex.xy * _ParticleRadius * 2.0, 0) + _Positions[instanceID].xyz;
                o.pos = UnityWorldToClipPos(worldPos);
                o.uv = v.uv;
                o.color = _Colors[instanceID];
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // SDF circle: UV is 0..1, center at 0.5
                float dist = length(i.uv - 0.5);
                // Smooth edge for anti-aliasing
                float alpha = 1.0 - smoothstep(0.45, 0.5, dist);
                clip(alpha - 0.001);
                return half4(i.color.rgb, i.color.a * alpha);
            }
            ENDCG
        }
    }
}
