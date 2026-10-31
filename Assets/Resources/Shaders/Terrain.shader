Shader "WAR2D/Terrain"
{
    // The map as one quad: each tile looks up its frame in the Tiles atlas from an RGBA32 index texture
    // (R = TileType, G = frame: autotile mask or random pick, and a player wall's damage state; B = a player
    // wall's owner palette index + 1, whose colour tints the frame's team pixels as TeamSprite does; A = the tile's clearance,
    // MapView.Clearance). Over plain floor and deep rock it scatters feature stamps (FloorFeature, RockFeature frames): each tile
    // with room may carry one, at a hashed position, size, kind and rotation. Mip level comes from the tile-local
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
        _FeatureFirst ("First atlas cell of the floor (x) and rock (y) stamps", Vector) = (0, 0, 0, 0)
        _FeatureKinds ("Floor kinds, rock kinds, frames per kind, stamp tiles", Vector) = (0, 0, 8, 2)
        _FeatureDensity ("Chance a clear floor (x) or rock (y) tile carries a stamp", Vector) = (0.12, 0.1, 0, 0)
        _FeatureScale ("Stamp size range, of its full size (min, max, skew towards small, max stretch)", Vector) = (0.18, 1, 1.8, 0.35)
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
            float4 _FeatureFirst, _FeatureKinds, _FeatureDensity, _FeatureScale;

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

            float4 Hash42(float2 p)
            {
                float4 p4 = frac(p.xyxy * float4(0.1031, 0.1030, 0.0973, 0.1099));
                p4 += dot(p4, p4.wzxy + 33.33);
                return frac((p4.xxyz + p4.yzzw) * p4.zywx);
            }

            // The feature stamps over this pixel, blended in over col. A stamp belongs to the tile its centre is in and is at
            // most _FeatureKinds.w tiles across, so only the 3 x 3 tiles around this one can reach it. A floor stamp needs its
            // tile's 3 x 3 block all floor (clearance 1), a rock stamp the 5 x 5 block all rock (clearance 2), so neither
            // crosses a cliff, a gem or a wall. A frame is _FeatureKinds.w tiles of art: at scale s, 1 / s times the tile's
            // art pixels per tile, which sets its mip level.
            half3 Stamps(half3 col, float2 tile, int2 cell, float artPerPixel)
            {
                [unroll] for (int dy = -1; dy <= 1; dy++)
                [unroll] for (int dx = -1; dx <= 1; dx++)
                {
                    int2 t = cell + int2(dx, dy);
                    if (any(t < 0) || any(t >= (int2)_MapSize.xy)) continue;
                    float4 index = LOAD_TEXTURE2D(_IndexTex, t) * 255.0 + 0.5;
                    uint type = (uint)index.r, clearance = (uint)index.a;
                    bool rock = type == 1u;
                    if (!(type == 0u && clearance >= 1u) && !(rock && clearance >= 2u)) continue;
                    float kinds = rock ? _FeatureKinds.y : _FeatureKinds.x;
                    float4 h = Hash42((float2)t + (rock ? 71.3 : 0.0));
                    if (kinds < 1 || h.x >= (rock ? _FeatureDensity.y : _FeatureDensity.x)) continue;
                    float4 pick = Hash42((float2)t + 517.0);
                    // Mostly small, now and then large; stretched along one axis by up to 2^_FeatureScale.w either way (kept
                    // within the 3 x 3 tiles' reach).
                    float stretch = exp2((pick.z * 2 - 1) * _FeatureScale.w);
                    float s = min(lerp(_FeatureScale.x, _FeatureScale.y, pow(h.w, _FeatureScale.z)), 1 / max(stretch, 1 / stretch));
                    float2 centre = (float2)t + 0.1 + 0.8 * h.yz;
                    float2 local = (tile - centre) / (_FeatureKinds.w * s * float2(stretch, 1 / stretch)) + 0.5;
                    if (any(local <= 0) || any(local >= 1)) continue;
                    float frames = _FeatureKinds.z;
                    uint frame = (uint)(rock ? _FeatureFirst.y : _FeatureFirst.x) + (uint)(floor(pick.x * kinds) * frames + floor(pick.y * frames));
                    float4 rect = _FrameRects[frame];
                    float lod = clamp(log2(max(artPerPixel / s, 1e-4)), 0, _MaxLod);
                    float inset = 0.5 * exp2(ceil(lod)) / (_TileSize * _FeatureKinds.w);
                    half4 stamp = SAMPLE_TEXTURE2D_LOD(_AtlasTex, sampler_AtlasTex, rect.xy + clamp(local, inset, 1 - inset) * rect.zw, lod);
                    col = lerp(col, stamp.rgb, stamp.a);
                }
                return col;
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

                if (type <= 1u) art.rgb = Stamps(art.rgb, i.tile, cell, artPerPixel);

                half flat = (half)saturate((artPerPixel - _FlatFrom) / max(_FlatTo - _FlatFrom, 1e-3));
                half3 col = lerp(art.rgb, _TileFlat[type].rgb, flat);
                if (type <= 3u) col = Weathered(col, i.tile);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
