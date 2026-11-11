using System;
using System.Collections.Generic;
using BoardSandbox.Client.Rendering;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The look of the interface: flat dark panels and a font that has Korean glyphs when the system offers one.
    // Styles are built from the current skin, so Ensure has to be called from inside OnGUI.
    public sealed class GuiTheme : IDisposable
    {
        private static readonly string[] ProportionalFonts =
        {
            "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic", "Segoe UI", "Helvetica Neue", "Arial"
        };

        private static readonly string[] MonospacedFonts =
        {
            "D2Coding", "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New"
        };

        public static readonly Color TextColor = new Color(0.90f, 0.92f, 0.95f, 1f);
        public static readonly Color MutedColor = new Color(0.60f, 0.66f, 0.72f, 1f);
        public static readonly Color AccentColor = new Color(0.30f, 0.55f, 0.93f, 1f);
        public static readonly Color WarningColor = new Color(0.96f, 0.76f, 0.30f, 1f);
        public static readonly Color ErrorColor = new Color(0.96f, 0.42f, 0.38f, 1f);

        private static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.10f, 0.94f);
        private static readonly Color RaisedColor = new Color(0.17f, 0.19f, 0.24f, 1f);
        private static readonly Color HoverColor = new Color(0.24f, 0.27f, 0.34f, 1f);
        private static readonly Color PressedColor = new Color(0.12f, 0.14f, 0.18f, 1f);
        private static readonly Color CardColor = new Color(0.10f, 0.12f, 0.15f, 1f);
        private static readonly Color RowColor = new Color(0.12f, 0.14f, 0.18f, 1f);
        private static readonly Color RowSelectedColor = new Color(0.16f, 0.30f, 0.52f, 1f);
        private static readonly Color DimColor = new Color(0f, 0f, 0f, 0.62f);
        private static readonly Color FieldColor = new Color(0.04f, 0.05f, 0.07f, 1f);
        private static readonly Color FieldFocusColor = new Color(0.07f, 0.09f, 0.13f, 1f);
        private static readonly Color InfoColor = new Color(0.10f, 0.14f, 0.20f, 0.96f);
        private static readonly Color WarningBackColor = new Color(0.22f, 0.17f, 0.06f, 0.96f);
        private static readonly Color ErrorBackColor = new Color(0.24f, 0.09f, 0.08f, 0.96f);

        private readonly Dictionary<Color, Texture2D> _solids = new Dictionary<Color, Texture2D>();
        private Font _font;
        private Font _monoFont;
        private bool _fontsChosen;

        public float Scale { get; private set; }

        public GUIStyle Panel { get; private set; }
        public GUIStyle Heading { get; private set; }
        public GUIStyle Label { get; private set; }
        public GUIStyle Wrapped { get; private set; }
        public GUIStyle Muted { get; private set; }
        public GUIStyle Banner { get; private set; }
        public GUIStyle Button { get; private set; }
        public GUIStyle Field { get; private set; }
        public GUIStyle Toggle { get; private set; }
        public GUIStyle Mono { get; private set; }
        public GUIStyle Swatch { get; private set; }
        public GUIStyle Card { get; private set; }
        public GUIStyle RowButton { get; private set; }
        public GUIStyle RowName { get; private set; }
        public GUIStyle RowInfo { get; private set; }
        public GUIStyle TileLetter { get; private set; }
        public GUIStyle Badge { get; private set; }
        public GUIStyle InfoToast { get; private set; }
        public GUIStyle WarningToast { get; private set; }
        public GUIStyle ErrorToast { get; private set; }

        public Texture2D TrackTexture { get; private set; }
        public Texture2D FillTexture { get; private set; }

        // Darkens everything behind a full-screen panel.
        public Texture2D DimTexture { get; private set; }

        // The color of the tile that stands for a game; the hue is in [0, 1).
        public static Color TileColor(float hue)
        {
            return Color.HSVToRGB(Mathf.Repeat(hue, 1f), 0.5f, 0.62f);
        }

        public int S(float value)
        {
            return Mathf.RoundToInt(value * Scale);
        }

        public float Sf(float value)
        {
            return value * Scale;
        }

        public void Ensure(float scale)
        {
            scale = Mathf.Clamp(scale, 0.75f, 3f);
            if (Panel != null && Mathf.Approximately(scale, Scale))
                return;
            Scale = scale;
            if (!_fontsChosen)
            {
                _font = CreateFont(ProportionalFonts);
                _monoFont = CreateFont(MonospacedFonts);
                TrackTexture = Solid(FieldColor);
                FillTexture = Solid(AccentColor);
                DimTexture = Solid(DimColor);
                _fontsChosen = true;
            }
            Build();
        }

        public void Dispose()
        {
            foreach (Texture2D texture in _solids.Values)
                ObjectUtil.Destroy(texture);
            _solids.Clear();
            ObjectUtil.Destroy(_font);
            ObjectUtil.Destroy(_monoFont);
            _font = null;
            _monoFont = null;
            Panel = null;
        }

        private void Build()
        {
            GUISkin skin = GUI.skin;

            Panel = new GUIStyle(skin.box)
            {
                border = new RectOffset(),
                padding = new RectOffset(S(12), S(12), S(10), S(10)),
                margin = new RectOffset(0, 0, 0, S(8)),
                alignment = TextAnchor.UpperLeft
            };
            Paint(Panel.normal, PanelColor, TextColor);

            Label = Text(skin.label, 13, TextColor, false);
            Label.margin = new RectOffset(S(2), S(2), S(3), S(3));
            Wrapped = Text(skin.label, 13, TextColor, true);
            Wrapped.margin = new RectOffset(S(2), S(2), S(3), S(3));
            Muted = Text(skin.label, 12, MutedColor, true);
            Muted.margin = new RectOffset(S(2), S(2), S(1), S(5));
            Heading = Text(skin.label, 17, TextColor, false);
            Heading.fontStyle = FontStyle.Bold;
            Heading.margin = new RectOffset(S(2), S(2), S(0), S(4));

            Banner = new GUIStyle(Panel)
            {
                fontSize = S(16),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                font = _font,
                padding = new RectOffset(S(18), S(18), S(8), S(8))
            };
            Banner.normal.textColor = TextColor;

            Button = new GUIStyle(skin.button)
            {
                font = _font,
                fontSize = S(13),
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(),
                padding = new RectOffset(S(10), S(10), S(5), S(5)),
                margin = new RectOffset(S(2), S(2), S(2), S(2)),
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip
            };
            Paint(Button.normal, RaisedColor, TextColor);
            Paint(Button.hover, HoverColor, TextColor);
            Paint(Button.active, PressedColor, TextColor);
            Paint(Button.focused, RaisedColor, TextColor);
            Paint(Button.onNormal, AccentColor, Color.white);
            Paint(Button.onHover, Color.Lerp(AccentColor, Color.white, 0.15f), Color.white);
            Paint(Button.onActive, Color.Lerp(AccentColor, Color.black, 0.2f), Color.white);
            Paint(Button.onFocused, AccentColor, Color.white);

            Field = new GUIStyle(skin.textField)
            {
                font = _font,
                fontSize = S(13),
                border = new RectOffset(),
                padding = new RectOffset(S(8), S(8), S(5), S(5)),
                margin = new RectOffset(S(2), S(2), S(2), S(2))
            };
            Paint(Field.normal, FieldColor, TextColor);
            Paint(Field.hover, FieldColor, TextColor);
            Paint(Field.active, FieldFocusColor, TextColor);
            Paint(Field.focused, FieldFocusColor, TextColor);

            Toggle = new GUIStyle(skin.toggle)
            {
                font = _font,
                fontSize = S(13),
                richText = false,
                margin = new RectOffset(S(2), S(2), S(4), S(4))
            };
            ColorText(Toggle, TextColor);

            Mono = new GUIStyle(skin.label)
            {
                font = _monoFont != null ? _monoFont : _font,
                fontSize = S(12),
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip,
                margin = new RectOffset(S(2), S(2), 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };
            Mono.normal.textColor = TextColor;

            Swatch = new GUIStyle
            {
                fixedWidth = S(14),
                fixedHeight = S(14),
                margin = new RectOffset(S(2), S(6), S(7), 0)
            };
            Swatch.normal.background = Texture2D.whiteTexture;

            Card = new GUIStyle(Panel)
            {
                padding = new RectOffset(S(14), S(14), S(12), S(12)),
                margin = new RectOffset(S(8), 0, 0, 0)
            };
            Paint(Card.normal, CardColor, TextColor);

            RowButton = new GUIStyle(skin.button)
            {
                border = new RectOffset(),
                padding = new RectOffset(),
                margin = new RectOffset(),
                clipping = TextClipping.Clip
            };
            Paint(RowButton.normal, RowColor, TextColor);
            Paint(RowButton.hover, HoverColor, TextColor);
            Paint(RowButton.active, PressedColor, TextColor);
            Paint(RowButton.focused, RowColor, TextColor);
            Paint(RowButton.onNormal, RowSelectedColor, Color.white);
            Paint(RowButton.onHover, Color.Lerp(RowSelectedColor, Color.white, 0.1f), Color.white);
            Paint(RowButton.onActive, Color.Lerp(RowSelectedColor, Color.black, 0.2f), Color.white);
            Paint(RowButton.onFocused, RowSelectedColor, Color.white);

            RowName = Line(14, TextColor, TextAnchor.MiddleLeft);
            RowName.fontStyle = FontStyle.Bold;
            RowInfo = Line(11, MutedColor, TextAnchor.MiddleLeft);
            Badge = Line(11, Color.Lerp(AccentColor, Color.white, 0.45f), TextAnchor.MiddleRight);
            TileLetter = Line(20, Color.white, TextAnchor.MiddleCenter);
            TileLetter.fontStyle = FontStyle.Bold;

            InfoToast = Toast(InfoColor, TextColor);
            WarningToast = Toast(WarningBackColor, WarningColor);
            ErrorToast = Toast(ErrorBackColor, ErrorColor);
        }

        private GUIStyle Toast(Color background, Color text)
        {
            GUIStyle style = new GUIStyle(Panel)
            {
                font = _font,
                fontSize = S(13),
                wordWrap = true,
                richText = false,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(S(14), S(14), S(8), S(8)),
                margin = new RectOffset(0, 0, 0, S(6))
            };
            Paint(style.normal, background, text);
            return style;
        }

        // One clipped line of text that is placed with an explicit rectangle.
        private GUIStyle Line(int size, Color color, TextAnchor alignment)
        {
            GUIStyle style = Text(GUI.skin.label, size, color, false);
            style.alignment = alignment;
            style.clipping = TextClipping.Clip;
            style.margin = new RectOffset();
            style.padding = new RectOffset();
            return style;
        }

        private GUIStyle Text(GUIStyle basis, int size, Color color, bool wrap)
        {
            GUIStyle style = new GUIStyle(basis)
            {
                font = _font,
                fontSize = S(size),
                wordWrap = wrap,
                richText = false
            };
            ColorText(style, color);
            return style;
        }

        private static void ColorText(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        private void Paint(GUIStyleState state, Color background, Color text)
        {
            state.background = Solid(background);
            state.textColor = text;
        }

        private Texture2D Solid(Color color)
        {
            if (_solids.TryGetValue(color, out Texture2D existing) && existing != null)
                return existing;
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            _solids[color] = texture;
            return texture;
        }

        private static Font CreateFont(string[] preferred)
        {
            try
            {
                string[] installed = Font.GetOSInstalledFontNames();
                List<string> usable = new List<string>();
                foreach (string name in preferred)
                {
                    foreach (string have in installed)
                    {
                        if (string.Equals(name, have, StringComparison.OrdinalIgnoreCase))
                        {
                            usable.Add(have);
                            break;
                        }
                    }
                }
                return usable.Count == 0 ? null : Font.CreateDynamicFontFromOSFont(usable.ToArray(), 16);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
