using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Game;

namespace BoardSandbox.Client.Views
{
    // What a grid view needs to know about the current game: the size of the board, who stands where,
    // the last move and the winning line.
    public sealed class BoardState
    {
        public const int DefaultSize = 8;

        private const double CoordinateLimit = 1000000.0;

        private static readonly Point2[] NoCells = new Point2[0];

        private readonly Dictionary<long, ViewObject> _cells = new Dictionary<long, ViewObject>();

        private BoardState()
        {
        }

        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public bool HasLastMove { get; private set; }
        public int LastX { get; private set; }
        public int LastY { get; private set; }
        public IReadOnlyList<Point2> WinningCells { get; private set; } = NoCells;

        public ViewObject ObjectAt(int x, int y)
        {
            _cells.TryGetValue(Key(x, y), out ViewObject piece);
            return piece;
        }

        public bool IsOccupied(int x, int y)
        {
            return _cells.ContainsKey(Key(x, y));
        }

        // The board announces its size with a "board" event, otherwise the stage decides, otherwise
        // it is as large as the pieces on it require.
        public static BoardState Read(ViewModel view, StageSettings stage)
        {
            BoardState state = new BoardState();
            int widest = 0;
            int deepest = 0;
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Props["pos"].IsArray)
                    continue;
                int x = Cell(piece.X);
                int y = Cell(piece.Y);
                long key = Key(x, y);
                if (!state._cells.TryGetValue(key, out ViewObject other) || other.Id < piece.Id)
                    state._cells[key] = piece;
                widest = Math.Max(widest, (int)Math.Ceiling(Limit(piece.X)));
                deepest = Math.Max(deepest, (int)Math.Ceiling(Limit(piece.Y)));
            }

            int columns = 0;
            int rows = 0;
            ViewEvent board = view.GetCustom("board");
            if (board != null)
            {
                columns = board.Raw["columns"].AsInt(0);
                rows = board.Raw["rows"].AsInt(0);
            }
            if (columns <= 0)
                columns = stage.Board.Columns > 0 ? stage.Board.Columns : Math.Max(DefaultSize, widest);
            if (rows <= 0)
                rows = stage.Board.Rows > 0 ? stage.Board.Rows : Math.Max(DefaultSize, deepest);
            state.Columns = Math.Min(columns, BoardLayout.MaxSize);
            state.Rows = Math.Min(rows, BoardLayout.MaxSize);

            ViewEvent last = view.GetCustom("last_move");
            if (last != null)
            {
                state.LastX = last.Raw["x"].AsInt(0);
                state.LastY = last.Raw["y"].AsInt(0);
                state.HasLastMove = state.LastX > 0 && state.LastY > 0;
            }

            if (view.Over != null)
                state.WinningCells = view.Over.Cells;
            return state;
        }

        private static double Limit(double value)
        {
            return Math.Max(-CoordinateLimit, Math.Min(CoordinateLimit, value));
        }

        public static int Cell(double coordinate)
        {
            return (int)Math.Round(Limit(coordinate), MidpointRounding.AwayFromZero);
        }

        private static long Key(int x, int y)
        {
            return unchecked(((long)x << 32) | (uint)y);
        }
    }
}
