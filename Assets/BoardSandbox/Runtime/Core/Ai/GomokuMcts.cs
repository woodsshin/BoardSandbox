using System;
using System.Collections.Generic;
using System.Threading;

namespace BoardSandbox.Core.Ai
{
    public sealed class GomokuSearchResult
    {
        // The chosen point (y * size + x from 0).
        public int Move { get; internal set; }

        // Simulations run by this search.
        public int Simulations { get; internal set; }

        // Simulations that went through the chosen move, including those remembered from earlier searches.
        public int Visits { get; internal set; }

        // Share of those simulations the mover won, a draw counting as half.
        public double WinRate { get; internal set; }

        // Positions in the transposition table after the search.
        public int Nodes { get; internal set; }

        // How often a line of play arrived at a position that was already in the table.
        public int Transpositions { get; internal set; }

        // The move was the only sensible one (a win or the answer to one), so nothing was simulated.
        public bool Forced { get; internal set; }
    }

    // Monte Carlo tree search for gomoku. Positions are kept in a transposition table under their Zobrist hash, so
    // lines of play that reach the same position by another order of moves share one node and its statistics, and
    // a later search of the same game starts from what an earlier one learned.
    //
    // One simulation walks down the tree by UCB1, adds one position, plays the game out from there and adds the
    // result to every position on the way. The number of simulations is the strength: with few of them the search
    // sees wins and losses a move or two ahead, with more it reads further.
    //
    // Three things keep a small number of simulations from being wasted on the 200 points of a board:
    // only points near stones are moves; a win in one and the answer to the opponent's win in one are played without
    // thought, in the tree and in the playouts (which also make and stop open lines one short of winning); and a
    // position gets its moves in the order of a quick guess, a few at first and more as it is visited (progressive
    // widening).
    //
    // An instance is not thread safe.
    public sealed class GomokuMcts
    {
        // The table is dropped when it grows past this.
        private const int MaxNodes = 400000;

        private static readonly double[] RunWeights = { 0, 2, 14, 90, 600 };

        private sealed class Node
        {
            public int ToMove;
            public int Stones;
            public int Visits;

            // Points won by the seat that moved into this position.
            public double Wins;

            // -1 while the game goes on, 0 for a draw, otherwise the winner.
            public int Terminal = -1;

            public int[] Moves;
            public Node[] Kids;
            public int Expanded;
        }

        private readonly Dictionary<ulong, Node> _table = new Dictionary<ulong, Node>();
        private readonly List<Node> _path = new List<Node>();
        private readonly List<int> _blocks = new List<int>();
        private readonly List<ulong> _stale = new List<ulong>();
        private GomokuPosition _scratch;
        private int _transpositions;

        // Weight of the exploration term of UCB1.
        public double Exploration { get; set; } = 0.25;

        // A position visited n times offers 1 + Widening * sqrt(n) of its moves.
        public double Widening { get; set; } = 0.5;

        public int NodeCount => _table.Count;

        public void Clear()
        {
            _table.Clear();
        }

        // Picks a move for the seat to move. The position must not be finished.
        public GomokuSearchResult Search(GomokuPosition root, int simulations, Random random, CancellationToken cancel = default)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            if (root.IsFull || root.Winner != 0)
                throw new InvalidOperationException("the game is over");
            simulations = Math.Max(1, simulations);

            Prepare(root);
            _transpositions = 0;
            if (!_table.TryGetValue(root.Hash, out Node rootNode))
            {
                rootNode = new Node { ToMove = root.Current, Stones = root.Moves };
                _table[root.Hash] = rootNode;
            }
            if (rootNode.Moves == null)
                ListMoves(rootNode, root, random);

            GomokuSearchResult result = new GomokuSearchResult();
            if (rootNode.Moves.Length == 1)
            {
                result.Move = rootNode.Moves[0];
                result.Forced = true;
                result.Nodes = _table.Count;
                return result;
            }

            for (int i = 0; i < simulations; i++)
            {
                cancel.ThrowIfCancellationRequested();
                Simulate(rootNode, root, random);
                result.Simulations++;
            }

            int best = 0;
            for (int i = 1; i < rootNode.Expanded; i++)
            {
                Node kid = rootNode.Kids[i];
                Node top = rootNode.Kids[best];
                if (kid.Visits > top.Visits || (kid.Visits == top.Visits && kid.Wins > top.Wins))
                    best = i;
            }
            Node chosen = rootNode.Kids[best];
            result.Move = rootNode.Moves[best];
            result.Visits = chosen.Visits;
            result.WinRate = chosen.Visits > 0 ? chosen.Wins / chosen.Visits : 0.5;
            result.Nodes = _table.Count;
            result.Transpositions = _transpositions;
            return result;
        }

        private void Prepare(GomokuPosition root)
        {
            if (_scratch == null || _scratch.Size != root.Size || _scratch.WinLength != root.WinLength || _scratch.ExactWin != root.ExactWin)
            {
                // Another game: its positions would hash like those of this one.
                _scratch = new GomokuPosition(root.Size, root.WinLength, root.ExactWin);
                _table.Clear();
                return;
            }
            if (_table.Count > MaxNodes)
            {
                _table.Clear();
                return;
            }
            // Positions with fewer stones than this one cannot come up again.
            _stale.Clear();
            foreach (KeyValuePair<ulong, Node> pair in _table)
            {
                if (pair.Value.Stones < root.Moves)
                    _stale.Add(pair.Key);
            }
            foreach (ulong hash in _stale)
                _table.Remove(hash);
        }

        private void Simulate(Node rootNode, GomokuPosition root, Random random)
        {
            GomokuPosition position = _scratch;
            position.CopyFrom(root);
            _path.Clear();
            _path.Add(rootNode);
            Node node = rootNode;
            int outcome;

            while (true)
            {
                if (node.Terminal >= 0)
                {
                    outcome = node.Terminal;
                    break;
                }
                if (node.Moves == null)
                    ListMoves(node, position, random);

                int offered = Math.Min(node.Moves.Length, 1 + (int)(Widening * Math.Sqrt(node.Visits)));
                if (node.Expanded < offered)
                {
                    int slot = node.Expanded++;
                    int mover = position.Current;
                    bool won = position.Play(node.Moves[slot]);
                    bool known = _table.TryGetValue(position.Hash, out Node kid);
                    if (!known)
                    {
                        kid = new Node { ToMove = position.Current, Stones = position.Moves };
                        kid.Terminal = won ? mover : position.IsFull ? 0 : -1;
                        _table[position.Hash] = kid;
                    }
                    else
                    {
                        _transpositions++;
                    }
                    node.Kids[slot] = kid;
                    _path.Add(kid);
                    node = kid;
                    if (known)
                        continue;
                    outcome = kid.Terminal >= 0 ? kid.Terminal : Playout(position, random);
                    break;
                }

                int best = 0;
                double bestScore = double.NegativeInfinity;
                double log = Math.Log(node.Visits + 1);
                for (int i = 0; i < node.Expanded; i++)
                {
                    Node kid = node.Kids[i];
                    double score = kid.Visits == 0
                        ? double.PositiveInfinity
                        : kid.Wins / kid.Visits + Exploration * Math.Sqrt(log / kid.Visits);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                position.Play(node.Moves[best]);
                node = node.Kids[best];
                _path.Add(node);
            }

            foreach (Node visited in _path)
            {
                visited.Visits++;
                int mover = 3 - visited.ToMove;
                visited.Wins += outcome == 0 ? 0.5 : outcome == mover ? 1.0 : 0.0;
            }
        }

        // The moves of a position: the winning move alone when there is one, else the answers to the opponent's
        // winning moves alone, else every point near a stone, the most promising first.
        private void ListMoves(Node node, GomokuPosition position, Random random)
        {
            int seat = position.Current;
            int other = 3 - seat;
            int count = position.CandidateCount;
            int[] moves;

            if (count == 0)
            {
                // No stone yet: the middle. A board without stones near any empty point does not occur otherwise.
                if (position.Moves == 0)
                {
                    moves = new[] { (position.Size / 2) * position.Size + position.Size / 2 };
                }
                else
                {
                    List<int> empty = new List<int>();
                    for (int index = 0; index < position.CellCount; index++)
                    {
                        if (position.Cell(index) == 0)
                            empty.Add(index);
                    }
                    moves = empty.ToArray();
                }
            }
            else
            {
                int win = -1;
                _blocks.Clear();
                for (int i = 0; i < count && win < 0; i++)
                {
                    int point = position.Candidate(i);
                    if (position.Wins(point, seat))
                        win = point;
                    else if (position.Wins(point, other))
                        _blocks.Add(point);
                }
                if (win >= 0)
                {
                    moves = new[] { win };
                }
                else if (_blocks.Count > 0)
                {
                    moves = _blocks.ToArray();
                }
                else
                {
                    moves = new int[count];
                    double[] order = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        int point = position.Candidate(i);
                        moves[i] = point;
                        order[i] = -(Promise(position, point, seat, other) + random.NextDouble());
                    }
                    Array.Sort(order, moves);
                }
            }

            node.Moves = moves;
            node.Kids = new Node[moves.Length];
            node.Expanded = 0;
        }

        // A quick guess at the worth of a point: the longer the lines it extends or cuts, the better.
        private static double Promise(GomokuPosition position, int point, int seat, int other)
        {
            double total = 0;
            int cap = RunWeights.Length - 1;
            for (int direction = 0; direction < 4; direction++)
            {
                total += RunWeights[Math.Min(cap, position.Line(point, seat, direction))];
                total += 0.8 * RunWeights[Math.Min(cap, position.Line(point, other, direction))];
            }
            return total;
        }

        // Plays the game out and gives the winner, 0 for a draw. Both sides take a win in one, answer the opponent's
        // win in one, make a line that wins the move after next or take the point where the opponent would, and
        // otherwise play a random point near the stones.
        private static int Playout(GomokuPosition position, Random random)
        {
            int previous = -1;
            int last = -1;
            int ply = 0;
            while (!position.IsFull)
            {
                int seat = position.Current;
                int other = 3 - seat;
                int move = -1;
                if (ply < 2)
                {
                    // Nothing is known yet about the lines that were on the board before the playout.
                    int block = -1;
                    for (int i = 0; i < position.CandidateCount && move < 0; i++)
                    {
                        int point = position.Candidate(i);
                        if (position.Wins(point, seat))
                            move = point;
                        else if (block < 0 && position.Wins(point, other))
                            block = point;
                    }
                    if (move < 0)
                        move = block;
                }
                else
                {
                    // From here on a winning point can only come from the last stone of either side.
                    move = position.WinningPointThrough(previous, seat);
                    if (move < 0)
                        move = position.WinningPointThrough(last, other);
                    if (move < 0)
                        move = position.OpenLinePointThrough(previous, seat);
                    if (move < 0)
                        move = position.OpenLinePointThrough(last, other);
                }
                if (move < 0)
                {
                    if (position.CandidateCount == 0)
                        return 0;
                    move = position.Candidate(random.Next(position.CandidateCount));
                }
                if (position.Play(move))
                    return seat;
                previous = last;
                last = move;
                ply++;
            }
            return 0;
        }
    }
}
