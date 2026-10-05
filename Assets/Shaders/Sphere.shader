Shader "Custom/SphereParticle"
{
    // Camera-facing quad per particle, shaded as a sphere from a fixed light direction (like Lague's billboard particles)
    Properties
    {
        _ParticleRadius ("Particle Radius", Float) = 0.04
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        ZWrite On
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

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                float4 viewPos = mul(UNITY_MATRIX_V, float4(_Positions[instanceID].xyz, 1)) + float4(v.vertex.xy * _ParticleRadius * 2.0, 0, 0);
                o.pos = mul(UNITY_MATRIX_P, viewPos);
                o.uv = v.uv;
                o.color = _Colors[instanceID];
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 p = (i.uv - 0.5) * 2.0;
                float r2 = dot(p, p);
                clip(1.0 - r2);
                float3 normal = float3(p, sqrt(1.0 - r2));
                float light = saturate(dot(normal, normalize(float3(-0.4, 0.7, 0.6))));
                return half4(i.color.rgb * (0.35 + 0.65 * light), 1);
            }
            ENDCG
        }
    }
}
