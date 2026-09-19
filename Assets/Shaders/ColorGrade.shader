// 全屏色调：饱和度 / 曝光 / 对比度 / 白色色阶 / 黑色色阶
// 用在 OnRenderImage（Graphics.Blit）上，无需 post-processing 包
Shader "Custom/ColorGrade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Saturation ("饱和度", Range(0, 2)) = 1.4
        _Exposure ("曝光", Range(0.5, 2)) = 1.22
        _Contrast ("对比度", Range(0.5, 1.5)) = 0.82
        _WhiteLevel ("白色色阶", Range(0, 0.5)) = 0.03
        _BlackLevel ("黑色色阶", Range(0, 0.5)) = 0.08
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Saturation;
            float _Exposure;
            float _Contrast;
            float _WhiteLevel;
            float _BlackLevel;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);

                // 曝光
                col.rgb *= _Exposure;

                // 饱和度
                float lum = dot(col.rgb, float3(0.299, 0.587, 0.114));
                col.rgb = lerp(float3(lum, lum, lum), col.rgb, _Saturation);

                // 对比度
                col.rgb = (col.rgb - 0.5) * _Contrast + 0.5;

                // 白色色阶：提升高光
                col.rgb += _WhiteLevel * col.rgb;

                // 黑色色阶：提升阴影
                col.rgb += _BlackLevel * (1.0 - col.rgb);

                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}
