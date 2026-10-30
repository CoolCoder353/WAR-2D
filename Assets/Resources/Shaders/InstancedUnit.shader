Shader "WAR2D/InstancedUnit"
{
    Properties { _MainTex ("Atlas", 2D) = "white" {} _MaskTex ("Team mask", 2D) = "white" {} _UnitSize ("Unit size", Float) = 1 _Aspect ("Height / width", Float) = 1 _TeamGain ("Team grey to owner colour", Float) = 1.55 _PixelsPerUnit ("Pixels per unit (0 = no snapping)", Float) = 64 _Trail ("Health bars: draw the recent-damage trail", Float) = 0 }
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
            float _Trail;

            // highlight: x selected, y hit flash. bar: x the bar's local u, y its local v, z filled art pixels, w trail art pixels.
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; half2 highlight : TEXCOORD1; float4 bar : TEXCOORD2; };

            // Health bar art (InstancedUnitRenderer.BuildBarAtlas): BarWidth x BarHeight pixels, 1 px outline, BarFrames fill steps.
            static const float BarWidth = 48, BarHeight = 4, BarFrames = 16;

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
                // pad bit 1: selected; bit 2: hit flash (units). Bars and rings leave it 0.
                uint pad = (uint)(u.pad + 0.5);
                o.highlight = half2((pad & 1u) != 0 ? 1 : 0, (pad & 2u) != 0 ? 1 : 0);
                // A bar's frame is how full it is; its rotation carries the recent-damage level (quads never rotate).
                float inner = BarWidth - 2;
                o.bar = float4(c + 0.5, round(u.frame / (BarFrames - 1) * inner), round(saturate(u.rotation) * inner));
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv).r;
                // Team pixels are a grey ramp: multiplying keeps their shading in the owner's colour.
                c.rgb = lerp(c.rgb, saturate(c.rgb * i.color.rgb * _TeamGain), mask);
                c.a *= i.color.a;
                c.rgb = lerp(c.rgb, half3(1, 1, 1), i.highlight.x * 0.45h); // selected units read brighter
                c.rgb = lerp(c.rgb, half3(1, 0.25, 0.25), i.highlight.y * 0.6h); // a unit that was just hit flashes red, as buildings do
                if (_Trail > 0.5)
                {
                    // The part of a bar that was lost a moment ago stays pale while it drains (health bars only).
                    float px = floor(i.bar.x * BarWidth), py = floor(i.bar.y * BarHeight);
                    bool edge = px < 1 || py < 1 || px > BarWidth - 2 || py > BarHeight - 2;
                    float fill = px - 1;
                    if (!edge && fill >= i.bar.z && fill < i.bar.w) c.rgb = half3(1, 0.93, 0.8);
                }
                return c;
            }
            ENDHLSL
        }
    }
}
