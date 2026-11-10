using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    public enum PieceShape
    {
        Stone,
        Disc,
        Cube
    }

    // The shapes the table is made of. Pieces are modelled in units of one board cell with their foot on the
    // origin, in white so that their material decides the color.
    public static class BoardShapes
    {
        public static readonly Color32 White = new Color32(255, 255, 255, 255);

        private static readonly Color32 Wood = new Color32(0xdc, 0xb0, 0x6b, 255);
        private static readonly Color32 Ink = new Color32(0x2b, 0x1d, 0x10, 255);
        private static readonly Color32 LightSquare = new Color32(0xec, 0xd4, 0xa0, 255);
        private static readonly Color32 DarkSquare = new Color32(0x9a, 0x6a, 0x3c, 255);
        private static readonly Color32 Cloth = new Color32(0x27, 0x31, 0x3b, 255);

        private const float LineHeight = 0.004f;

        // The height at which the foot of a piece rests above the board surface.
        public const float RestHeight = 0.01f;

        public static float PieceHeight(PieceShape shape)
        {
            switch (shape)
            {
                case PieceShape.Stone:
                    return 0.36f;
                case PieceShape.Cube:
                    return 0.7f;
                default:
                    return 0.16f;
            }
        }

        public static MeshBuilder Piece(PieceShape shape)
        {
            MeshBuilder builder = new MeshBuilder();
            switch (shape)
            {
                case PieceShape.Stone:
                    builder.AddStone(Vector3.zero, 0.46f, PieceHeight(shape), 32, 12, White);
                    break;
                case PieceShape.Cube:
                    builder.AddBox(new Vector3(0f, PieceHeight(shape) * 0.5f, 0f), new Vector3(0.7f, PieceHeight(shape), 0.7f), White);
                    break;
                default:
                    builder.AddCylinder(Vector3.zero, 0.42f, PieceHeight(shape), 40, White);
                    break;
            }
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
        public static MeshBuilder Table(float halfExtent)
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddQuad(new Vector3(-halfExtent, 0f, -halfExtent), new Vector3(-halfExtent, 0f, halfExtent),
                new Vector3(halfExtent, 0f, halfExtent), new Vector3(halfExtent, 0f, -halfExtent), Vector3.up, Cloth);
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
        public static MeshBuilder Board(BoardLayout layout)
        {
            MeshBuilder builder = new MeshBuilder();
            builder.AddBox(new Vector3(0f, -layout.Thickness * 0.5f, 0f),
                new Vector3(layout.SlabHalfWidth * 2f, layout.Thickness, layout.SlabHalfDepth * 2f), Wood);

            if (layout.Style == BoardStyle.Checker)
                AddSquares(builder, layout);
            else
                AddLines(builder, layout);
            return builder;
        }

        private static void AddSquares(MeshBuilder builder, BoardLayout layout)
        {
            float half = layout.CellSize * 0.5f;
            for (int y = 1; y <= layout.Rows; y++)
            {
                for (int x = 1; x <= layout.Columns; x++)
                {
                    Vector3 c = layout.CellToWorld(x, y, LineHeight);
                    Color32 color = ((x + y) & 1) == 0 ? LightSquare : DarkSquare;
                    builder.AddQuad(c + new Vector3(-half, 0f, -half), c + new Vector3(-half, 0f, half),
                        c + new Vector3(half, 0f, half), c + new Vector3(half, 0f, -half), Vector3.up, color);
                }
            }
        }

        private static void AddLines(MeshBuilder builder, BoardLayout layout)
        {
            float thin = layout.CellSize * 0.045f;
            float thick = layout.CellSize * 0.085f;
            float left = -layout.HalfWidth;
            float right = layout.HalfWidth;
            float far = layout.HalfDepth;
            float near = -layout.HalfDepth;

            for (int x = 1; x <= layout.Columns; x++)
            {
                float width = x == 1 || x == layout.Columns ? thick : thin;
                float worldX = layout.CellToWorld(x, 1).x;
                AddStrip(builder, new Vector3(worldX - width * 0.5f, LineHeight, near - thick * 0.5f),
                    new Vector3(worldX + width * 0.5f, LineHeight, far + thick * 0.5f), Ink);
            }
            for (int y = 1; y <= layout.Rows; y++)
            {
                float width = y == 1 || y == layout.Rows ? thick : thin;
                float worldZ = layout.CellToWorld(1, y).z;
                AddStrip(builder, new Vector3(left - thick * 0.5f, LineHeight, worldZ - width * 0.5f),
                    new Vector3(right + thick * 0.5f, LineHeight, worldZ + width * 0.5f), Ink);
            }

            foreach (Vector2Int point in StarPoints(layout.Columns, layout.Rows))
                builder.AddDisc(layout.CellToWorld(point.x, point.y, LineHeight * 2f), layout.CellSize * 0.1f, 20, Ink);
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
