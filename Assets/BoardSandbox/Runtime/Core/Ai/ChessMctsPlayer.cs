using System;
using System.Collections.Generic;
using System.Threading;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Ai
{
    // Chess by Monte Carlo tree search; the effort is the number of simulations per move. It reads the game through
    // rules.query("state"), which must give fen, turn, plies and finished as the chess mod does, and answers with
    // { action = "move", uci = "e2e4" }.
    public sealed class ChessMctsPlayer : IComputerPlayer
    {
        public const string EngineName = "chess_mcts";

        // The tree of the last search, kept for the next move of the game. A search takes it out while it runs, so
        // two searches never share it.
        private ChessMcts _idle = new ChessMcts();

        // The positions this player has seen in the game, so that it does not walk into a draw by repetition when
        // it stands better. The mod does not report the earlier positions, hence the player's own record.
        private readonly List<ulong> _seen = new List<ulong>();
        private readonly object _gate = new object();
        private int _plies = -1;

        public string Engine => EngineName;

        // What the last finished search found; null before the first.
        public ChessSearchResult LastResult { get; private set; }

        public Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (!session.Capabilities.Query)
                throw new GameSessionException("NOT_SUPPORTED", "the engine '" + EngineName + "' needs rules.query(\"state\")");
            JsonValue state = session.Query("state");
            ChessPosition position = ReadPosition(state);
            if (state["finished"].AsBool())
                throw new GameSessionException("GAME_OVER", "the game is over");
            if (position.Side + 1 != seat)
                throw new GameSessionException("NOT_YOUR_TURN", "seat " + seat + " is not to move");
            int halfMoveLimit = (session.GameOptions ?? JsonValue.NewObject())["halfmove_limit"].AsInt(100);

            ulong[] played;
            lock (_gate)
            {
                // Fewer plies than last time: a new game or moves taken back, and the record no longer holds.
                int plies = state["plies"].AsInt(0);
                if (plies <= _plies)
                    _seen.Clear();
                _plies = plies;
                _seen.Add(position.Hash);
                played = _seen.ToArray();
            }

            return cancel =>
            {
                ChessMcts search = Interlocked.Exchange(ref _idle, null) ?? new ChessMcts();
                search.HalfMoveLimit = halfMoveLimit;
                ChessSearchResult result = search.Search(position, effort, new Random(seed), played, cancel);
                LastResult = result;
                Interlocked.Exchange(ref _idle, search);

                position.Make(result.Move);
                lock (_gate)
                    _seen.Add(position.Hash);
                return JsonValue.NewObject().Set("action", "move").Set("uci", result.Uci);
            };
        }

        public static ChessPosition ReadPosition(JsonValue state)
        {
            if (state == null || !state.IsObject || state["fen"].AsString(null) == null)
                throw new GameSessionException("NOT_SUPPORTED", "rules.query(\"state\") gave no position");
            try
            {
                return new ChessPosition(state["fen"].AsString());
            }
            catch (FormatException e)
            {
                throw new GameSessionException("NOT_SUPPORTED", e.Message, e);
            }
        }
    }
}
