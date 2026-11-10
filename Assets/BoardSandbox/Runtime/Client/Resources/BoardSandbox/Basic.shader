Shader "BoardSandbox/Basic"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Glossiness ("Shininess", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            half _Glossiness;

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
                float3 world : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                #if defined(UNITY_COLORSPACE_GAMMA)
                o.color = v.color;
                #else
                o.color = fixed4(GammaToLinearSpace(v.color.rgb), v.color.a);
                #endif
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 lightDir = normalize(float3(-0.35, 0.85, -0.4));
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.world);
                float diffuse = saturate(dot(n, lightDir));
                float3 halfDir = normalize(lightDir + viewDir);
                float specular = pow(saturate(dot(n, halfDir)), 48.0) * _Glossiness;
                fixed3 albedo = i.color.rgb * _Color.rgb;
                fixed3 lit = albedo * (0.45 + 0.65 * diffuse) + specular;
                return fixed4(lit, 1);
            }
            ENDCG
        }
    }
}
