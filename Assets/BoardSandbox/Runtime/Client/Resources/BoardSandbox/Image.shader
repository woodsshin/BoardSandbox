Shader "BoardSandbox/Image"
{
    Properties
    {
        _MainTex ("Image", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }
        LOD 100

        Pass
        {
            Cull Off
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float4 _BseLightDirection;
            fixed4 _BseLightColor;
            float4 _BseLightLevels;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 lightDir = normalize(float3(-0.35, 0.85, -0.4));
                float3 lightColor = float3(1, 1, 1);
                float ambient = 0.45;
                float strength = 0.65;
                if (_BseLightLevels.w > 0.5)
                {
                    lightDir = normalize(_BseLightDirection.xyz);
                    lightColor = _BseLightColor.rgb;
                    ambient = _BseLightLevels.x;
                    strength = _BseLightLevels.y;
                }
                // The image is lit like the face it lies on, from either side.
                float diffuse = saturate(abs(dot(n, lightDir)));
                fixed3 albedo = tex2D(_MainTex, i.uv).rgb * _Color.rgb;
                return fixed4(albedo * (ambient + strength * diffuse * lightColor), 1);
            }
            ENDCG
        }
    }
}
