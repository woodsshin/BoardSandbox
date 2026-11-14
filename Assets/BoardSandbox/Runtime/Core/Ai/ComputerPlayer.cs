using System;
using System.Collections.Generic;
using System.Threading;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Ai
{
    // A computer player that thinks instead of playing at random. A mod asks for one by name in config.json:
    //
    //     "ai": { "engine": "gomoku_mcts", "levels": [100, 500, 2000] }
    //
    // "levels" is the effort the engine may spend on a move, growing with the games played against it; what the
    // number means is up to the engine.
    public interface IComputerPlayer
    {
        string Engine { get; }

        // Reads the position from the session on the calling thread. The returned search touches neither the session
        // nor the VM, so it may run on another thread; it gives the command to submit for the seat. Throws
        // GameSessionException when the game is not one the engine can play.
        Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed);
    }

    public static class ComputerPlayers
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Func<IComputerPlayer>> Factories =
            new Dictionary<string, Func<IComputerPlayer>>(StringComparer.Ordinal)
            {
                { GomokuMctsPlayer.EngineName, () => new GomokuMctsPlayer() }
            };

        // Adds an engine, or replaces the one of that name.
        public static void Register(string engine, Func<IComputerPlayer> factory)
        {
            if (string.IsNullOrEmpty(engine))
                throw new ArgumentException("an engine needs a name", nameof(engine));
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            lock (Gate)
                Factories[engine] = factory;
        }

        public static bool Has(string engine)
        {
            lock (Gate)
                return engine != null && Factories.ContainsKey(engine);
        }

        // A new player of the engine the mod names; null when it names none or one that is not known here.
        public static IComputerPlayer Create(ModManifest manifest)
        {
            if (manifest == null || manifest.AiEngine.Length == 0)
                return null;
            Func<IComputerPlayer> factory;
            lock (Gate)
            {
                if (!Factories.TryGetValue(manifest.AiEngine, out factory))
                    return null;
            }
            return factory();
        }
    }

    // Gomoku by Monte Carlo tree search; the effort is the number of simulations per move. It reads the game through
    // rules.query("state"), which must give size, win_length, exact_win, current and board as the gomoku mod does,
    // and answers with { action = "place_stone", x, y }.
    public sealed class GomokuMctsPlayer : IComputerPlayer
    {
        public const string EngineName = "gomoku_mcts";

        // The tree of the last search, kept for the next move of the game. A search takes it out while it runs, so
        // two searches never share it.
        private GomokuMcts _idle = new GomokuMcts();

        public string Engine => EngineName;

        // What the last finished search found; null before the first.
        public GomokuSearchResult LastResult { get; private set; }

        public Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (!session.Capabilities.Query)
                throw new GameSessionException("NOT_SUPPORTED", "the engine '" + EngineName + "' needs rules.query(\"state\")");
            GomokuPosition position = ReadPosition(session.Query("state"));
            if (position.Current != seat)
                throw new GameSessionException("NOT_YOUR_TURN", "seat " + seat + " is not to move");
            if (position.IsFull)
                throw new GameSessionException("GAME_OVER", "the board is full");

            return cancel =>
            {
                GomokuMcts search = Interlocked.Exchange(ref _idle, null) ?? new GomokuMcts();
                GomokuSearchResult result = search.Search(position, effort, new Random(seed), cancel);
                LastResult = result;
                Interlocked.Exchange(ref _idle, search);
                return JsonValue.NewObject()
                    .Set("action", "place_stone")
                    .Set("x", result.Move % position.Size + 1)
                    .Set("y", result.Move / position.Size + 1);
            };
        }

        public static GomokuPosition ReadPosition(JsonValue state)
        {
            if (state == null || !state.IsObject)
                throw new GameSessionException("NOT_SUPPORTED", "rules.query(\"state\") gave no position");
            int size = state["size"].AsInt(0);
            int winLength = state["win_length"].AsInt(0);
            if (size < GomokuPosition.MinSize || size > GomokuPosition.MaxSize || winLength < 2 || winLength > size)
                throw new GameSessionException("NOT_SUPPORTED", "the engine '" + EngineName + "' cannot play a board of this size");
            try
            {
                return GomokuPosition.Parse(size, winLength, state["exact_win"].AsBool(), state["board"].AsString(null), state["current"].AsInt(0));
            }
            catch (FormatException e)
            {
                throw new GameSessionException("NOT_SUPPORTED", e.Message, e);
            }
        }
    }
}
