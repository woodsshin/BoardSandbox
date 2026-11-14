using System;

namespace BoardSandbox.Core.Ai
{
    // How much a stone on a point threatens, all four directions taken together.
    public enum GomokuThreat
    {
        None = 0,

        // A four: the opponent has one answer.
        Four = 1,

        // Two open threes: it becomes an open four unless the opponent gets ahead with fours.
        DoubleThree = 2,

        // An open four, two fours, or a four with an open three: it wins unless the opponent wins first.
        Winning = 3,

        // The line is complete.
        Win = 4
    }

    // A position of an m,n,k game on a square board (gomoku and its variants) for the search: the stones, who is to
    // move, the Zobrist hash, the empty points near stones, which are the only moves the search looks at, and for
    // every empty point the shape either seat would make there in each direction. The shapes are kept up to date
    // stone by stone, so asking for them costs nothing. Points are indices y * Size + x counted from 0.
    public sealed class GomokuPosition
    {
        public const int MinSize = 3;
        public const int MaxSize = 25;

        // How far from a stone a move may be to be considered.
        private const int Near = 2;

        private static readonly int[] StepX = { 1, 0, 1, 1 };
        private static readonly int[] StepY = { 0, 1, 1, -1 };

        // Worth of a shape in one direction when ordering moves.
        private static readonly int[] ShapeValues = { 0, 4, 12, 60, 70, 3000, 100000 };
        private const int WinningBonus = 3000;
        private const int DoubleThreeBonus = 600;

        private readonly byte[] _cells;
        private readonly byte[] _shapes;
        private readonly int[] _candidates;
        private readonly int[] _slots;
        private readonly ZobristKeys _keys;
        private readonly GomokuShapes _table;
        private readonly int _reach;
        private ulong _stones;

        public GomokuPosition(int size, int winLength, bool exactWin)
        {
            if (size < MinSize || size > MaxSize)
                throw new ArgumentOutOfRangeException(nameof(size));
            if (winLength < 2 || winLength > size)
                throw new ArgumentOutOfRangeException(nameof(winLength));
            Size = size;
            WinLength = winLength;
            ExactWin = exactWin;
            int count = size * size;
            _cells = new byte[count];
            _shapes = new byte[count * 8];
            _candidates = new int[count];
            _slots = new int[count];
            for (int i = 0; i < count; i++)
                _slots[i] = -1;
            _keys = ZobristKeys.For(count);
            _table = GomokuShapes.For(winLength, exactWin);
            _reach = _table.Reach;
            Current = 1;
            // On a board smaller than the reach of a line the edges alone can make a shape.
            for (int index = 0; index < count; index++)
            {
                for (int direction = 0; direction < 4; direction++)
                    Reshape(index, direction);
            }
        }

        public int Size { get; }
        public int WinLength { get; }

        // Only a line of exactly WinLength wins; a longer one does not.
        public bool ExactWin { get; }

        public int CellCount => _cells.Length;

        // The seat to move, 1 or 2.
        public int Current { get; private set; }

        // Stones on the board.
        public int Moves { get; private set; }

        // The seat that completed a line with Play, 0 for none.
        public int Winner { get; private set; }

        public bool IsFull => Moves == _cells.Length;
        public ulong Hash => Current == 2 ? _stones ^ _keys.SideToMove : _stones;
        public int CandidateCount { get; private set; }

        // A board as the gomoku mod reports it: one character per point, row by row, '0' empty, '1' and '2' the seats.
        public static GomokuPosition Parse(int size, int winLength, bool exactWin, string board, int current)
        {
            GomokuPosition position = new GomokuPosition(size, winLength, exactWin);
            if (board == null || board.Length != position.CellCount)
                throw new FormatException("the board does not match the board size");
            if (current != 1 && current != 2)
                throw new FormatException("the seat to move must be 1 or 2");
            for (int index = 0; index < board.Length; index++)
            {
                int seat = board[index] - '0';
                if (seat < 0 || seat > 2)
                    throw new FormatException("the board contains an invalid point");
                if (seat != 0)
                    position.Place(index, seat);
            }
            position.Current = current;
            return position;
        }

        public void CopyFrom(GomokuPosition other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            if (other.Size != Size || other.WinLength != WinLength || other.ExactWin != ExactWin)
                throw new ArgumentException("the positions are of different games", nameof(other));
            Buffer.BlockCopy(other._cells, 0, _cells, 0, _cells.Length);
            Buffer.BlockCopy(other._shapes, 0, _shapes, 0, _shapes.Length);
            Array.Copy(other._candidates, _candidates, other.CandidateCount);
            Array.Copy(other._slots, _slots, _slots.Length);
            CandidateCount = other.CandidateCount;
            _stones = other._stones;
            Current = other.Current;
            Moves = other.Moves;
            Winner = other.Winner;
        }

        public int Cell(int index)
        {
            return _cells[index];
        }

        // An empty point within reach of a stone.
        public int Candidate(int slot)
        {
            return _candidates[slot];
        }

        // Gives the move to the other seat without a stone, to ask what that seat could do here.
        public void Pass()
        {
            Current = 3 - Current;
        }

        // Places a stone of the seat to move and passes the turn. True when the stone completes a line.
        public bool Play(int index)
        {
            if (_cells[index] != 0)
                throw new InvalidOperationException("the point is taken");
            int seat = Current;
            bool won = Wins(index, seat);
            Place(index, seat);
            Current = 3 - seat;
            if (won)
                Winner = seat;
            return won;
        }

        // Whether a stone of the seat on this empty point would complete a line.
        public bool Wins(int index, int seat)
        {
            int at = (index * 2 + seat - 1) * 4;
            return _shapes[at] == (byte)GomokuShape.Five || _shapes[at + 1] == (byte)GomokuShape.Five
                || _shapes[at + 2] == (byte)GomokuShape.Five || _shapes[at + 3] == (byte)GomokuShape.Five;
        }

        // The shape a stone of the seat on this empty point would make in one of the four directions
        // (across, down, and the two diagonals).
        public GomokuShape Shape(int index, int seat, int direction)
        {
            return (GomokuShape)_shapes[(index * 2 + seat - 1) * 4 + direction];
        }

        // What a stone of the seat on this empty point would threaten, and a number to order moves by.
        public GomokuThreat Rate(int index, int seat, out int value)
        {
            int at = (index * 2 + seat - 1) * 4;
            int fours = 0;
            int openThrees = 0;
            bool openFour = false;
            value = 0;
            for (int direction = 0; direction < 4; direction++)
            {
                int shape = _shapes[at + direction];
                if (shape == 0)
                    continue;
                value += ShapeValues[shape];
                switch ((GomokuShape)shape)
                {
                    case GomokuShape.Five:
                        return GomokuThreat.Win;
                    case GomokuShape.OpenFour:
                        openFour = true;
                        break;
                    case GomokuShape.Four:
                        fours++;
                        break;
                    case GomokuShape.OpenThree:
                        openThrees++;
                        break;
                }
            }
            if (openFour || fours >= 2 || (fours == 1 && openThrees >= 1))
            {
                if (!openFour)
                    value += WinningBonus;
                return GomokuThreat.Winning;
            }
            if (openThrees >= 2)
            {
                value += DoubleThreeBonus;
                return GomokuThreat.DoubleThree;
            }
            return fours == 1 ? GomokuThreat.Four : GomokuThreat.None;
        }

        private void Place(int index, int seat)
        {
            _cells[index] = (byte)seat;
            _stones ^= _keys.Stone(index, seat);
            Moves++;

            int slot = _slots[index];
            if (slot >= 0)
            {
                int moved = _candidates[--CandidateCount];
                _candidates[slot] = moved;
                _slots[moved] = slot;
                _slots[index] = -1;
            }

            int x = index % Size;
            int y = index / Size;
            int left = Math.Max(0, x - Near);
            int right = Math.Min(Size - 1, x + Near);
            int top = Math.Max(0, y - Near);
            int bottom = Math.Min(Size - 1, y + Near);
            for (int cy = top; cy <= bottom; cy++)
            {
                for (int cx = left; cx <= right; cx++)
                {
                    int near = cy * Size + cx;
                    if (_cells[near] == 0 && _slots[near] < 0)
                    {
                        _slots[near] = CandidateCount;
                        _candidates[CandidateCount++] = near;
                    }
                }
            }

            // The stone changes the shapes of the empty points on its four lines, each in that line's direction only.
            for (int direction = 0; direction < 4; direction++)
            {
                int dx = StepX[direction];
                int dy = StepY[direction];
                for (int step = -_reach; step <= _reach; step++)
                {
                    if (step == 0)
                        continue;
                    int cx = x + dx * step;
                    int cy = y + dy * step;
                    if (cx < 0 || cx >= Size || cy < 0 || cy >= Size)
                        continue;
                    int point = cy * Size + cx;
                    if (_cells[point] == 0)
                        Reshape(point, direction);
                }
            }
        }

        private void Reshape(int index, int direction)
        {
            int x = index % Size;
            int y = index / Size;
            int dx = StepX[direction];
            int dy = StepY[direction];
            int at = index * 8 + direction;

            if (_reach > 16)
            {
                // Too long a line to pack into a number; such games are rare enough to work the shape out each time.
                for (int seat = 1; seat <= 2; seat++)
                {
                    int[] line = new int[2 * _reach + 1];
                    for (int step = -_reach; step <= _reach; step++)
                        line[step + _reach] = step == 0 ? 1 : Code(x + dx * step, y + dy * step, seat);
                    _shapes[at + (seat - 1) * 4] = (byte)_table.Classify(line);
                }
                return;
            }

            ulong first = 0;
            ulong second = 0;
            int shift = 0;
            for (int step = -_reach; step <= _reach; step++)
            {
                if (step == 0)
                    continue;
                int cx = x + dx * step;
                int cy = y + dy * step;
                int stone = cx < 0 || cx >= Size || cy < 0 || cy >= Size ? 3 : _cells[cy * Size + cx];
                if (stone != 0)
                {
                    first |= (ulong)(stone == 1 ? 1 : 2) << shift;
                    second |= (ulong)(stone == 2 ? 1 : 2) << shift;
                }
                shift += 2;
            }
            _shapes[at] = (byte)_table.Lookup(first);
            _shapes[at + 4] = (byte)_table.Lookup(second);
        }

        private int Code(int x, int y, int seat)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size)
                return 2;
            int stone = _cells[y * Size + x];
            return stone == 0 ? 0 : stone == seat ? 1 : 2;
        }
    }
}
