using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    // Colors as mods write them: "#rgb", "#rgba", "#rrggbb", "#rrggbbaa" or a name.
    public static class ColorParser
    {
        private static readonly Dictionary<string, Color32> Names = new Dictionary<string, Color32>(StringComparer.OrdinalIgnoreCase)
        {
            { "black", new Color32(0x17, 0x17, 0x1a, 255) },
            { "white", new Color32(0xf2, 0xef, 0xe6, 255) },
            { "red", new Color32(0xc8, 0x37, 0x2d, 255) },
            { "green", new Color32(0x3b, 0x8a, 0x4a, 255) },
            { "blue", new Color32(0x2f, 0x5f, 0xb3, 255) },
            { "yellow", new Color32(0xe3, 0xc1, 0x3a, 255) },
            { "orange", new Color32(0xe0, 0x7b, 0x2c, 255) },
            { "purple", new Color32(0x7b, 0x4b, 0xb0, 255) },
            { "brown", new Color32(0x7a, 0x4b, 0x2a, 255) },
            { "gray", new Color32(0x8a, 0x8a, 0x8a, 255) },
            { "grey", new Color32(0x8a, 0x8a, 0x8a, 255) },
            { "cyan", new Color32(0x2f, 0xb5, 0xc4, 255) },
            { "pink", new Color32(0xe5, 0x8b, 0xb2, 255) }
        };

        public static bool TryParse(string text, out Color32 color)
        {
            color = new Color32(255, 255, 255, 255);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (text[0] != '#')
            {
                if (!Names.TryGetValue(text, out Color32 named))
                    return false;
                color = named;
                return true;
            }

            string digits = text.Substring(1);
            if (digits.Length == 3 || digits.Length == 4)
            {
                char[] doubled = new char[digits.Length * 2];
                for (int i = 0; i < digits.Length; i++)
                {
                    doubled[i * 2] = digits[i];
                    doubled[i * 2 + 1] = digits[i];
                }
                digits = new string(doubled);
            }
            if (digits.Length != 6 && digits.Length != 8)
                return false;
            if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
                return false;
            if (digits.Length == 6)
                value = (value << 8) | 0xff;
            color = new Color32((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }

        public static Color32 Parse(string text, Color32 fallback)
        {
            return TryParse(text, out Color32 color) ? color : fallback;
        }
    }
}
