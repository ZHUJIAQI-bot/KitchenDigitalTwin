// 楼体发光窗格着色器：把远景楼变成「亮着窗户」的夜城立面
// 按世界坐标分格，每格随机亮/暗；夜晚亮窗自发光。与 Glass/Water 一样走无光照 pass。
Shader "Custom/BuildingWindows"
{
    Properties
    {
        _Facade ("墙体色", Color) = (0.22, 0.25, 0.33, 1)
        _Roof ("屋顶色", Color) = (0.16, 0.18, 0.24, 1)
        _WindowLit ("亮窗色", Color) = (1.0, 0.85, 0.55, 1)
        _WindowDim ("暗窗色", Color) = (0.09, 0.11, 0.16, 1)
        _WinW ("窗宽", Float) = 1.6
        _WinH ("窗高", Float) = 2.6
        _Glow ("夜间亮窗", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Facade;
            fixed4 _Roof;
            fixed4 _WindowLit;
            fixed4 _WindowDim;
            float _WinW;
            float _WinH;
            float _Glow;

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

            float hash2(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 an = abs(n);

                // 顶面 = 屋顶（无窗）
                if (an.y > 0.9)
                {
                    return _Roof;
                }

                // 侧面按法线选择贴面坐标（X 面用 z,y；Z 面用 x,y）
                float2 faceUV;
                if (an.x > 0.9)
                {
                    faceUV = float2(i.worldPos.z, i.worldPos.y);
                }
                else
                {
                    faceUV = float2(i.worldPos.x, i.worldPos.y);
                }

                float2 grid = float2(faceUV.x / _WinW, faceUV.y / _WinH);
                float2 cell = floor(grid);
                float2 f = frac(grid);

                // 窗框边距
                float m = 0.22;
                float inWin = step(m, f.x) * step(f.x, 1.0 - m)
                            * step(m, f.y) * step(f.y, 1.0 - m);

                // 每窗随机亮/暗（同一列加入轻微纵向相关性，更像真实开灯分布）
                float lit = step(0.66, hash2(cell + floor(i.worldPos.y * 0.02)));

                fixed4 win = lerp(_WindowDim, _WindowLit, lit);
                fixed4 col = lerp(_Facade, win, inWin);

                // 夜晚亮窗发光
                col.rgb += _Glow * lit * inWin * _WindowLit.rgb * 2.4;

                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}
