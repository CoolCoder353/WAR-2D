Shader "WAR2D/Fog"
{
    // The fog overlay: the bilinear fog texture (alpha 1 unexplored, 140/255 explored, 0 seen) quantised to
    // those three levels with a 4×4 ordered dither on 2-art-pixel blocks, so edges read as pixel art.
    Properties
    {
        _MainTex ("Fog (one texel per cell)", 2D) = "black" {}
        _PixelsPerUnit ("Art pixels per world unit", Float) = 64
        _Block ("Art pixels per dither cell", Float) = 2
        _Explored ("Explored alpha", Float) = 0.549
        _WorldSize ("Fog quad size in world units (xy)", Vector) = (1, 1, 0, 0)
    }
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _PixelsPerUnit, _Block, _Explored;
            float4 _WorldSize;

            struct appdata { float3 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 world : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex);
                o.uv = v.uv;
                o.world = TransformObjectToWorld(v.vertex).xy;
                return o;
            }

            static const float Bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

            half4 frag(v2f i) : SV_Target
            {
                // Sample at the dither cell's centre so a cell is all one level.
                float2 cell = floor(i.world * _PixelsPerUnit / _Block);
                float2 world = (cell + 0.5) * _Block / _PixelsPerUnit;
                float2 uv = world / _WorldSize.xy; // the quad spans (0, 0) to _WorldSize
                float a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a;
                uint2 c = (uint2)(int2)cell & 3;
                float t = (Bayer[c.y * 4 + c.x] + 0.5) / 16.0;
                // Between seen and explored, or between explored and unexplored.
                float level = a < _Explored ? (a / _Explored > t ? _Explored : 0) : ((a - _Explored) / (1 - _Explored) > t ? 1 : _Explored);
                return half4(0, 0, 0, level);
            }
            ENDHLSL
        }
    }
}
