using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BoardSandbox.Core.Ai
{
    // What a stone on an empty point makes of the line through it in one direction, weakest first.
    public enum GomokuShape : byte
    {
        None = 0,

        // Two more stones make an open four.
        Two = 1,

        // One more stone makes a four that can be stopped.
        Three = 2,

        // One more stone makes an open four.
        OpenThree = 3,

        // One point completes the line; the opponent must take it.
        Four = 4,

        // Two points complete the line; the opponent cannot take both.
        OpenFour = 5,

        // The line is complete.
        Five = 6
    }

    // The shape for every pattern of stones around a point on one line. A pattern is the WinLength - 1 points on
    // either side, each empty, own or closed (an opponent's stone or the edge), so broken lines such as XX_X are
    // seen as well as unbroken ones. Shapes are worked out the first time a pattern comes up and remembered.
    public sealed class GomokuShapes
    {
        private const byte Unknown = 255;
        private const int FlatReach = 5;
        private const int KeyedReach = 16;

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, GomokuShapes> Shared = new Dictionary<int, GomokuShapes>();

        private readonly int _winLength;
        private readonly bool _exactWin;
        private readonly byte[] _flat;
        private readonly ConcurrentDictionary<ulong, byte> _keyed;

        private GomokuShapes(int winLength, bool exactWin)
        {
            _winLength = winLength;
            _exactWin = exactWin;
            Reach = winLength - 1;
            if (Reach <= FlatReach)
            {
                _flat = new byte[1 << (4 * Reach)];
                for (int i = 0; i < _flat.Length; i++)
                    _flat[i] = Unknown;
            }
            else if (Reach <= KeyedReach)
            {
                _keyed = new ConcurrentDictionary<ulong, byte>();
            }
        }

        // Points looked at on either side.
        public int Reach { get; }

        public static GomokuShapes For(int winLength, bool exactWin)
        {
            int key = winLength * 2 + (exactWin ? 1 : 0);
            lock (Gate)
            {
                if (!Shared.TryGetValue(key, out GomokuShapes shapes))
                {
                    shapes = new GomokuShapes(winLength, exactWin);
                    Shared[key] = shapes;
                }
                return shapes;
            }
        }

        // The pattern packs two bits per point, the farthest point of the first side lowest: 0 empty, 1 own,
        // 2 closed. Searches on several threads may ask at once; they would store the same answer.
        public GomokuShape Lookup(ulong pattern)
        {
            if (_flat != null)
            {
                byte known = _flat[pattern];
                if (known == Unknown)
                {
                    known = (byte)Classify(pattern);
                    _flat[pattern] = known;
                }
                return (GomokuShape)known;
            }
            if (_keyed != null)
            {
                if (!_keyed.TryGetValue(pattern, out byte known))
                {
                    known = (byte)Classify(pattern);
                    _keyed[pattern] = known;
                }
                return (GomokuShape)known;
            }
            return Classify(pattern);
        }

        // For a pattern too long to pack: the points themselves, the stone in the middle included.
        public GomokuShape Classify(int[] line)
        {
            int middle = line.Length / 2;
            if (WinsAt(line, middle))
                return GomokuShape.Five;
            int wins = WinningPoints(line);
            if (wins >= 2)
                return GomokuShape.OpenFour;
            if (wins == 1)
                return GomokuShape.Four;

            bool three = false;
            for (int e = 0; e < line.Length; e++)
            {
                if (line[e] != 0)
                    continue;
                line[e] = 1;
                int next = WinningPoints(line);
                line[e] = 0;
                if (next >= 2)
                    return GomokuShape.OpenThree;
                if (next == 1)
                    three = true;
            }
            if (three)
                return GomokuShape.Three;

            for (int e = 0; e < line.Length; e++)
            {
                if (line[e] != 0)
                    continue;
                line[e] = 1;
                for (int f = e + 1; f < line.Length; f++)
                {
                    if (line[f] != 0)
                        continue;
                    line[f] = 1;
                    int next = WinningPoints(line);
                    line[f] = 0;
                    if (next >= 2)
                    {
                        line[e] = 0;
                        return GomokuShape.Two;
                    }
                }
                line[e] = 0;
            }
            return GomokuShape.None;
        }

        private GomokuShape Classify(ulong pattern)
        {
            int[] line = new int[2 * Reach + 1];
            for (int j = 0; j < 2 * Reach; j++)
                line[j < Reach ? j : j + 1] = (int)((pattern >> (2 * j)) & 3);
            line[Reach] = 1;
            return Classify(line);
        }

        private int WinningPoints(int[] line)
        {
            int count = 0;
            for (int e = 0; e < line.Length; e++)
            {
                if (line[e] != 0)
                    continue;
                line[e] = 1;
                if (WinsAt(line, e))
                    count++;
                line[e] = 0;
            }
            return count;
        }

        private bool WinsAt(int[] line, int point)
        {
            int total = 1;
            for (int i = point - 1; i >= 0 && line[i] == 1; i--)
                total++;
            for (int i = point + 1; i < line.Length && line[i] == 1; i++)
                total++;
            return total == _winLength || (total > _winLength && !_exactWin);
        }
    }
}
