using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    public enum PieceShape
    {
        Stone,
        Disc,
        Cube,
        Box,
        Sphere,
        Cylinder,
        Cone
    }

    // The colors and proportions of the board; lengths are in cells.
    public sealed class BoardPalette
    {
        public static readonly BoardPalette Default = new BoardPalette();

        public Color32 Slab = new Color32(0xdc, 0xb0, 0x6b, 255);
        public Color32 Line = new Color32(0x2b, 0x1d, 0x10, 255);
        public Color32 Light = new Color32(0xec, 0xd4, 0xa0, 255);
        public Color32 Dark = new Color32(0x9a, 0x6a, 0x3c, 255);
        public float ThinLine = 0.045f;
        public float ThickLine = 0.085f;
        public bool Stars = true;
    }

    // The shapes the table is made of. Pieces are modelled in units of one board cell with their foot on the
    // origin, in white so that their material decides the color.
    public static class BoardShapes
    {
        public static readonly Color32 White = new Color32(255, 255, 255, 255);

        public const float StickerRise = 0.004f;

        private const float LineHeight = 0.004f;

        // The height at which the foot of a piece rests above the board surface.
        public const float RestHeight = 0.01f;

        public static bool TryParseShape(string name, out PieceShape shape)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "stone":
                    shape = PieceShape.Stone;
                    return true;
                case "disc":
                    shape = PieceShape.Disc;
                    return true;
                case "cube":
                    shape = PieceShape.Cube;
                    return true;
                case "box":
                    shape = PieceShape.Box;
                    return true;
                case "sphere":
                    shape = PieceShape.Sphere;
                    return true;
                case "cylinder":
                    shape = PieceShape.Cylinder;
                    return true;
                case "cone":
                    shape = PieceShape.Cone;
                    return true;
                default:
                    shape = PieceShape.Disc;
                    return false;
            }
        }

        // Width, height and depth of a shape before it is scaled.
        public static Vector3 NaturalSize(PieceShape shape)
        {
            switch (shape)
            {
                case PieceShape.Stone:
                    return new Vector3(0.92f, 0.36f, 0.92f);
                case PieceShape.Cube:
                    return new Vector3(0.7f, 0.7f, 0.7f);
                case PieceShape.Disc:
                    return new Vector3(0.84f, 0.16f, 0.84f);
                default:
                    return Vector3.one;
            }
        }

        public static bool IsRound(PieceShape shape)
        {
            return shape == PieceShape.Stone || shape == PieceShape.Sphere;
        }

        public static float PieceHeight(PieceShape shape)
        {
            return NaturalSize(shape).y;
        }

        // With centered set the origin is the middle of the shape instead of its foot.
        public static MeshBuilder Piece(PieceShape shape, bool centered = false, Color32? body = null, Color32?[] stickers = null, float inset = 0.1f)
        {
            MeshBuilder builder = new MeshBuilder();
            Vector3 size = NaturalSize(shape);
            Color32 color = body ?? White;
            switch (shape)
            {
                case PieceShape.Stone:
                case PieceShape.Sphere:
                    builder.AddStone(Vector3.zero, size.x * 0.5f, size.y, 32, shape == PieceShape.Sphere ? 16 : 12, color);
                    break;
                case PieceShape.Cube:
                case PieceShape.Box:
                    builder.AddStickerBox(new Vector3(0f, size.y * 0.5f, 0f), size, color, stickers, inset, StickerRise);
                    break;
                case PieceShape.Cone:
                    builder.AddCone(Vector3.zero, size.x * 0.5f, size.y, 40, color);
                    break;
                default:
                    builder.AddCylinder(Vector3.zero, size.x * 0.5f, size.y, 40, color);
                    break;
            }
            if (centered)
                builder.Translate(new Vector3(0f, -size.y * 0.5f, 0f));
            return builder;
        }

        // A small dot that marks the last move.
        public static MeshBuilder Marker()
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddDisc(Vector3.zero, 0.12f, 20, White);
            return builder;
        }

        // The faint shade a piece casts on the board.
        public static MeshBuilder Shadow()
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddDisc(Vector3.zero, 0.5f, 32, White);
            return builder;
        }

        // The cloth the board lies on; it is centred on the origin.
        public static MeshBuilder Table(float halfExtent, Color32 color)
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddQuad(new Vector3(-halfExtent, 0f, -halfExtent), new Vector3(-halfExtent, 0f, halfExtent),
                new Vector3(halfExtent, 0f, halfExtent), new Vector3(halfExtent, 0f, -halfExtent), Vector3.up, color);
            return builder;
        }

        // A ring that is drawn around the pieces of a winning line.
        public static MeshBuilder Highlight()
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddRing(Vector3.zero, 0.5f, 0.62f, 40, White);
            return builder;
        }

        // The slab with its lines or squares; the colors are part of the vertices.
        public static MeshBuilder Board(BoardLayout layout, BoardPalette palette = null)
        {
            palette = palette ?? BoardPalette.Default;
            MeshBuilder builder = new MeshBuilder();
            builder.AddBox(new Vector3(0f, -layout.Thickness * 0.5f, 0f),
                new Vector3(layout.SlabHalfWidth * 2f, layout.Thickness, layout.SlabHalfDepth * 2f), palette.Slab);

            if (layout.Style == BoardStyle.Checker)
                AddSquares(builder, layout, palette);
            else
                AddLines(builder, layout, palette);
            return builder;
        }

        private static void AddSquares(MeshBuilder builder, BoardLayout layout, BoardPalette palette)
        {
            float half = layout.CellSize * 0.5f;
            for (int y = 1; y <= layout.Rows; y++)
            {
                for (int x = 1; x <= layout.Columns; x++)
                {
                    Vector3 c = layout.CellToWorld(x, y, LineHeight);
                    Color32 color = ((x + y) & 1) == 0 ? palette.Light : palette.Dark;
                    builder.AddQuad(c + new Vector3(-half, 0f, -half), c + new Vector3(-half, 0f, half),
                        c + new Vector3(half, 0f, half), c + new Vector3(half, 0f, -half), Vector3.up, color);
                }
            }
        }

        private static void AddLines(MeshBuilder builder, BoardLayout layout, BoardPalette palette)
        {
            Color32 ink = palette.Line;
            float thin = layout.CellSize * palette.ThinLine;
            float thick = layout.CellSize * palette.ThickLine;
            float left = -layout.HalfWidth;
            float right = layout.HalfWidth;
            float far = layout.HalfDepth;
            float near = -layout.HalfDepth;

            for (int x = 1; x <= layout.Columns; x++)
            {
                float width = x == 1 || x == layout.Columns ? thick : thin;
                float worldX = layout.CellToWorld(x, 1).x;
                AddStrip(builder, new Vector3(worldX - width * 0.5f, LineHeight, near - thick * 0.5f),
                    new Vector3(worldX + width * 0.5f, LineHeight, far + thick * 0.5f), ink);
            }
            for (int y = 1; y <= layout.Rows; y++)
            {
                float width = y == 1 || y == layout.Rows ? thick : thin;
                float worldZ = layout.CellToWorld(1, y).z;
                AddStrip(builder, new Vector3(left - thick * 0.5f, LineHeight, worldZ - width * 0.5f),
                    new Vector3(right + thick * 0.5f, LineHeight, worldZ + width * 0.5f), ink);
            }

            if (!palette.Stars)
                return;
            foreach (Vector2Int point in StarPoints(layout.Columns, layout.Rows))
                builder.AddDisc(layout.CellToWorld(point.x, point.y, LineHeight * 2f), layout.CellSize * 0.1f, 20, ink);
        }

        private static void AddStrip(MeshBuilder builder, Vector3 min, Vector3 max, Color32 color)
        {
            builder.AddQuad(new Vector3(min.x, min.y, min.z), new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z), new Vector3(max.x, min.y, min.z), Vector3.up, color);
        }

        // The dots printed on the board: the corners at an offset of three or four lines and the middle.
        public static System.Collections.Generic.List<Vector2Int> StarPoints(int columns, int rows)
        {
            System.Collections.Generic.List<Vector2Int> points = new System.Collections.Generic.List<Vector2Int>();
            if (columns < 9 || rows < 9 || columns % 2 == 0 || rows % 2 == 0)
                return points;
            int offsetX = columns >= 13 ? 4 : 3;
            int offsetY = rows >= 13 ? 4 : 3;
            int[] xs = { offsetX, columns + 1 - offsetX };
            int[] ys = { offsetY, rows + 1 - offsetY };
            foreach (int x in xs)
            {
                foreach (int y in ys)
                    points.Add(new Vector2Int(x, y));
            }
            points.Add(new Vector2Int((columns + 1) / 2, (rows + 1) / 2));
            return points;
        }
    }
}
