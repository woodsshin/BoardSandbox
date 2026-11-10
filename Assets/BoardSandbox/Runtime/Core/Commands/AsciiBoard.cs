using System;
using System.Text;
using BoardSandbox.Core.Game;

namespace BoardSandbox.Core.Commands
{
    // Draws a grid board from the objects on the table; the geometry comes from a "board" event when the mod sends one.
    public static class AsciiBoard
    {
        private const int MaxSide = 60;

        public static string Render(ViewModel view)
        {
            int columns = 0;
            int rows = 0;
            ViewEvent board = view.GetCustom("board");
            if (board != null)
            {
                columns = board.Raw["columns"].AsInt();
                rows = board.Raw["rows"].AsInt();
            }
            else
            {
                foreach (ViewObject o in view.Objects)
                {
                    columns = Math.Max(columns, (int)Math.Ceiling(o.X));
                    rows = Math.Max(rows, (int)Math.Ceiling(o.Y));
                }
            }
            if (columns <= 0 || rows <= 0 || columns > MaxSide || rows > MaxSide)
                return "(no grid to draw)";

            char[,] cells = new char[rows, columns];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                    cells[y, x] = '.';
            }
            foreach (ViewObject o in view.Objects)
            {
                int x = (int)Math.Round(o.X) - 1;
                int y = (int)Math.Round(o.Y) - 1;
                if (x >= 0 && x < columns && y >= 0 && y < rows)
                    cells[y, x] = SymbolFor(o.Owner);
            }

            int lastX = -1;
            int lastY = -1;
            ViewEvent last = view.GetCustom("last_move");
            if (last != null)
            {
                lastX = last.Raw["x"].AsInt() - 1;
                lastY = last.Raw["y"].AsInt() - 1;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("    ");
            for (int x = 1; x <= columns; x++)
                sb.Append(x.ToString().PadLeft(2)).Append(' ');
            sb.Append('\n');
            for (int y = 0; y < rows; y++)
            {
                sb.Append((y + 1).ToString().PadLeft(3)).Append(' ');
                for (int x = 0; x < columns; x++)
                {
                    bool marked = x == lastX && y == lastY;
                    sb.Append(marked ? '[' : ' ').Append(cells[y, x]).Append(marked ? ']' : ' ');
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static char SymbolFor(int owner)
        {
            switch (owner)
            {
                case 1:
                    return 'X';
                case 2:
                    return 'O';
                case 3:
                    return 'A';
                case 4:
                    return 'B';
                default:
                    return '#';
            }
        }
    }
}
