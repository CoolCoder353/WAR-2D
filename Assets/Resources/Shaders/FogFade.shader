Shader "Hidden/WAR2D/FogFade"
{
    // One frame of the fog's fade (FogView): every texel of the current fog moved toward its target level by _Step levels of 255,
    // stopping on it, exactly as FogView.Toward does with bytes. A full-screen triangle into an R8 render texture.
    SubShader
    {
        ZTest Always ZWrite Off Cull Off Blend Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_Current);
            TEXTURE2D(_Target);
            float _Step;

            float4 vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2 - 1, 0, 1);
            }

            float frag(float4 pos : SV_POSITION) : SV_Target
            {
                uint2 texel = (uint2)pos.xy;
                float current = round(LOAD_TEXTURE2D(_Current, texel).r * 255);
                float target = round(LOAD_TEXTURE2D(_Target, texel).r * 255);
                float next = current < target ? min(target, current + _Step) : max(target, current - _Step);
                return next / 255;
            }
            ENDHLSL
        }
    }
}
