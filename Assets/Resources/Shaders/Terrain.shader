Shader "WAR2D/Terrain"
{
    // The map as one quad: each tile looks up its frame in the Tiles atlas from an RG8 index texture
    // (R = TileType, G = variant: autotile mask or random pick). Mip level comes from the tile-local
    // derivatives (no seams at tile edges) and stays inside the frame's padding; at the farthest zoom the
    // tiles fade to their flat palette colour.
    Properties
    {
        _IndexTex ("Tile index (RG8)", 2D) = "black" {}
        _AtlasTex ("Tiles atlas", 2D) = "white" {}
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
            StructuredBuffer<float4> _FrameRects; // per atlas cell: xy = uv min, zw = uv size
            float _TileFirst[8];                  // per TileType: its first cell in the atlas
            float4 _TileFlat[8];                  // per TileType: its flat colour (linear when the project is)
            float4 _MapSize;
            float _TileSize, _MaxLod, _FlatFrom, _FlatTo;

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
                float2 index = LOAD_TEXTURE2D(_IndexTex, cell).rg * 255.0 + 0.5;
                uint type = min((uint)index.x, 7u);
                uint variant = (uint)index.y;
                float4 rect = _FrameRects[(uint)_TileFirst[type] + variant];

                // Art pixels per screen pixel, from the continuous tile coordinate (frac() would break it).
                float2 dx = ddx(i.tile) * _TileSize, dy = ddy(i.tile) * _TileSize;
                float artPerPixel = max(length(dx), length(dy));
                float lod = clamp(log2(max(artPerPixel, 1e-4)), 0, _MaxLod);
                // Stay half a texel (at this mip) inside the frame, away from the padding.
                float inset = 0.5 * exp2(ceil(lod)) / _TileSize;
                float2 local = clamp(frac(i.tile), inset, 1 - inset);
                half4 art = SAMPLE_TEXTURE2D_LOD(_AtlasTex, sampler_AtlasTex, rect.xy + local * rect.zw, lod);

                half flat = (half)saturate((artPerPixel - _FlatFrom) / max(_FlatTo - _FlatFrom, 1e-3));
                return half4(lerp(art.rgb, _TileFlat[type].rgb, flat), 1);
            }
            ENDHLSL
        }
    }
}
