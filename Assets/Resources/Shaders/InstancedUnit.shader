Shader "WAR2D/InstancedUnit"
{
    Properties { _MainTex ("Atlas", 2D) = "white" {} _UnitSize ("Unit size", Float) = 0.8 _Aspect ("Height / width", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct UnitInstance { float2 position; float rotation; uint frame; uint color; float health; float scale; float pad; };
            StructuredBuffer<UnitInstance> _Instances;
            StructuredBuffer<float4> _FrameRects; // xy = uv min, zw = uv size
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _UnitSize;
            float _Aspect;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; half highlight : TEXCOORD1; };

            static const float2 Corners[6] =
            {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(0.5, 0.5),
                float2(-0.5, -0.5), float2(0.5, 0.5), float2(-0.5, 0.5)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                UnitInstance u = _Instances[iid];
                float2 c = Corners[vid];
                float2 shaped = float2(c.x, c.y * _Aspect);
                float s, co;
                sincos(u.rotation, s, co);
                float2 local = float2(shaped.x * co - shaped.y * s, shaped.x * s + shaped.y * co) * _UnitSize * u.scale;
                v2f o;
                o.pos = TransformWorldToHClip(float3(u.position + local, 0));
                float4 r = _FrameRects[u.frame];
                o.uv = r.xy + (c + 0.5) * r.zw;
                o.color = half4((u.color & 255) / 255.0, ((u.color >> 8) & 255) / 255.0,
                                ((u.color >> 16) & 255) / 255.0, ((u.color >> 24) & 255) / 255.0);
                o.highlight = u.pad > 0.5 ? 1 : 0;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
                c.rgb = lerp(c.rgb, half3(1, 1, 1), i.highlight * 0.45h); // selected units read brighter
                return c;
            }
            ENDHLSL
        }
    }
}
