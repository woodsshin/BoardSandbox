using System;
using System.Collections.Generic;
using System.Threading;

namespace BoardSandbox.Core.Ai
{
    public sealed class ChessSearchResult
    {
        // The chosen move as ChessPosition encodes it, and in the form "e2e4" or "e7e8q".
        public int Move { get; internal set; }
        public string Uci { get; internal set; }

        // Simulations run by this search.
        public int Simulations { get; internal set; }

        // Simulations that went through the chosen move, including those remembered from earlier searches.
        public int Visits { get; internal set; }

        // How the mover stands after the move, from 0 (lost) over 0.5 (even) to 1 (won).
        public double Value { get; internal set; }

        // Positions in the transposition table after the search.
        public int Nodes { get; internal set; }

        // How often a line of play arrived at a position that was already in the table.
        public int Transpositions { get; internal set; }

        // The only legal move, so nothing was simulated.
        public bool Forced { get; internal set; }
    }

    // Monte Carlo tree search for chess. Positions are kept in a transposition table under their Zobrist hash, so
    // lines that reach the same position by another order of moves share one node, and the search of the next move
    // starts from what this one learned.
    //
    // Playing chess out at random says nothing about a position, so a simulation ends differently than in gomoku:
    // it walks down the tree to a position that is new, looks one move further there with every exchange of
    // pieces played to its end (a quiescence search), and takes that as the worth of the position. The same look
    // gives every move of the new position a first guess, which steers the walk (PUCT): good-looking moves are
    // tried first, the others when the good ones disappoint. Up the tree a position is worth a mix of the average
    // of its simulations and of its best reply, which lets a single refutation count.
    //
    // The number of simulations is the strength: each adds one position to the tree, so more of them read further.
    //
    // An instance is not thread safe.
    public sealed class ChessMcts
    {
        // The table is dropped when it grows past this.
        private const int MaxNodes = 300000;

        // Plies the quiescence search may go beyond the last move of the tree.
        private const int QuietDepth = 6;

        // Beyond this ply a side in check no longer tries every way out.
        private const int CheckDepth = 2;

        // A move this many hundredths of a pawn behind the best gets no share of the first guesses, so its exact
        // worth does not matter.
        private const int PriorWindow = 250;

        // The longest line of play a simulation follows.
        private const int MaxLine = 200;

        // Hundredths of a pawn that halve the odds: a pawn up is about 58 %, a piece about 75 %.
        private const double ValueScale = 300.0;

        private sealed class Node
        {
            public int[] Moves;
            public float[] Priors;
            public Node[] Kids;
            public int Visits;

            // Sum of the values of the simulations and the best that is known, both for the side to move here.
            public double Sum;
            public double Best;

            // What the look ahead said when the position was added.
            public double First;

            // The game ends here (mate, stalemate or a draw by the rules); First is the result.
            public bool Terminal;
        }

        private readonly Dictionary<ulong, Node> _table = new Dictionary<ulong, Node>();
        private readonly List<Node> _path = new List<Node>();
        private readonly List<ulong> _line = new List<ulong>();
        private readonly HashSet<ulong> _played = new HashSet<ulong>();
        private readonly ChessPosition _scratch = new ChessPosition();
        private readonly int[] _legal = new int[ChessPosition.MaxMoves];
        private readonly int[] _scores = new int[ChessPosition.MaxMoves];
        private readonly int[][] _buffers = new int[QuietDepth + 4][];
        private readonly int[][] _order = new int[QuietDepth + 4][];
        private int _transpositions;

        public ChessMcts()
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                _buffers[i] = new int[ChessPosition.MaxMoves];
                _order[i] = new int[ChessPosition.MaxMoves];
            }
        }

        // Weight of the exploration term.
        public double Exploration { get; set; } = 1.0;

        // How much of a position's worth is its best reply instead of the average of its simulations.
        public double BestReplyWeight { get; set; } = 0.5;

        // Hundredths of a pawn by which a move may look worse than the best and still get a fair share of the
        // first guesses.
        public double PriorSpread { get; set; } = 60.0;

        // Plies without a capture or a pawn move that draw the game.
        public int HalfMoveLimit { get; set; } = 100;

        public int NodeCount => _table.Count;

        public void Clear()
        {
            _table.Clear();
        }

        // Picks a move for the side to move. played holds the hashes of the positions the game has been through,
        // which the search treats as draws by repetition; null for none. Throws InvalidOperationException when the
        // side to move has no legal move.
        public ChessSearchResult Search(ChessPosition root, int simulations, Random random, IEnumerable<ulong> played = null,
            CancellationToken cancel = default)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            simulations = Math.Max(1, simulations);

            _played.Clear();
            if (played != null)
            {
                foreach (ulong hash in played)
                    _played.Add(hash);
            }
            _played.Remove(root.Hash);
            if (_table.Count > MaxNodes)
                _table.Clear();
            _transpositions = 0;

            _scratch.CopyFrom(root);
            if (!_table.TryGetValue(root.Hash, out Node rootNode) || rootNode.Terminal)
            {
                rootNode = Create(_scratch, random);
                _table[root.Hash] = rootNode;
            }
            if (rootNode.Moves.Length == 0)
                throw new InvalidOperationException("the side to move has no legal move");

            ChessSearchResult result = new ChessSearchResult();
            if (rootNode.Moves.Length == 1)
            {
                result.Move = rootNode.Moves[0];
                result.Uci = ChessPosition.Uci(result.Move);
                result.Forced = true;
                result.Value = rootNode.First;
                result.Nodes = _table.Count;
                return result;
            }

            for (int i = 0; i < simulations; i++)
            {
                cancel.ThrowIfCancellationRequested();
                Simulate(rootNode, root, random);
                result.Simulations++;
            }

            int best = -1;
            for (int i = 0; i < rootNode.Moves.Length; i++)
            {
                Node kid = rootNode.Kids[i];
                if (kid == null)
                    continue;
                if (best < 0 || kid.Visits > rootNode.Kids[best].Visits
                    || (kid.Visits == rootNode.Kids[best].Visits && Worth(kid) < Worth(rootNode.Kids[best])))
                    best = i;
            }
            result.Move = rootNode.Moves[best];
            result.Uci = ChessPosition.Uci(result.Move);
            result.Visits = rootNode.Kids[best].Visits;
            result.Value = 1 - Worth(rootNode.Kids[best]);
            result.Nodes = _table.Count;
            result.Transpositions = _transpositions;
            return result;
        }

        // The worth of a position for the side to move in it.
        private double Worth(Node node)
        {
            if (node.Terminal || node.Visits == 0)
                return node.First;
            return (1 - BestReplyWeight) * (node.Sum / node.Visits) + BestReplyWeight * node.Best;
        }

        private void Simulate(Node rootNode, ChessPosition root, Random random)
        {
            ChessPosition position = _scratch;
            position.CopyFrom(root);
            _path.Clear();
            _line.Clear();
            _path.Add(rootNode);
            _line.Add(root.Hash);
            Node node = rootNode;
            double value;

            while (true)
            {
                if (node.Terminal)
                {
                    value = node.First;
                    break;
                }

                // PUCT: the best worth so far plus a bonus that is large for moves with a good first guess and few
                // visits. A move that was never tried counts as a little worse than the position looks.
                double here = Worth(node);
                double root2 = Math.Sqrt(node.Visits + 1);
                int pick = 0;
                double pickScore = double.NegativeInfinity;
                for (int i = 0; i < node.Moves.Length; i++)
                {
                    Node kid = node.Kids[i];
                    double worth = kid == null ? here - 0.1 : 1 - Worth(kid);
                    double score = worth + Exploration * node.Priors[i] * root2 / (1 + (kid == null ? 0 : kid.Visits));
                    if (score > pickScore)
                    {
                        pickScore = score;
                        pick = i;
                    }
                }

                position.Make(node.Moves[pick]);
                Node next = node.Kids[pick];
                if (next == null)
                {
                    ulong hash = position.Hash;
                    if (_played.Contains(hash) || _line.Contains(hash))
                    {
                        // The position came up before in this game or this line: a draw by repetition, for this way
                        // of reaching it only, so it stays out of the table.
                        next = new Node { Moves = new int[0], Terminal = true, First = 0.5, Best = 0.5 };
                        node.Kids[pick] = next;
                    }
                    else if (_table.TryGetValue(hash, out next))
                    {
                        _transpositions++;
                        node.Kids[pick] = next;
                        _path.Add(next);
                        _line.Add(hash);
                        node = next;
                        continue;
                    }
                    else
                    {
                        next = Create(position, random);
                        _table[hash] = next;
                        node.Kids[pick] = next;
                    }
                    _path.Add(next);
                    value = next.First;
                    break;
                }
                if (!next.Terminal && (_played.Contains(position.Hash) || _line.Contains(position.Hash) || _path.Count >= MaxLine))
                {
                    // Known moves led back to a position of this game or this line: a draw by repetition here,
                    // whatever the position is worth when reached another way.
                    value = 0.5;
                    break;
                }
                _path.Add(next);
                _line.Add(position.Hash);
                node = next;
            }

            // The value is for the side to move in the last position; it flips with every step up.
            for (int i = _path.Count - 1; i >= 0; i--)
            {
                Node visited = _path[i];
                visited.Visits++;
                visited.Sum += value;
                if (!visited.Terminal)
                {
                    double best = double.NegativeInfinity;
                    bool all = true;
                    for (int k = 0; k < visited.Kids.Length; k++)
                    {
                        Node kid = visited.Kids[k];
                        if (kid == null)
                        {
                            all = false;
                            continue;
                        }
                        double worth = 1 - (kid.Terminal || kid.Visits == 0 ? kid.First : kid.Best);
                        if (worth > best)
                            best = worth;
                    }
                    // Until every move was tried, one of the others may still be as good as the first look said.
                    visited.Best = all ? best : Math.Max(best, visited.First);
                }
                value = 1 - value;
            }
        }

        // A new position: its legal moves, each with a first guess from the look ahead, and its worth.
        private Node Create(ChessPosition position, Random random)
        {
            Node node = new Node();
            int count = position.LegalMoves(_legal);
            if (count == 0)
            {
                node.Moves = new int[0];
                node.Terminal = true;
                node.First = position.InCheck() ? 0 : 0.5;
                node.Best = node.First;
                return node;
            }
            if (position.InsufficientMaterial() || (HalfMoveLimit > 0 && position.HalfMoves >= HalfMoveLimit))
            {
                node.Moves = new int[0];
                node.Terminal = true;
                node.First = 0.5;
                node.Best = 0.5;
                return node;
            }

            // Captures first: a good one early lets the look at the others stop sooner.
            for (int i = 0; i < count; i++)
                _scores[i] = position.Gain(_legal[i]);
            Array.Sort(_scores, _legal, 0, count);
            Array.Reverse(_legal, 0, count);

            int top = -ChessPosition.Mate;
            for (int i = 0; i < count; i++)
            {
                position.Make(_legal[i]);
                // A few hundredths of chance, so that equal moves are not always tried in the same order.
                int floor = Math.Max(-ChessPosition.Mate, top - PriorWindow);
                int score = -Quiet(position, -ChessPosition.Mate, -floor, 0) + random.Next(5);
                position.Unmake();
                _scores[i] = score;
                if (score > top)
                    top = score;
            }

            node.Moves = new int[count];
            node.Priors = new float[count];
            node.Kids = new Node[count];
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                node.Moves[i] = _legal[i];
                double share = Math.Exp((_scores[i] - top) / PriorSpread);
                node.Priors[i] = (float)share;
                total += share;
            }
            for (int i = 0; i < count; i++)
                node.Priors[i] = (float)(0.9 * node.Priors[i] / total + 0.1 / count);
            node.First = Odds(top);
            node.Best = node.First;
            return node;
        }

        // Hundredths of a pawn as the chance to win.
        private static double Odds(int score)
        {
            if (score >= ChessPosition.Mate - 100)
                return 1;
            if (score <= -ChessPosition.Mate + 100)
                return 0;
            return 1.0 / (1.0 + Math.Exp(-score / ValueScale));
        }

        // The worth of a position for the side to move once the exchanges are over: the side may stop and take the
        // static evaluation, or go on capturing. A side in check must answer the check instead.
        private int Quiet(ChessPosition position, int alpha, int beta, int ply)
        {
            bool check = position.InCheck();
            int best = -ChessPosition.Mate + ply;
            if (!check)
            {
                best = position.Evaluate();
                if (best >= beta || ply >= QuietDepth)
                    return best;
                if (best > alpha)
                    alpha = best;
            }
            else if (ply >= CheckDepth)
            {
                // Deep in the exchanges a check is not followed any further.
                return position.Evaluate();
            }

            int[] moves = _buffers[ply];
            int[] order = _order[ply];
            int count = position.PseudoMoves(moves, !check);
            for (int i = 0; i < count; i++)
                order[i] = position.Gain(moves[i]);

            bool any = false;
            for (int i = 0; i < count; i++)
            {
                // The most valuable capture first.
                int pick = i;
                for (int j = i + 1; j < count; j++)
                {
                    if (order[j] > order[pick])
                        pick = j;
                }
                int move = moves[pick];
                moves[pick] = moves[i];
                order[pick] = order[i];

                // A capture that cannot lift the score to alpha, or gives a piece for a cheaper one that is guarded,
                // is not worth a look.
                if (!check && position.IsHopeless(move, best, alpha))
                    continue;
                if (!position.TryMake(move))
                    continue;
                any = true;
                int score = -Quiet(position, -beta, -alpha, ply + 1);
                position.Unmake();
                if (score > best)
                {
                    best = score;
                    if (score > alpha)
                    {
                        alpha = score;
                        if (alpha >= beta)
                            break;
                    }
                }
            }
            // In check with no way out is mate; the score was set to that above.
            if (check && !any)
                return -ChessPosition.Mate + ply;
            return best;
        }
    }
}
