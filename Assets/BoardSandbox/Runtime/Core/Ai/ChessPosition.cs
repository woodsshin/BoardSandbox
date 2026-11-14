using System;

namespace BoardSandbox.Core.Ai
{
    // A chess position for the search: the board, whose move it is, the castling rights, the en passant square, the
    // clock of the fifty-move rule and the Zobrist hash, with move generation, making and taking back of moves and a
    // static evaluation.
    //
    // Squares are numbered rank * 8 + file with a1 = 0 and h8 = 63. Pieces are 0 for none, 1 to 6 for the white pawn,
    // knight, bishop, rook, queen and king and 7 to 12 for the black ones. Colours are 0 for white and 1 for black.
    // A move is one number: from, to << 6, the promotion piece type << 12 and its kind << 15.
    public sealed class ChessPosition
    {
        public const int Pawn = 1;
        public const int Knight = 2;
        public const int Bishop = 3;
        public const int Rook = 4;
        public const int Queen = 5;
        public const int King = 6;

        public const int MaxMoves = 256;

        // The value of checkmate in the units of Evaluate (hundredths of a pawn).
        public const int Mate = 30000;

        public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        private const int KindNormal = 0;
        private const int KindEnPassant = 1;
        private const int KindCastle = 2;

        private const int WhiteKingSide = 1;
        private const int WhiteQueenSide = 2;
        private const int BlackKingSide = 4;
        private const int BlackQueenSide = 8;

        private const int MaxHistory = 1024;
        private const string PieceLetters = ".PNBRQKpnbrqk";

        private static readonly int[] KnightFile = { 1, 2, 2, 1, -1, -2, -2, -1 };
        private static readonly int[] KnightRank = { 2, 1, -1, -2, -2, -1, 1, 2 };
        private static readonly int[] RayFile = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] RayRank = { 0, 0, 1, -1, 1, -1, 1, -1 };

        // Castling rights that remain when a move touches a square.
        private static readonly int[] RightsKept = BuildRightsKept();

        private static readonly int[] PieceValues = { 0, 100, 320, 330, 500, 900, 0 };

        // Piece-square tables from white's side, the eighth rank first (the "simplified evaluation function" of
        // Tomasz Michniewski).
        private static readonly int[][] Tables =
        {
            null,
            new[]
            {
                0, 0, 0, 0, 0, 0, 0, 0,
                50, 50, 50, 50, 50, 50, 50, 50,
                10, 10, 20, 30, 30, 20, 10, 10,
                5, 5, 10, 25, 25, 10, 5, 5,
                0, 0, 0, 20, 20, 0, 0, 0,
                5, -5, -10, 0, 0, -10, -5, 5,
                5, 10, 10, -20, -20, 10, 10, 5,
                0, 0, 0, 0, 0, 0, 0, 0
            },
            new[]
            {
                -50, -40, -30, -30, -30, -30, -40, -50,
                -40, -20, 0, 0, 0, 0, -20, -40,
                -30, 0, 10, 15, 15, 10, 0, -30,
                -30, 5, 15, 20, 20, 15, 5, -30,
                -30, 0, 15, 20, 20, 15, 0, -30,
                -30, 5, 10, 15, 15, 10, 5, -30,
                -40, -20, 0, 5, 5, 0, -20, -40,
                -50, -40, -30, -30, -30, -30, -40, -50
            },
            new[]
            {
                -20, -10, -10, -10, -10, -10, -10, -20,
                -10, 0, 0, 0, 0, 0, 0, -10,
                -10, 0, 5, 10, 10, 5, 0, -10,
                -10, 5, 5, 10, 10, 5, 5, -10,
                -10, 0, 10, 10, 10, 10, 0, -10,
                -10, 10, 10, 10, 10, 10, 10, -10,
                -10, 5, 0, 0, 0, 0, 5, -10,
                -20, -10, -10, -10, -10, -10, -10, -20
            },
            new[]
            {
                0, 0, 0, 0, 0, 0, 0, 0,
                5, 10, 10, 10, 10, 10, 10, 5,
                -5, 0, 0, 0, 0, 0, 0, -5,
                -5, 0, 0, 0, 0, 0, 0, -5,
                -5, 0, 0, 0, 0, 0, 0, -5,
                -5, 0, 0, 0, 0, 0, 0, -5,
                -5, 0, 0, 0, 0, 0, 0, -5,
                0, 0, 0, 5, 5, 0, 0, 0
            },
            new[]
            {
                -20, -10, -10, -5, -5, -10, -10, -20,
                -10, 0, 0, 0, 0, 0, 0, -10,
                -10, 0, 5, 5, 5, 5, 0, -10,
                -5, 0, 5, 5, 5, 5, 0, -5,
                0, 0, 5, 5, 5, 5, 0, -5,
                -10, 5, 5, 5, 5, 5, 0, -10,
                -10, 0, 5, 0, 0, 0, 0, -10,
                -20, -10, -10, -5, -5, -10, -10, -20
            },
            new[]
            {
                -30, -40, -40, -50, -50, -40, -40, -30,
                -30, -40, -40, -50, -50, -40, -40, -30,
                -30, -40, -40, -50, -50, -40, -40, -30,
                -30, -40, -40, -50, -50, -40, -40, -30,
                -20, -30, -30, -40, -40, -30, -30, -20,
                -10, -20, -20, -20, -20, -20, -20, -10,
                20, 20, 0, 0, 0, 0, 20, 20,
                20, 30, 10, 0, 0, 10, 30, 20
            }
        };

        // The king once the heavy pieces are gone.
        private static readonly int[] KingEndTable =
        {
            -50, -40, -30, -20, -20, -30, -40, -50,
            -30, -20, -10, 0, 0, -10, -20, -30,
            -30, -10, 20, 30, 30, 20, -10, -30,
            -30, -10, 30, 40, 40, 30, -10, -30,
            -30, -10, 30, 40, 40, 30, -10, -30,
            -30, -10, 20, 30, 30, 20, -10, -30,
            -30, -30, 0, 0, 0, 0, -30, -30,
            -50, -30, -30, -30, -30, -30, -30, -50
        };

        // 64 * 6 piece squares, then 16 castling states, then 8 en passant files.
        private static readonly ZobristKeys Keys = ZobristKeys.For(64 * 6 + 24);

        private readonly int[] _board = new int[64];
        private readonly int[] _kings = new int[2];
        private readonly int[] _undoMove = new int[MaxHistory];
        private readonly int[] _undoCaptured = new int[MaxHistory];
        private readonly int[] _undoState = new int[MaxHistory];
        private readonly ulong[] _undoHash = new ulong[MaxHistory];
        private int _depth;
        private int _castling;
        private int _enPassant = -1;

        public ChessPosition()
            : this(StartFen)
        {
        }

        public ChessPosition(string fen)
        {
            Load(fen);
        }

        // 0 when white is to move, 1 when black is.
        public int Side { get; private set; }

        // Plies since a capture or a pawn move.
        public int HalfMoves { get; private set; }

        public ulong Hash { get; private set; }

        // Moves made on this object and not taken back.
        public int Depth => _depth;

        public int Piece(int square)
        {
            return _board[square];
        }

        public static int From(int move)
        {
            return move & 63;
        }

        public static int To(int move)
        {
            return (move >> 6) & 63;
        }

        // The piece type a pawn turns into, 0 when the move does not promote.
        public static int Promotion(int move)
        {
            return (move >> 12) & 7;
        }

        // "e2e4", "e7e8q"
        public static string Uci(int move)
        {
            string text = SquareName(From(move)) + SquareName(To(move));
            int promotion = Promotion(move);
            return promotion == 0 ? text : text + char.ToLowerInvariant(PieceLetters[promotion]);
        }

        public static string SquareName(int square)
        {
            return new string(new[] { (char)('a' + (square & 7)), (char)('1' + (square >> 3)) });
        }

        // Reads a position in Forsyth-Edwards notation. Castling rights without the king and rook on their squares
        // are dropped. Throws FormatException for text that is not a position.
        public void Load(string fen)
        {
            if (fen == null)
                throw new FormatException("the FEN is empty");
            string[] fields = fen.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 1)
                throw new FormatException("the FEN is empty");
            string[] ranks = fields[0].Split('/');
            if (ranks.Length != 8)
                throw new FormatException("the piece placement needs 8 ranks");

            Array.Clear(_board, 0, 64);
            int whiteKings = 0;
            int blackKings = 0;
            for (int row = 0; row < 8; row++)
            {
                int rank = 7 - row;
                int file = 0;
                foreach (char c in ranks[row])
                {
                    if (c >= '1' && c <= '8')
                    {
                        file += c - '0';
                        continue;
                    }
                    int piece = PieceLetters.IndexOf(c);
                    if (piece <= 0 || file > 7)
                        throw new FormatException("the piece placement is not valid");
                    int square = rank * 8 + file;
                    _board[square] = piece;
                    if (piece == King)
                    {
                        whiteKings++;
                        _kings[0] = square;
                    }
                    else if (piece == King + 6)
                    {
                        blackKings++;
                        _kings[1] = square;
                    }
                    file++;
                }
                if (file != 8)
                    throw new FormatException("a rank does not have 8 squares");
            }
            if (whiteKings != 1 || blackKings != 1)
                throw new FormatException("each side needs exactly one king");

            string side = fields.Length > 1 ? fields[1] : "w";
            if (side != "w" && side != "b")
                throw new FormatException("the side to move must be w or b");
            Side = side == "w" ? 0 : 1;

            _castling = 0;
            if (fields.Length > 2)
            {
                foreach (char c in fields[2])
                {
                    if (c == 'K' && _board[4] == King && _board[7] == Rook)
                        _castling |= WhiteKingSide;
                    else if (c == 'Q' && _board[4] == King && _board[0] == Rook)
                        _castling |= WhiteQueenSide;
                    else if (c == 'k' && _board[60] == King + 6 && _board[63] == Rook + 6)
                        _castling |= BlackKingSide;
                    else if (c == 'q' && _board[60] == King + 6 && _board[56] == Rook + 6)
                        _castling |= BlackQueenSide;
                }
            }

            _enPassant = -1;
            if (fields.Length > 3 && fields[3].Length == 2)
            {
                int file = fields[3][0] - 'a';
                int rank = fields[3][1] - '1';
                if (file >= 0 && file < 8 && (rank == 2 || rank == 5))
                    _enPassant = rank * 8 + file;
            }

            HalfMoves = 0;
            if (fields.Length > 4 && int.TryParse(fields[4], out int clock) && clock >= 0)
                HalfMoves = clock;

            _depth = 0;
            Hash = ComputeHash();
            if (IsAttacked(_kings[1 - Side], Side))
                throw new FormatException("the side that is not to move is in check");
        }

        public void CopyFrom(ChessPosition other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            Array.Copy(other._board, _board, 64);
            _kings[0] = other._kings[0];
            _kings[1] = other._kings[1];
            _castling = other._castling;
            _enPassant = other._enPassant;
            Side = other.Side;
            HalfMoves = other.HalfMoves;
            Hash = other.Hash;
            _depth = 0;
        }

        public bool InCheck()
        {
            return IsAttacked(_kings[Side], 1 - Side);
        }

        // Neither side can ever give mate: bare kings, a single knight or bishop, or bishops on one colour only.
        public bool InsufficientMaterial()
        {
            int knights = 0;
            int lightBishops = 0;
            int darkBishops = 0;
            for (int square = 0; square < 64; square++)
            {
                int piece = _board[square];
                if (piece == 0)
                    continue;
                int type = piece > 6 ? piece - 6 : piece;
                if (type == Pawn || type == Rook || type == Queen)
                    return false;
                if (type == Knight)
                    knights++;
                else if (type == Bishop)
                {
                    if ((((square >> 3) + (square & 7)) & 1) == 0)
                        darkBishops++;
                    else
                        lightBishops++;
                }
            }
            if (knights + lightBishops + darkBishops <= 1)
                return true;
            return knights == 0 && (lightBishops == 0 || darkBishops == 0);
        }

        // Whether a piece of the colour attacks the square.
        public bool IsAttacked(int square, int by)
        {
            int file = square & 7;
            int rank = square >> 3;
            int shift = by * 6;

            // A white pawn attacks upwards, so it stands a rank below.
            int pawnRank = by == 0 ? rank - 1 : rank + 1;
            if (pawnRank >= 0 && pawnRank < 8)
            {
                if (file > 0 && _board[pawnRank * 8 + file - 1] == Pawn + shift)
                    return true;
                if (file < 7 && _board[pawnRank * 8 + file + 1] == Pawn + shift)
                    return true;
            }
            for (int i = 0; i < 8; i++)
            {
                int f = file + KnightFile[i];
                int r = rank + KnightRank[i];
                if (f >= 0 && f < 8 && r >= 0 && r < 8 && _board[r * 8 + f] == Knight + shift)
                    return true;
            }
            for (int i = 0; i < 8; i++)
            {
                int f = file + RayFile[i];
                int r = rank + RayRank[i];
                bool first = true;
                while (f >= 0 && f < 8 && r >= 0 && r < 8)
                {
                    int piece = _board[r * 8 + f];
                    if (piece != 0)
                    {
                        int type = piece - shift;
                        if (type == Queen || (first && type == King) || type == (i < 4 ? Rook : Bishop))
                            return true;
                        break;
                    }
                    first = false;
                    f += RayFile[i];
                    r += RayRank[i];
                }
            }
            return false;
        }

        // Writes the legal moves into the buffer (MaxMoves long) and gives their number.
        public int LegalMoves(int[] moves)
        {
            int count = PseudoMoves(moves, false);
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                if (TryMake(moves[i]))
                {
                    Unmake();
                    moves[kept++] = moves[i];
                }
            }
            return kept;
        }

        // The moves that obey how the pieces move but may leave the own king in check; TryMake refuses those.
        // With capturesOnly, captures and promotions to a queen alone.
        public int PseudoMoves(int[] moves, bool capturesOnly)
        {
            int count = 0;
            int us = Side;
            int shift = us * 6;
            for (int from = 0; from < 64; from++)
            {
                int piece = _board[from];
                if (piece == 0 || (piece > 6) != (us == 1))
                    continue;
                int type = piece - shift;
                int file = from & 7;
                int rank = from >> 3;
                switch (type)
                {
                    case Pawn:
                    {
                        int step = us == 0 ? 8 : -8;
                        int to = from + step;
                        bool promotes = us == 0 ? rank == 6 : rank == 1;
                        if (_board[to] == 0)
                        {
                            if (promotes)
                            {
                                moves[count++] = from | (to << 6) | (Queen << 12);
                                if (!capturesOnly)
                                {
                                    moves[count++] = from | (to << 6) | (Rook << 12);
                                    moves[count++] = from | (to << 6) | (Bishop << 12);
                                    moves[count++] = from | (to << 6) | (Knight << 12);
                                }
                            }
                            else if (!capturesOnly)
                            {
                                moves[count++] = from | (to << 6);
                                if ((us == 0 ? rank == 1 : rank == 6) && _board[to + step] == 0)
                                    moves[count++] = from | ((to + step) << 6);
                            }
                        }
                        for (int side = -1; side <= 1; side += 2)
                        {
                            int f = file + side;
                            if (f < 0 || f > 7)
                                continue;
                            int target = to + side;
                            int victim = _board[target];
                            if (victim != 0 && (victim > 6) != (us == 1))
                            {
                                if (promotes)
                                {
                                    moves[count++] = from | (target << 6) | (Queen << 12);
                                    if (!capturesOnly)
                                    {
                                        moves[count++] = from | (target << 6) | (Rook << 12);
                                        moves[count++] = from | (target << 6) | (Bishop << 12);
                                        moves[count++] = from | (target << 6) | (Knight << 12);
                                    }
                                }
                                else
                                {
                                    moves[count++] = from | (target << 6);
                                }
                            }
                            else if (target == _enPassant)
                            {
                                moves[count++] = from | (target << 6) | (KindEnPassant << 15);
                            }
                        }
                        break;
                    }
                    case Knight:
                        for (int i = 0; i < 8; i++)
                        {
                            int f = file + KnightFile[i];
                            int r = rank + KnightRank[i];
                            if (f < 0 || f > 7 || r < 0 || r > 7)
                                continue;
                            int to = r * 8 + f;
                            int victim = _board[to];
                            if (victim == 0 ? !capturesOnly : (victim > 6) != (us == 1))
                                moves[count++] = from | (to << 6);
                        }
                        break;
                    case King:
                        for (int i = 0; i < 8; i++)
                        {
                            int f = file + RayFile[i];
                            int r = rank + RayRank[i];
                            if (f < 0 || f > 7 || r < 0 || r > 7)
                                continue;
                            int to = r * 8 + f;
                            int victim = _board[to];
                            if (victim == 0 ? !capturesOnly : (victim > 6) != (us == 1))
                                moves[count++] = from | (to << 6);
                        }
                        if (!capturesOnly)
                            count = AddCastling(moves, count, us);
                        break;
                    default:
                    {
                        int first = type == Bishop ? 4 : 0;
                        int last = type == Rook ? 4 : 8;
                        for (int i = first; i < last; i++)
                        {
                            int f = file + RayFile[i];
                            int r = rank + RayRank[i];
                            while (f >= 0 && f < 8 && r >= 0 && r < 8)
                            {
                                int to = r * 8 + f;
                                int victim = _board[to];
                                if (victim == 0)
                                {
                                    if (!capturesOnly)
                                        moves[count++] = from | (to << 6);
                                }
                                else
                                {
                                    if ((victim > 6) != (us == 1))
                                        moves[count++] = from | (to << 6);
                                    break;
                                }
                                f += RayFile[i];
                                r += RayRank[i];
                            }
                        }
                        break;
                    }
                }
            }
            return count;
        }

        // Makes the move unless it leaves the mover's king in check.
        public bool TryMake(int move)
        {
            Make(move);
            if (IsAttacked(_kings[1 - Side], Side))
            {
                Unmake();
                return false;
            }
            return true;
        }

        // Makes a move that is known to be legal.
        public void Make(int move)
        {
            if (_depth >= MaxHistory)
                throw new InvalidOperationException("too many moves were made without taking one back");
            int from = move & 63;
            int to = (move >> 6) & 63;
            int promotion = (move >> 12) & 7;
            int kind = (move >> 15) & 3;
            int us = Side;
            int shift = us * 6;
            int piece = _board[from];
            int captured = _board[to];

            _undoMove[_depth] = move;
            _undoCaptured[_depth] = captured;
            _undoState[_depth] = _castling | ((_enPassant + 1) << 4) | (HalfMoves << 11);
            _undoHash[_depth] = Hash;
            _depth++;

            ulong hash = Hash ^ StateKey();
            hash ^= PieceKey(from, piece);
            if (captured != 0)
                hash ^= PieceKey(to, captured);
            _board[from] = 0;

            int placed = promotion != 0 ? promotion + shift : piece;
            _board[to] = placed;
            hash ^= PieceKey(to, placed);

            int type = piece - shift;
            int next = -1;
            if (kind == KindEnPassant)
            {
                int taken = us == 0 ? to - 8 : to + 8;
                hash ^= PieceKey(taken, _board[taken]);
                _board[taken] = 0;
            }
            else if (kind == KindCastle)
            {
                int rookFrom = to > from ? from + 3 : from - 4;
                int rookTo = to > from ? from + 1 : from - 1;
                int rook = _board[rookFrom];
                _board[rookFrom] = 0;
                _board[rookTo] = rook;
                hash ^= PieceKey(rookFrom, rook) ^ PieceKey(rookTo, rook);
            }
            else if (type == Pawn && (to - from == 16 || from - to == 16))
            {
                next = (from + to) / 2;
            }
            if (type == King)
                _kings[us] = to;

            _castling &= RightsKept[from] & RightsKept[to];
            _enPassant = next;
            HalfMoves = type == Pawn || captured != 0 ? 0 : HalfMoves + 1;
            Side = 1 - us;
            Hash = hash ^ StateKey() ^ Keys.SideToMove;
        }

        // Takes the last move back.
        public void Unmake()
        {
            _depth--;
            int move = _undoMove[_depth];
            int from = move & 63;
            int to = (move >> 6) & 63;
            int promotion = (move >> 12) & 7;
            int kind = (move >> 15) & 3;
            int us = 1 - Side;
            int shift = us * 6;

            int piece = promotion != 0 ? Pawn + shift : _board[to];
            _board[from] = piece;
            _board[to] = _undoCaptured[_depth];
            if (kind == KindEnPassant)
            {
                _board[us == 0 ? to - 8 : to + 8] = Pawn + (1 - us) * 6;
            }
            else if (kind == KindCastle)
            {
                int rookFrom = to > from ? from + 3 : from - 4;
                int rookTo = to > from ? from + 1 : from - 1;
                _board[rookFrom] = _board[rookTo];
                _board[rookTo] = 0;
            }
            if (piece - shift == King)
                _kings[us] = from;

            int state = _undoState[_depth];
            _castling = state & 15;
            _enPassant = ((state >> 4) & 127) - 1;
            HalfMoves = state >> 11;
            Hash = _undoHash[_depth];
            Side = us;
        }

        // Whether the move takes a piece.
        public bool IsCapture(int move)
        {
            return _board[(move >> 6) & 63] != 0 || ((move >> 15) & 3) == KindEnPassant;
        }

        // What the move wins at once, for ordering: the piece taken, less a little for a valuable taker, and the
        // gain of a promotion.
        public int Gain(int move)
        {
            int victim = _board[(move >> 6) & 63];
            int attacker = _board[move & 63];
            int gain = 0;
            if (victim != 0)
                gain += PieceValues[victim > 6 ? victim - 6 : victim] * 10 - PieceValues[attacker > 6 ? attacker - 6 : attacker] / 10 + 10000;
            else if (((move >> 15) & 3) == KindEnPassant)
                gain += 11000;
            int promotion = (move >> 12) & 7;
            if (promotion != 0)
                gain += PieceValues[promotion] * 10;
            return gain;
        }

        // Whether a capture can be skipped by a search that stands at standPat and needs alpha: even the piece taken
        // and a margin do not reach alpha, or the taker is worth more than what it takes and can be taken back.
        public bool IsHopeless(int move, int standPat, int alpha)
        {
            int to = (move >> 6) & 63;
            int victim = _board[to];
            if (victim == 0 || ((move >> 12) & 7) != 0)
                return false;
            int attacker = _board[move & 63];
            int gain = PieceValues[victim > 6 ? victim - 6 : victim];
            if (standPat + gain + 200 < alpha)
                return true;
            int cost = PieceValues[attacker > 6 ? attacker - 6 : attacker];
            return cost > gain + 50 && cost != 0 && IsAttacked(to, 1 - Side);
        }

        // The worth of the position for the side to move in hundredths of a pawn: material, where the pieces stand,
        // the pair of bishops, and, against a bare king, how close it is to being driven into a corner.
        public int Evaluate()
        {
            int[] material = { 0, 0 };
            int[] placement = { 0, 0 };
            int[] bishops = { 0, 0 };
            int[] pawns = { 0, 0 };
            int heavy = 0;
            for (int square = 0; square < 64; square++)
            {
                int piece = _board[square];
                if (piece == 0)
                    continue;
                int colour = piece > 6 ? 1 : 0;
                int type = piece - colour * 6;
                if (type == King)
                    continue;
                material[colour] += PieceValues[type];
                placement[colour] += Tables[type][colour == 0 ? (7 - (square >> 3)) * 8 + (square & 7) : square];
                if (type == Bishop)
                    bishops[colour]++;
                else if (type == Pawn)
                    pawns[colour]++;
                if (type != Pawn)
                    heavy += PieceValues[type];
            }

            // The kings come out once few pieces are left.
            bool ending = heavy <= 2600;
            for (int colour = 0; colour < 2; colour++)
            {
                int square = _kings[colour];
                int index = colour == 0 ? (7 - (square >> 3)) * 8 + (square & 7) : square;
                placement[colour] += ending ? KingEndTable[index] : Tables[King][index];
                if (bishops[colour] >= 2)
                    material[colour] += 30;
            }

            int score = material[0] + placement[0] - material[1] - placement[1];
            for (int colour = 0; colour < 2; colour++)
            {
                int other = 1 - colour;
                // Mating a bare king: push it to the edge and bring the own king near.
                if (material[other] == 0 && pawns[other] == 0 && material[colour] >= PieceValues[Rook])
                {
                    int loser = _kings[other];
                    int winner = _kings[colour];
                    int file = loser & 7;
                    int rank = loser >> 3;
                    int fromCentre = Math.Max(3 - file, file - 4) + Math.Max(3 - rank, rank - 4);
                    int apart = Math.Abs((winner & 7) - file) + Math.Abs((winner >> 3) - rank);
                    int push = 20 * fromCentre + 8 * (14 - apart);
                    score += colour == 0 ? push : -push;
                }
            }
            return Side == 0 ? score + 10 : -score + 10;
        }

        public string ToFen()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int rank = 7; rank >= 0; rank--)
            {
                int empty = 0;
                for (int file = 0; file < 8; file++)
                {
                    int piece = _board[rank * 8 + file];
                    if (piece == 0)
                    {
                        empty++;
                        continue;
                    }
                    if (empty > 0)
                        text.Append(empty);
                    empty = 0;
                    text.Append(PieceLetters[piece]);
                }
                if (empty > 0)
                    text.Append(empty);
                if (rank > 0)
                    text.Append('/');
            }
            text.Append(Side == 0 ? " w " : " b ");
            if (_castling == 0)
                text.Append('-');
            if ((_castling & WhiteKingSide) != 0)
                text.Append('K');
            if ((_castling & WhiteQueenSide) != 0)
                text.Append('Q');
            if ((_castling & BlackKingSide) != 0)
                text.Append('k');
            if ((_castling & BlackQueenSide) != 0)
                text.Append('q');
            text.Append(' ').Append(_enPassant < 0 ? "-" : SquareName(_enPassant));
            text.Append(' ').Append(HalfMoves).Append(" 1");
            return text.ToString();
        }

        private int AddCastling(int[] moves, int count, int us)
        {
            int home = us == 0 ? 4 : 60;
            int kingSide = us == 0 ? WhiteKingSide : BlackKingSide;
            int queenSide = us == 0 ? WhiteQueenSide : BlackQueenSide;
            if ((_castling & (kingSide | queenSide)) == 0 || IsAttacked(home, 1 - us))
                return count;
            if ((_castling & kingSide) != 0 && _board[home + 1] == 0 && _board[home + 2] == 0
                && !IsAttacked(home + 1, 1 - us) && !IsAttacked(home + 2, 1 - us))
                moves[count++] = home | ((home + 2) << 6) | (KindCastle << 15);
            if ((_castling & queenSide) != 0 && _board[home - 1] == 0 && _board[home - 2] == 0 && _board[home - 3] == 0
                && !IsAttacked(home - 1, 1 - us) && !IsAttacked(home - 2, 1 - us))
                moves[count++] = home | ((home - 2) << 6) | (KindCastle << 15);
            return count;
        }

        private static ulong PieceKey(int square, int piece)
        {
            int colour = piece > 6 ? 1 : 0;
            return Keys.Stone(square * 6 + piece - colour * 6 - 1, colour + 1);
        }

        // The part of the hash for the castling rights and the en passant file.
        private ulong StateKey()
        {
            ulong key = Keys.Stone(64 * 6 + _castling, 1);
            if (_enPassant >= 0)
                key ^= Keys.Stone(64 * 6 + 16 + (_enPassant & 7), 1);
            return key;
        }

        private ulong ComputeHash()
        {
            ulong hash = StateKey();
            for (int square = 0; square < 64; square++)
            {
                if (_board[square] != 0)
                    hash ^= PieceKey(square, _board[square]);
            }
            return Side == 1 ? hash ^ Keys.SideToMove : hash;
        }

        private static int[] BuildRightsKept()
        {
            int[] kept = new int[64];
            for (int i = 0; i < 64; i++)
                kept[i] = 15;
            kept[4] = 15 & ~(WhiteKingSide | WhiteQueenSide);
            kept[7] = 15 & ~WhiteKingSide;
            kept[0] = 15 & ~WhiteQueenSide;
            kept[60] = 15 & ~(BlackKingSide | BlackQueenSide);
            kept[63] = 15 & ~BlackKingSide;
            kept[56] = 15 & ~BlackQueenSide;
            return kept;
        }
    }
}
