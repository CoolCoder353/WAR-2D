Shader "WAR2D/TeamSprite"
{
    // A sprite from a generated atlas with its team mask: team pixels (a grey ramp) take the owner's colour,
    // the rest keeps the art. Ghosts (last-seen buildings) are desaturated and see-through. The vertex colour
    // still multiplies, so the placement preview can tint the whole sprite.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _MaskTex ("Team mask", 2D) = "black" {}
        _TeamColor ("Owner colour", Color) = (1, 1, 1, 1)
        _TeamGain ("Team grey to owner colour", Float) = 1.55
        _Ghost ("Ghost (0 or 1)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float4 _MainTex_TexelSize;

            // Crisp art when zoomed in (playtest 2026-10-30: units were blurry at high zoom). The atlases are trilinear for a smooth
            // zoom out, which smears texels when magnified: snap the uv to the texel centre, keeping a one-screen-pixel blend at
            // texel edges, and sample with the original gradients so the mip choice is unchanged. Zoomed out it changes nothing.
            float2 SharpUv(float2 uv, float4 texelSize)
            {
                if (texelSize.z <= 0) return uv; // the texture's size wasn't set: leave the uv alone
                float2 t = uv * texelSize.zw;
                float2 seam = floor(t + 0.5);
                float2 fw = clamp(fwidth(t), 1e-5, 1.0);
                return (seam + clamp((t - seam) / fw, -0.5, 0.5)) * texelSize.xy;
            }
            CBUFFER_START(UnityPerMaterial)
                half4 _TeamColor;
                float _TeamGain;
                float _Ghost;
            CBUFFER_END

            struct appdata { float3 vertex : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 dx = ddx(i.uv), dy = ddy(i.uv), uv = SharpUv(i.uv, _MainTex_TexelSize);
                half4 c = SAMPLE_TEXTURE2D_GRAD(_MainTex, sampler_MainTex, uv, dx, dy);
                half mask = SAMPLE_TEXTURE2D_GRAD(_MaskTex, sampler_MaskTex, uv, dx, dy).r;
                c.rgb = lerp(c.rgb, saturate(c.rgb * _TeamColor.rgb * _TeamGain), mask);
                // Ghost: grey and dim, 60 % opaque.
                half grey = dot(c.rgb, half3(0.299, 0.587, 0.114));
                c.rgb = lerp(c.rgb, grey.xxx * 0.6, _Ghost);
                c.a *= lerp(1, 0.6, _Ghost);
                return c * i.color;
            }
            ENDHLSL
        }
    }
}
