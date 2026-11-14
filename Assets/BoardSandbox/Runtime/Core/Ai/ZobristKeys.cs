using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Ai
{
    // Zobrist hashing: one fixed random 64-bit number for every (cell, seat) pair and one for the side to move.
    // The hash of a position is the XOR of the numbers of its stones, so placing a stone updates it with one XOR,
    // and the same position reached by a different order of moves gets the same hash.
    public sealed class ZobristKeys
    {
        private const int Seats = 2;

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, ZobristKeys> Shared = new Dictionary<int, ZobristKeys>();

        private readonly ulong[] _stones;

        public ZobristKeys(int cells, ulong seed = 0x9E3779B97F4A7C15UL)
        {
            if (cells < 1)
                throw new ArgumentOutOfRangeException(nameof(cells));
            Cells = cells;
            ulong state = seed ^ ((ulong)cells * 0xD6E8FEB86659FD93UL);
            _stones = new ulong[cells * Seats];
            for (int i = 0; i < _stones.Length; i++)
                _stones[i] = Next(ref state);
            SideToMove = Next(ref state);
        }

        public int Cells { get; }

        // Part of the hash while seat 2 is to move.
        public ulong SideToMove { get; }

        public ulong Stone(int cell, int seat)
        {
            return _stones[cell * Seats + seat - 1];
        }

        // The same keys for every board of this many cells, so hashes stay comparable between searches.
        public static ZobristKeys For(int cells)
        {
            lock (Gate)
            {
                if (!Shared.TryGetValue(cells, out ZobristKeys keys))
                {
                    keys = new ZobristKeys(cells);
                    Shared[cells] = keys;
                }
                return keys;
            }
        }

        // SplitMix64
        private static ulong Next(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
