Shader "TikTokLiveGame/StageBackdrop"
{
    Properties
    {
        _MainTex ("Backdrop", 2D) = "white" {}
        _Brightness ("Brightness", Float) = 1.25
        _Recolor ("Recolor", Float) = 1
        _ColorA ("First color", Color) = (0.7,0.1,1,1)
        _ColorB ("Second color", Color) = (0,1,1,1)
        _Beat ("Beat", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            sampler2D _MainTex;
            float4 _MainTex_ST, _ColorA, _ColorB;
            float _Brightness, _Recolor, _Beat;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=TRANSFORM_TEX(v.uv,_MainTex); return o; }
            fixed4 frag(v2f i):SV_Target
            {
                float3 original=tex2D(_MainTex,i.uv).rgb;
                float high=max(original.r,max(original.g,original.b));
                float low=min(original.r,min(original.g,original.b));
                float saturated=saturate((high-low)*3);
                float blend=0.5+0.5*sin(i.uv.x*4+i.uv.y*3+_Beat*0.16);
                float3 tint=lerp(_ColorA.rgb,_ColorB.rgb,blend);
                // Neutral lettering stays white while colored artwork receives the palette.
                float3 recolored=lerp(original,tint*high,saturated);
                return fixed4(lerp(original,recolored,_Recolor)*_Brightness,1);
            }
            ENDCG
        }
    }
}
