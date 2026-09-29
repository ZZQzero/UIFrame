Shader "Hidden/UIFrame/ImageTransform"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Orientation ("Orientation", Float) = 1
        _Composite ("Composite", Float) = 0
        _Background ("Background", Vector) = (1,1,1,1)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Orientation, _Composite;
            float4 _Background;
            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_Orientation == 2) uv = float2(1-uv.x, uv.y);
                else if (_Orientation == 3) uv = 1-uv;
                else if (_Orientation == 4) uv = float2(uv.x, 1-uv.y);
                else if (_Orientation == 5) uv = float2(1-uv.y, 1-uv.x);
                else if (_Orientation == 6) uv = float2(1-uv.y, uv.x);
                else if (_Orientation == 7) uv = uv.yx;
                else if (_Orientation == 8) uv = float2(uv.y, 1-uv.x);
                float4 color = tex2D(_MainTex, uv);
                if (_Composite != 0)
                {
                    #ifndef UNITY_COLORSPACE_GAMMA
                    color.rgb = LinearToGammaSpace(color.rgb);
                    #endif
                    color.rgb = color.rgb * color.a + _Background.rgb * (1-color.a);
                    #ifndef UNITY_COLORSPACE_GAMMA
                    color.rgb = GammaToLinearSpace(color.rgb);
                    #endif
                    color.a = 1;
                }
                return color;
            }
            ENDCG
        }
    }
}
