using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    public enum BoardStyle
    {
        // Pieces sit on the crossings of drawn lines.
        Lines,

        // Pieces sit on the squares of a checkered board.
        Checker
    }

    // Maps the 1-based grid coordinates of a mod to the table. The board lies on the y = 0 plane with its
    // centre at the origin; row 1 is on the far side (+z) and column 1 on the left (-x) of the default view.
    public sealed class BoardLayout
    {
        public const int MaxSize = 64;

        public BoardLayout(int columns, int rows, BoardStyle style = BoardStyle.Lines, float cellSize = 1f)
        {
            Columns = Mathf.Clamp(columns, 1, MaxSize);
            Rows = Mathf.Clamp(rows, 1, MaxSize);
            Style = style;
            CellSize = Mathf.Max(0.01f, cellSize);
        }

        public int Columns { get; }
        public int Rows { get; }
        public BoardStyle Style { get; }
        public float CellSize { get; }

        // Distance from the outermost coordinate to the edge of the board.
        public float Margin => CellSize * (Style == BoardStyle.Lines ? 0.8f : 0.9f);
        public float Thickness => CellSize * 0.4f;

        // Half of the extent covered by the coordinates themselves.
        public float HalfWidth => (Columns - 1) * CellSize * 0.5f;
        public float HalfDepth => (Rows - 1) * CellSize * 0.5f;

        public float SlabHalfWidth => HalfWidth + Margin;
        public float SlabHalfDepth => HalfDepth + Margin;

        public Vector3 CellToWorld(double x, double y, float height = 0f)
        {
            float worldX = (float)((x - (Columns + 1) * 0.5) * CellSize);
            float worldZ = (float)(((Rows + 1) * 0.5 - y) * CellSize);
            return new Vector3(worldX, height, worldZ);
        }

        // Fractional coordinates of a point on the table.
        public Vector2 WorldToCell(Vector3 world)
        {
            return new Vector2(world.x / CellSize + (Columns + 1) * 0.5f, (Rows + 1) * 0.5f - world.z / CellSize);
        }

        public bool Contains(int x, int y)
        {
            return x >= 1 && x <= Columns && y >= 1 && y <= Rows;
        }

        // The coordinate nearest to where the ray meets the board surface.
        public bool TryPick(Ray ray, out int x, out int y)
        {
            x = 0;
            y = 0;
            Plane surface = new Plane(Vector3.up, Vector3.zero);
            if (!surface.Raycast(ray, out float distance))
                return false;
            Vector2 cell = WorldToCell(ray.GetPoint(distance));
            int pickedX = Mathf.RoundToInt(cell.x);
            int pickedY = Mathf.RoundToInt(cell.y);
            if (!Contains(pickedX, pickedY))
                return false;
            x = pickedX;
            y = pickedY;
            return true;
        }

        // How far a camera looking at the centre has to be for the whole board, with the pieces standing
        // on it, to fit on screen. A padding of 1 makes the board touch the edge of the screen.
        public float FitDistance(float verticalFovDegrees, float aspect, float yawDegrees, float pitchDegrees, float padding = 1.15f)
        {
            float tanY = Mathf.Tan(Mathf.Clamp(verticalFovDegrees, 5f, 170f) * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * Mathf.Max(0.1f, aspect);
            CameraMath.Basis(yawDegrees, Mathf.Clamp(pitchDegrees, 1f, 90f), out Vector3 right, out Vector3 up, out Vector3 forward);

            float top = CellSize * 0.5f;
            float distance = CellSize;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? -SlabHalfWidth : SlabHalfWidth,
                    (i & 2) == 0 ? -Thickness : top,
                    (i & 4) == 0 ? -SlabHalfDepth : SlabHalfDepth);
                float depth = Vector3.Dot(corner, forward);
                distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(corner, right)) * padding / tanX - depth);
                distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(corner, up)) * padding / tanY - depth);
            }
            return distance;
        }
    }
}
