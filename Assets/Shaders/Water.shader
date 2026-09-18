// 风格化水面着色器：透明 + 菲涅尔反光 + 移动高光 + 顶点波纹 + 夜间泛光
// 与 Glass.shader 一样走无光照 pass，不依赖 Standard 透明变体（避免 WebGL 剥离变白）
Shader "Custom/Water"
{
    Properties
    {
        _DeepColor ("深水色", Color) = (0.04, 0.16, 0.30, 0.82)
        _ShallowColor ("浅水色", Color) = (0.10, 0.34, 0.52, 0.82)
        _SkyReflect ("天光反射色", Color) = (0.55, 0.78, 0.95, 1)
        _Glow ("夜间泛光", Range(0, 1)) = 0
        _GlowColor ("泛光色", Color) = (0.20, 0.55, 0.95, 1)
        _FresnelPower ("菲涅尔强度", Range(0.5, 8)) = 3.0
        _WaveAmp ("波幅", Range(0, 0.5)) = 0.06
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _DeepColor;
            fixed4 _ShallowColor;
            fixed4 _SkyReflect;
            fixed4 _GlowColor;
            float _Glow;
            float _FresnelPower;
            float _WaveAmp;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                // 轻微波纹：仅扰动朝上的面（水面），避免侧面也抖
                float wave = sin(wp.x * 0.9 + _Time.y * 1.4) * 0.5
                           + cos(wp.z * 0.7 + _Time.y * 1.0) * 0.5;
                wp.y += wave * _WaveAmp * saturate(v.normal.y);
                o.worldPos = wp;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float fresnel = pow(1.0 - saturate(dot(viewDir, i.worldNormal)), _FresnelPower);

                fixed4 col = lerp(_DeepColor, _ShallowColor, fresnel * 0.6);
                col.rgb = lerp(col.rgb, _SkyReflect.rgb, fresnel);

                // 移动的高光斑
                float glint = pow(saturate(sin(i.worldPos.x * 0.5 + i.worldPos.z * 0.4 + _Time.y * 2.0)), 24.0);
                col.rgb += glint * _SkyReflect.rgb * 0.6;

                // 夜晚泛光（模拟对岸楼群的倒影光）
                col.rgb += _Glow * _GlowColor.rgb * (0.35 + 0.65 * fresnel);

                col.a = _DeepColor.a;
                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}
