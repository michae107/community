Shader "Hidden/IdleRpg/TileCoordinates"
{
    Properties
    {
        _GlyphAtlas ("Glyph coverage atlas", 2D) = "black" {}
        _Tint ("Letter color", Color) = (1, 1, 1, 0.92)
        _CharacterWidth ("Character width", Float) = 0.62
        _CharacterHeight ("Character height", Float) = 1.04
        _CharacterCount ("Character count", Float) = 0
        _GlyphsA ("Packed glyphs A", Vector) = (0, 0, 0, 0)
        _GlyphsB ("Packed glyphs B", Vector) = (0, 0, 0, 0)
        _TileOrigin ("Tile origin", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _GlyphAtlas;
            float4 _Tint;
            float4 _GlyphsA, _GlyphsB, _TileOrigin;
            float _CharacterCount, _CharacterWidth, _CharacterHeight;

            struct VertexInput { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct FragmentInput
            {
                float4 position : SV_POSITION;
                float3 tilePosition : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.position = UnityObjectToClipPos(input.vertex);
                float3 relative = mul(unity_ObjectToWorld, input.vertex).xyz - _TileOrigin.xyz;
                // glTF import mirrors X; reverse that for Godot's atlas layout.
                output.tilePosition = float3(-relative.x, relative.y, relative.z);
                output.normal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            fixed4 frag(FragmentInput input) : SV_Target
            {
                float2 textPosition = float2(input.tilePosition.x / _CharacterWidth + _CharacterCount * .5,
                    input.tilePosition.z / _CharacterHeight + .5);
                // Continuous derivatives keep mip choice stable at glyph borders.
                float2 gradientX = ddx(textPosition) / float2(8, -5);
                float2 gradientY = ddy(textPosition) / float2(8, -5);
                if (_CharacterCount < 1 || normalize(input.normal).y < .9 ||
                    (abs(input.tilePosition.y) > .03 && abs(input.tilePosition.y + .2) > .03)) discard;
                if (textPosition.x < 0 || textPosition.x >= _CharacterCount || textPosition.y < 0 || textPosition.y >= 1) discard;

                int index = (int)floor(textPosition.x);
                int slot = index / 3;
                int packed = (int)(slot < 4 ? _GlyphsA[slot] : _GlyphsB[slot - 4]);
                int glyph = (packed >> ((index % 3) * 6)) & 63;
                float2 cell = float2(glyph % 8, glyph / 8);
                float2 uv = (cell + float2(frac(textPosition.x), textPosition.y)) / float2(8, 5);
                uv.y = 1 - uv.y;
                float coverage = tex2Dgrad(_GlyphAtlas, uv, gradientX, gradientY).r;
                return fixed4(_Tint.rgb, coverage * _Tint.a);
            }
            ENDCG
        }
    }
}
