Shader "WAR2D/InstancedUnit"
{
    Properties { _MainTex ("Atlas", 2D) = "white" {} _MaskTex ("Team mask", 2D) = "white" {} _UnitSize ("Unit size", Float) = 1 _Aspect ("Height / width", Float) = 1 _TeamGain ("Team grey to owner colour", Float) = 1.55 _PixelsPerUnit ("Pixels per unit (0 = no snapping)", Float) = 64 }
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
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float _UnitSize;
            float _Aspect;
            float _TeamGain;
            float _PixelsPerUnit;

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
                // Quads don't rotate: the facing picks a rendered direction frame (style guide).
                float2 local = float2(c.x, c.y * _Aspect) * _UnitSize * u.scale;
                // Snap the origin to the art's pixel grid so frames don't swim between pixels.
                float2 origin = _PixelsPerUnit > 0 ? round(u.position * _PixelsPerUnit) / _PixelsPerUnit : u.position;
                v2f o;
                o.pos = TransformWorldToHClip(float3(origin + local, 0));
                float4 r = _FrameRects[u.frame];
                o.uv = r.xy + (c + 0.5) * r.zw;
                o.color = half4((u.color & 255) / 255.0, ((u.color >> 8) & 255) / 255.0,
                                ((u.color >> 16) & 255) / 255.0, ((u.color >> 24) & 255) / 255.0);
                o.highlight = u.pad > 0.5 ? 1 : 0;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv).r;
                // Team pixels are a grey ramp: multiplying keeps their shading in the owner's colour.
                c.rgb = lerp(c.rgb, saturate(c.rgb * i.color.rgb * _TeamGain), mask);
                c.a *= i.color.a;
                c.rgb = lerp(c.rgb, half3(1, 1, 1), i.highlight * 0.45h); // selected units read brighter
                return c;
            }
            ENDHLSL
        }
    }
}
