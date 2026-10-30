Shader "WAR2D/Terrain"
{
    // The map as one quad: each tile looks up its frame in the Tiles atlas from an RGBA32 index texture
    // (R = TileType, G = frame: autotile mask or random pick, and a player wall's damage state; B = a player
    // wall's owner palette index + 1, whose colour tints the frame's team pixels as TeamSprite does). Mip level comes from the tile-local
    // derivatives (no seams at tile edges) and stays inside the frame's padding; at the farthest zoom the
    // tiles fade to their flat palette colour.
    Properties
    {
        _IndexTex ("Tile index (RGBA32)", 2D) = "black" {}
        _AtlasTex ("Tiles atlas", 2D) = "white" {}
        _MaskTex ("Tiles team mask", 2D) = "black" {}
        _TeamGain ("Team grey to owner colour", Float) = 1.55
        _MapSize ("Map size in tiles (xy)", Vector) = (1, 1, 0, 0)
        _TileSize ("Art pixels per tile", Float) = 64
        _MaxLod ("Deepest mip used (padding is 2 px, so 2 at most)", Float) = 2
        _FlatFrom ("Art px per screen px where the flat colour starts", Float) = 3
        _FlatTo ("Art px per screen px where it is fully flat", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_IndexTex);
            TEXTURE2D(_AtlasTex);
            SAMPLER(sampler_AtlasTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float4 _TeamColors[9];                // slot 0: no owner; 1..8: palette index + 1
            StructuredBuffer<float4> _FrameRects; // per atlas cell: xy = uv min, zw = uv size
            float _TileFirst[8];                  // per TileType: its first cell in the atlas
            float4 _TileFlat[8];                  // per TileType: its flat colour (linear when the project is)
            float4 _MapSize;
            float _TileSize, _MaxLod, _FlatFrom, _FlatTo, _TeamGain;

            // Large-scale variation (playtest 2026-10-30: ground and rock looked too even): smooth value noise over the tile grid.
            float Hash21(float2 p)
            {
                p = frac(p * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x), lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), f.x), f.y);
            }

            // Natural ground and rock (not player walls or blueprints) drift lighter and darker, warmer and cooler, in patches a few
            // to a dozen tiles across, so a field of the same tiles doesn't read as a repeating grid. Over that, the map falls into
            // zones a few dozen tiles across (warped so their edges wander): dusty brown ground, cool dark slate, and the bare grey
            // stone between. Every natural type gets the same tint at a point, since a rock tile's edge is floor art that must
            // match the floor tile beside it.
            half3 Weathered(half3 col, float2 tile)
            {
                float2 p = tile + 9.0 * (float2(ValueNoise(tile / 19.0 + 3.0), ValueNoise(tile / 19.0 + 91.0)) - 0.5);
                float dust = smoothstep(0.52, 0.72, ValueNoise(p / 27.0 + 5.0));
                float slate = smoothstep(0.55, 0.75, ValueNoise(p / 33.0 + 63.0)) * (1 - dust);
                float broad = ValueNoise(p / 11.0), fine = ValueNoise(tile / 3.7 + 17.0), mottle = ValueNoise(tile * 1.3 + 29.0);
                float light = 0.88 + 0.18 * broad + 0.08 * (fine - 0.5) + 0.06 * (mottle - 0.5);
                float warm = ValueNoise(tile / 17.0 + 41.0) - 0.5;
                half3 tint = half3(1 + 0.06 * warm, 1, 1 - 0.06 * warm);
                tint = lerp(tint, half3(1.16, 1.03, 0.82), dust);
                tint = lerp(tint, half3(0.86, 0.95, 1.12), slate);
                light *= 1 + 0.05 * dust - 0.08 * slate;
                return col * light * tint;
            }

            struct appdata { float3 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 tile : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex);
                o.tile = v.uv * _MapSize.xy;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                int2 cell = clamp((int2)floor(i.tile), 0, (int2)_MapSize.xy - 1);
                float3 index = LOAD_TEXTURE2D(_IndexTex, cell).rgb * 255.0 + 0.5;
                uint type = min((uint)index.x, 7u);
                uint variant = (uint)index.y;
                uint owner = min((uint)index.z, 8u);
                float4 rect = _FrameRects[(uint)_TileFirst[type] + variant];

                // Art pixels per screen pixel, from the continuous tile coordinate (frac() would break it).
                float2 dx = ddx(i.tile) * _TileSize, dy = ddy(i.tile) * _TileSize;
                float artPerPixel = max(length(dx), length(dy));
                float lod = clamp(log2(max(artPerPixel, 1e-4)), 0, _MaxLod);
                // Stay half a texel (at this mip) inside the frame, away from the padding.
                float inset = 0.5 * exp2(ceil(lod)) / _TileSize;
                float2 local = clamp(frac(i.tile), inset, 1 - inset);
                float2 uv = rect.xy + local * rect.zw;
                half4 art = SAMPLE_TEXTURE2D_LOD(_AtlasTex, sampler_AtlasTex, uv, lod);
                if (owner > 0)
                {
                    half mask = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, uv, lod).r;
                    art.rgb = lerp(art.rgb, saturate(art.rgb * (half3)_TeamColors[owner].rgb * _TeamGain), mask);
                }

                half flat = (half)saturate((artPerPixel - _FlatFrom) / max(_FlatTo - _FlatFrom, 1e-3));
                half3 col = lerp(art.rgb, _TileFlat[type].rgb, flat);
                if (type <= 3u) col = Weathered(col, i.tile);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
