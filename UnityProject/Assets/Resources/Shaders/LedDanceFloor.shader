Shader "TikTokLiveGame/LedDanceFloor"
{
    Properties
    {
        _ColorA ("Violet", Color) = (0.62,0.08,1,1)
        _ColorB ("Cyan", Color) = (0.02,0.85,1,1)
        _Brightness ("Brightness", Range(0,2)) = 1.5
        _Rainbow ("Rainbow", Float) = 1
        _Pattern ("Wave / Checker / Ripple", Float) = 0
        _Beat ("Shared club beat", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            float4 _ColorA, _ColorB;
            float _Brightness, _Pattern, _Beat, _Rainbow;
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 grid = i.uv * float2(16,12);
                float2 cell = floor(grid);
                float2 local = frac(grid);
                float edge = min(min(local.x, 1-local.x), min(local.y, 1-local.y));
                float aa = max(max(fwidth(grid.x), fwidth(grid.y)), 0.002);
                float panel = smoothstep(0.034-aa, 0.034+aa, edge);
                float wave = sin(cell.x * 0.6 + cell.y * 0.7 - _Beat * 0.8) * 0.5 + 0.5;
                if (_Pattern > 1.5)
                    wave = sin(length(cell - float2(7.5,5.5)) * 1.1 - _Beat) * 0.5 + 0.5;
                else if (_Pattern > 0.5)
                    wave = 0.5 + 0.5 * cos((cell.x + cell.y) * UNITY_PI) * sin(_Beat * UNITY_PI);
                float pulse = 0.88 + 0.12 * cos(_Beat * 2 * UNITY_PI);
                float3 tint = lerp(_ColorB.rgb, _ColorA.rgb, smoothstep(0.35,0.65,wave));
                float hue = frac(cell.x * 0.065 + cell.y * 0.08 - _Beat * 0.035);
                float3 rainbow = lerp(float3(1,1,1), saturate(abs(frac(hue + float3(0,0.666667,0.333333))*6-3)-1), 0.82);
                tint = lerp(tint, rainbow, _Rainbow);
                float rim = 1 - smoothstep(0.045,0.095,edge);
                float center = 1 - length(local - 0.5) * 0.42;
                float light = (0.24 + 0.76 * wave) * center * pulse + rim * 0.22;
                float3 baseColor = float3(0.008,0.01,0.022);
                float3 emission = tint * light * _Brightness * panel;
                // A thin light strip frames the floor independently of the tile gaps.
                float border = min(min(i.uv.x,1-i.uv.x),min(i.uv.y,1-i.uv.y));
                emission += lerp(_ColorA.rgb,_ColorB.rgb,i.uv.x) * (1-smoothstep(0.002,0.005,border)) * _Brightness * 0.7;
                return fixed4(baseColor + emission,1);
            }
            ENDCG
        }
    }
}
