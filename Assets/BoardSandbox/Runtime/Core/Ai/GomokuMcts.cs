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

        // The move starts a row of fours that the opponent can only answer until it wins.
        public bool Solved { get; internal set; }
    }

    // Monte Carlo tree search for gomoku. Positions are kept in a transposition table under their Zobrist hash, so
    // lines of play that reach the same position by another order of moves share one node and its statistics, and
    // a later search of the same game starts from what an earlier one learned.
    //
    // One simulation walks down the tree by UCB1, adds one position, plays the game out from there and adds the
    // result to every position on the way. The number of simulations is the strength: with few of them the search
    // sees wins and losses a move or two ahead, with more it reads further.
    //
    // The search knows the shapes of gomoku (GomokuShape), which is what makes its simulations worth something:
    // - only points near stones are moves;
    // - a win, the answer to a win, a move that wins by force (an open four, two fours, a four with an open three)
    //   and the answer to such a move are played without thought, in the tree and in the playouts;
    // - a position gets its other moves in the order of their shapes, a few at first and more as it is visited
    //   (progressive widening), and the playouts prefer good shapes too;
    // - before anything is simulated, a row of fours that wins by force is looked for (the opponent has one answer
    //   to each four), for the mover and, to keep away from it, for the opponent. The more simulations, the longer
    //   the rows it may read.
    //
    // An instance is not thread safe.
    public sealed class GomokuMcts
    {
        // The table is dropped when it grows past this.
        private const int MaxNodes = 400000;

        // Fours in a row the forced-win search reads at most, and positions it may look at per simulation allowed.
        private const int MaxForcedDepth = 14;
        private const int ForcedNodesPerSimulation = 6;

        // Root moves tried against a forced win of the opponent.
        private const int MaxDefences = 24;

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
        private readonly List<int> _picked = new List<int>();
        private readonly List<int> _fours = new List<int>();
        private readonly List<ulong> _stale = new List<ulong>();
        private GomokuPosition _scratch;
        private GomokuPosition[] _forced;
        private int _forcedBudget;
        private int _transpositions;

        // Weight of the exploration term of UCB1.
        public double Exploration { get; set; } = 0.4;

        // A position visited n times offers 1 + Widening * sqrt(n) of its moves.
        public double Widening { get; set; } = 0.8;

        // How often a playout takes the move with the best shapes instead of a random one near the stones.
        public double Greed { get; set; } = 0.7;

        // A playout that has not ended after this many moves counts as a draw.
        public int PlayoutLength { get; set; } = 24;

        // Look for wins by a row of fours before simulating.
        public bool ReadForcedWins { get; set; } = true;

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

            if (ReadForcedWins)
            {
                int budget = simulations * ForcedNodesPerSimulation;
                if (ForcedWin(root, budget, out int winning))
                {
                    result.Move = winning;
                    result.Solved = true;
                    result.Nodes = _table.Count;
                    return result;
                }
                int[] safe = Defences(root, rootNode, budget, cancel);
                if (safe != null)
                {
                    // Only these moves keep the opponent from a forced win; start this position afresh with them.
                    rootNode = new Node { ToMove = root.Current, Stones = root.Moves, Moves = safe, Kids = new Node[safe.Length] };
                    _table[root.Hash] = rootNode;
                    if (safe.Length == 1)
                    {
                        result.Move = safe[0];
                        result.Forced = true;
                        result.Nodes = _table.Count;
                        return result;
                    }
                }
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
                _forced = new GomokuPosition[MaxForcedDepth + 2];
                for (int i = 0; i < _forced.Length; i++)
                    _forced[i] = new GomokuPosition(root.Size, root.WinLength, root.ExactWin);
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

        // The moves of a position, the most promising first:
        // the winning move alone; else the answers to the opponent's winning moves alone; else a move that wins by
        // force alone; else, when the opponent has such a move, the points where it would be made and the mover's
        // own fours; else every point near a stone.
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
                int[] all = new int[count];
                double[] order = new double[count];
                int win = -1;
                int attack = -1;
                int attackValue = -1;
                bool threatened = false;
                _picked.Clear();
                _fours.Clear();
                for (int i = 0; i < count && win < 0; i++)
                {
                    int point = position.Candidate(i);
                    GomokuThreat mine = position.Rate(point, seat, out int myValue);
                    GomokuThreat theirs = position.Rate(point, other, out int theirValue);
                    all[i] = point;
                    order[i] = -(myValue * 1.2 + theirValue + random.NextDouble());
                    if (mine == GomokuThreat.Win)
                        win = point;
                    else if (theirs == GomokuThreat.Win)
                        _picked.Add(point);
                    else if (mine == GomokuThreat.Winning && myValue > attackValue)
                    {
                        attack = point;
                        attackValue = myValue;
                    }
                    if (theirs == GomokuThreat.Winning)
                        threatened = true;
                    if (mine == GomokuThreat.Four)
                        _fours.Add(point);
                }

                if (win >= 0)
                {
                    moves = new[] { win };
                }
                else if (_picked.Count > 0)
                {
                    moves = _picked.ToArray();
                }
                else if (attack >= 0)
                {
                    moves = new[] { attack };
                }
                else
                {
                    Array.Sort(order, all);
                    if (threatened)
                    {
                        _picked.Clear();
                        foreach (int point in all)
                        {
                            if (position.Rate(point, other, out _) == GomokuThreat.Winning || _fours.Contains(point))
                                _picked.Add(point);
                        }
                        moves = _picked.ToArray();
                    }
                    else
                    {
                        moves = all;
                    }
                }
            }

            node.Moves = moves;
            node.Kids = new Node[moves.Length];
            node.Expanded = 0;
        }

        // Plays the game out and gives the winner, 0 for a draw. Both sides win when they can, stop a win, make a
        // move that wins by force or take the point where the opponent would, and otherwise play the point with the
        // best shapes most of the time and a random point near the stones the rest.
        private int Playout(GomokuPosition position, Random random)
        {
            for (int ply = 0; ply < PlayoutLength && !position.IsFull; ply++)
            {
                int count = position.CandidateCount;
                if (count == 0)
                    return 0;
                int seat = position.Current;
                int other = 3 - seat;
                int win = -1;
                int block = -1;
                int attack = -1;
                int defend = -1;
                int best = -1;
                int bestValue = -1;
                for (int i = 0; i < count; i++)
                {
                    int point = position.Candidate(i);
                    GomokuThreat mine = position.Rate(point, seat, out int myValue);
                    if (mine == GomokuThreat.Win)
                    {
                        win = point;
                        break;
                    }
                    GomokuThreat theirs = position.Rate(point, other, out int theirValue);
                    if (theirs == GomokuThreat.Win)
                        block = point;
                    else if (mine == GomokuThreat.Winning)
                        attack = point;
                    else if (theirs == GomokuThreat.Winning)
                        defend = point;
                    int value = myValue * 5 + theirValue * 4 + random.Next(16);
                    if (value > bestValue)
                    {
                        bestValue = value;
                        best = point;
                    }
                }

                int move = win >= 0 ? win
                    : block >= 0 ? block
                    : attack >= 0 ? attack
                    : defend >= 0 ? defend
                    : random.NextDouble() < Greed ? best
                    : position.Candidate(random.Next(count));
                if (position.Play(move))
                    return seat;
            }
            return 0;
        }

        // Whether the seat to move wins by a row of fours, each of which the opponent can only answer on one point,
        // and the first of them.
        private bool ForcedWin(GomokuPosition position, int budget, out int first)
        {
            _forcedBudget = budget;
            // Deeper only when the shallower search found nothing, so the shortest win comes first.
            for (int depth = 2; depth <= MaxForcedDepth && _forcedBudget > 0; depth += 4)
            {
                if (Fours(position, depth, out first))
                    return true;
            }
            first = -1;
            return false;
        }

        private bool Fours(GomokuPosition position, int depth, out int first)
        {
            first = -1;
            if (--_forcedBudget < 0)
                return false;
            int seat = position.Current;
            int other = 3 - seat;
            int count = position.CandidateCount;

            // The opponent's own four leaves one move, which must be a four as well to go on.
            int must = -1;
            for (int i = 0; i < count; i++)
            {
                int point = position.Candidate(i);
                if (position.Wins(point, seat))
                {
                    first = point;
                    return true;
                }
                if (position.Wins(point, other))
                {
                    if (must >= 0)
                        return false;
                    must = point;
                }
            }
            if (depth <= 0)
                return false;

            GomokuPosition next = _forced[depth];
            // Moves that win at once (an open four) before plain fours.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < count; i++)
                {
                    int point = position.Candidate(i);
                    if (must >= 0 && point != must)
                        continue;
                    GomokuThreat threat = position.Rate(point, seat, out _);
                    if (pass == 0 ? threat != GomokuThreat.Winning : threat != GomokuThreat.Four)
                        continue;

                    next.CopyFrom(position);
                    next.Play(point);
                    int answer = -1;
                    int answers = 0;
                    for (int j = 0; j < next.CandidateCount; j++)
                    {
                        int reply = next.Candidate(j);
                        if (next.Wins(reply, seat))
                        {
                            answer = reply;
                            answers++;
                        }
                    }
                    if (answers == 0)
                        continue;
                    if (answers >= 2)
                    {
                        first = point;
                        return true;
                    }
                    if (next.Play(answer))
                        continue;
                    if (Fours(next, depth - 1, out _))
                    {
                        first = point;
                        return true;
                    }
                    if (_forcedBudget < 0)
                        return false;
                }
            }
            return false;
        }

        // When the opponent would win by a row of fours if the mover did nothing about it: the moves after which
        // that is no longer so, in the order of the position's moves. Null when there is no such danger or nothing
        // was found against it.
        private int[] Defences(GomokuPosition root, Node rootNode, int budget, CancellationToken cancel)
        {
            GomokuPosition probe = _forced[MaxForcedDepth + 1];
            probe.CopyFrom(root);
            probe.Pass();
            if (!ForcedWin(probe, budget, out _))
                return null;

            List<int> safe = new List<int>();
            int tried = 0;
            foreach (int move in rootNode.Moves)
            {
                if (tried++ >= MaxDefences)
                    break;
                cancel.ThrowIfCancellationRequested();
                probe.CopyFrom(root);
                if (probe.Play(move))
                    return new[] { move };
                if (!ForcedWin(probe, budget, out _))
                    safe.Add(move);
            }
            return safe.Count > 0 && safe.Count < rootNode.Moves.Length ? safe.ToArray() : null;
        }
    }
}
