using System;

namespace BoardSandbox.Core.Ai
{
    // A position of an m,n,k game on a square board (gomoku and its variants) for the search: the stones, who is to
    // move, the Zobrist hash, and the empty points near stones, which are the only moves the search looks at.
    // Points are indices y * Size + x counted from 0.
    public sealed class GomokuPosition
    {
        public const int MinSize = 3;
        public const int MaxSize = 25;

        // How far from a stone a move may be to be considered.
        private const int Reach = 2;

        private static readonly int[] StepX = { 1, 0, 1, 1 };
        private static readonly int[] StepY = { 0, 1, 1, -1 };

        private readonly byte[] _cells;
        private readonly int[] _candidates;
        private readonly int[] _slots;
        private readonly ZobristKeys _keys;
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
            _candidates = new int[count];
            _slots = new int[count];
            for (int i = 0; i < count; i++)
                _slots[i] = -1;
            _keys = ZobristKeys.For(count);
            Current = 1;
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
            for (int direction = 0; direction < 4; direction++)
            {
                if (WinsAlong(index, seat, direction))
                    return true;
            }
            return false;
        }

        // The stones of the seat in an unbroken line through the point in one of the four directions, not counting
        // the point itself.
        public int Line(int index, int seat, int direction)
        {
            int x = index % Size;
            int y = index / Size;
            return Run(x, y, StepX[direction], StepY[direction], seat) + Run(x, y, -StepX[direction], -StepY[direction], seat);
        }

        // An empty point where the seat completes a line that runs through the given stone of that seat; -1 for none.
        // Such a point is the first empty one past the stones next to it, so only eight points need a look.
        public int WinningPointThrough(int index, int seat)
        {
            if (index < 0)
                return -1;
            int x = index % Size;
            int y = index / Size;
            for (int direction = 0; direction < 4; direction++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int dx = StepX[direction] * sign;
                    int dy = StepY[direction] * sign;
                    int cx = x + dx;
                    int cy = y + dy;
                    while (cx >= 0 && cx < Size && cy >= 0 && cy < Size && _cells[cy * Size + cx] == seat)
                    {
                        cx += dx;
                        cy += dy;
                    }
                    if (cx < 0 || cx >= Size || cy < 0 || cy >= Size)
                        continue;
                    int point = cy * Size + cx;
                    if (_cells[point] == 0 && WinsAlong(point, seat, direction))
                        return point;
                }
            }
            return -1;
        }

        // An empty point where the seat, by lengthening the line through the given stone of that seat, gets a line
        // one short of winning with both ends free, which wins the move after next; -1 for none. Only unbroken lines
        // are seen.
        public int OpenLinePointThrough(int index, int seat)
        {
            if (index < 0 || WinLength < 4)
                return -1;
            int x = index % Size;
            int y = index / Size;
            for (int direction = 0; direction < 4; direction++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int dx = StepX[direction] * sign;
                    int dy = StepY[direction] * sign;
                    int cx = x + dx;
                    int cy = y + dy;
                    while (Holds(cx, cy, seat))
                    {
                        cx += dx;
                        cy += dy;
                    }
                    if (!Holds(cx, cy, 0))
                        continue;
                    int ahead = Run(cx, cy, dx, dy, seat);
                    int behind = Run(cx, cy, -dx, -dy, seat);
                    if (1 + ahead + behind != WinLength - 1)
                        continue;
                    if (Holds(cx + dx * (ahead + 1), cy + dy * (ahead + 1), 0) && Holds(cx - dx * (behind + 1), cy - dy * (behind + 1), 0))
                        return cy * Size + cx;
                }
            }
            return -1;
        }

        private bool Holds(int x, int y, int value)
        {
            return x >= 0 && x < Size && y >= 0 && y < Size && _cells[y * Size + x] == value;
        }

        private bool WinsAlong(int index, int seat, int direction)
        {
            int total = 1 + Line(index, seat, direction);
            return total == WinLength || (total > WinLength && !ExactWin);
        }

        private int Run(int x, int y, int dx, int dy, int seat)
        {
            int count = 0;
            int cx = x + dx;
            int cy = y + dy;
            while (cx >= 0 && cx < Size && cy >= 0 && cy < Size && _cells[cy * Size + cx] == seat)
            {
                count++;
                cx += dx;
                cy += dy;
            }
            return count;
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
            int left = Math.Max(0, x - Reach);
            int right = Math.Min(Size - 1, x + Reach);
            int top = Math.Max(0, y - Reach);
            int bottom = Math.Min(Size - 1, y + Reach);
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
        }
    }
}
