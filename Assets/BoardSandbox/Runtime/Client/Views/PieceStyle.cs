using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Mods;
using UnityEngine;

namespace BoardSandbox.Client.Views
{
    // How one piece looks: its shape, its color and its size relative to a board cell.
    public readonly struct PieceStyle
    {
        public const float MinScale = 0.25f;
        public const float MaxScale = 3f;

        private static readonly Color32 Neutral = new Color32(0x8a, 0x8a, 0x8a, 255);

        public PieceStyle(PieceShape shape, Color32 color, float scale)
        {
            Shape = shape;
            Color = color;
            Scale = scale;
        }

        public PieceShape Shape { get; }
        public Color32 Color { get; }
        public float Scale { get; }

        public bool Equals(PieceStyle other)
        {
            return Shape == other.Shape && Scale == other.Scale
                && Color.r == other.Color.r && Color.g == other.Color.g && Color.b == other.Color.b && Color.a == other.Color.a;
        }

        // The piece's own "color" property wins, then the color of the seat that owns it.
        public static PieceStyle For(ViewObject piece, ViewHints hints, ModManifest manifest)
        {
            Color32 color = ColorParser.Parse(piece.Props["color"].AsString(), SeatColor(manifest, piece.Owner));
            float scale = Mathf.Clamp((float)piece.Props["scale"].AsDouble(1.0), MinScale, MaxScale);
            return new PieceStyle(hints.ShapeFor(piece.Kind), color, scale);
        }

        public static Color32 SeatColor(ModManifest manifest, int seat)
        {
            SeatInfo info = manifest?.GetSeat(seat);
            return info == null ? Neutral : ColorParser.Parse(info.Color, Neutral);
        }
    }
}
