Shader "WAR2D/InstancedBuilding"
{
    // Buildings as one instanced draw: each instance places its atlas frame's pivot at its position, at the
    // frame's own size (building frames differ in size and pivot, unlike the one-tile unit frames). Team pixels
    // take the owner's colour; pad bit 1 draws a ghost (last seen: grey, dim, 60 % opaque), bit 2 a hit flash.
    // The colour's alpha fades a blueprint.
    Properties { _MainTex ("Atlas", 2D) = "white" {} _MaskTex ("Team mask", 2D) = "black" {} _TeamGain ("Team grey to owner colour", Float) = 1.55 _PixelsPerUnit ("Pixels per unit (0 = no snapping)", Float) = 64 }
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
            StructuredBuffer<float4> _FrameGeom;  // xy = size in world units, zw = pivot from the bottom-left in world units
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float _TeamGain;
            float _PixelsPerUnit;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; half2 flags : TEXCOORD1; };

            static const float2 Corners[6] =
            {
                float2(0, 0), float2(1, 0), float2(1, 1),
                float2(0, 0), float2(1, 1), float2(0, 1)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                UnitInstance u = _Instances[iid];
                float2 c = Corners[vid];
                float4 g = _FrameGeom[u.frame];
                float2 origin = _PixelsPerUnit > 0 ? round(u.position * _PixelsPerUnit) / _PixelsPerUnit : u.position;
                v2f o;
                o.pos = TransformWorldToHClip(float3(origin + (c * g.xy - g.zw) * u.scale, 0));
                float4 r = _FrameRects[u.frame];
                o.uv = r.xy + c * r.zw;
                o.color = half4((u.color & 255) / 255.0, ((u.color >> 8) & 255) / 255.0,
                                ((u.color >> 16) & 255) / 255.0, ((u.color >> 24) & 255) / 255.0);
                uint pad = (uint)(u.pad + 0.5);
                o.flags = half2((pad & 1u) != 0 ? 1 : 0, (pad & 2u) != 0 ? 1 : 0);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv).r;
                c.rgb = lerp(c.rgb, saturate(c.rgb * i.color.rgb * _TeamGain), mask);
                half grey = dot(c.rgb, half3(0.299, 0.587, 0.114));
                c.rgb = lerp(c.rgb, grey.xxx * 0.6, i.flags.x);
                c.a *= lerp(1, 0.6, i.flags.x);
                c.rgb = lerp(c.rgb, half3(1, 0.25, 0.25), i.flags.y * 0.6);
                c.a *= i.color.a;
                return c;
            }
            ENDHLSL
        }
    }
}
