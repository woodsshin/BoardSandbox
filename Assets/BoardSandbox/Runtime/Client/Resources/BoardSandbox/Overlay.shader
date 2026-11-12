Shader "BoardSandbox/Overlay"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 0.5)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float4 _BseLightDirection;
            float4 _BseLightLevels;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR0;
                float3 normal : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 lightDir = normalize(float3(-0.35, 0.85, -0.4));
                if (_BseLightLevels.w > 0.5)
                    lightDir = normalize(_BseLightDirection.xyz);
                float diffuse = saturate(dot(normalize(i.normal), lightDir));
                fixed3 rgb = _Color.rgb * i.color.rgb * (0.6 + 0.4 * diffuse);
                return fixed4(rgb, _Color.a * i.color.a);
            }
            ENDCG
        }
    }
}
