using System;
using System.Collections.Generic;
using System.Threading;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Ai
{
    // A computer player that thinks instead of playing at random. A mod asks for one in config.json:
    //
    //     "ai": { "levels": [60, 600, 3000] }
    //
    // "levels" is the effort the player may spend on a move, growing with the games played against it; what the
    // number means is up to whoever thinks. Without "engine" that is the mod itself, in Luau (LuauComputerPlayer).
    public interface IComputerPlayer
    {
        string Engine { get; }

        // Reads the position from the session on the calling thread. The returned search touches neither the session
        // nor its VM, so it may run on another thread; it gives the command to submit for the seat. Throws
        // GameSessionException when the game is not one the engine can play.
        Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed);
    }

    public static class ComputerPlayers
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Func<IComputerPlayer>> Factories =
            new Dictionary<string, Func<IComputerPlayer>>(StringComparer.Ordinal)
            {
                { LuauComputerPlayer.EngineName, () => new LuauComputerPlayer() }
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

    // The computer player a mod writes itself. The engine knows no game: it asks the rules of the mod for the move,
    //
    //     rules.query("think", { seat = 2, effort = 600, seed = 12345 })  -->  { action = "place_stone", x = 8, y = 8 }
    //
    // and submits what comes back like any other input. The question is put to a second VM of the mod that was brought
    // to the position with rules.restore, so the game stays responsive while the script thinks and the script may take
    // far longer than a command (Vm.CallTimeoutMs). The mod therefore needs rules.snapshot, rules.restore and
    // rules.query. The VM is kept for the next move, so whatever the script remembers between two moves stays.
    public sealed class LuauComputerPlayer : IComputerPlayer, IDisposable
    {
        public const string EngineName = "luau";

        // The name of the query that asks for the move.
        public const string Query = "think";

        // The VM of the last search. A search takes it out while it runs, so two searches never share one.
        private readonly object _gate = new object();
        private LuauVm _idle;
        private ModPackage _mod;
        private bool _disposed;

        public string Engine => EngineName;

        // The limits of the VM that thinks; changes apply to the next VM that is made.
        public VmOptions Vm { get; set; } = new VmOptions { MemoryLimitMb = 256, CallTimeoutMs = 30000 };

        public Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (!session.Capabilities.Query || !session.Capabilities.Snapshot)
            {
                throw new GameSessionException("NOT_SUPPORTED",
                    "a computer player in Luau needs rules.query(\"" + Query + "\"), rules.snapshot and rules.restore");
            }
            if (session.View.IsOver)
                throw new GameSessionException("GAME_OVER", "the game is over");
            string snapshot = session.CaptureSnapshot();
            ModPackage mod = session.Mod;
            VmOptions options = Vm.Clone();
            JsonValue restore = JsonValue.NewObject().Set("snapshot", snapshot);
            JsonValue think = JsonValue.NewObject()
                .Set("name", Query)
                .Set("args", JsonValue.NewObject().Set("seat", seat).Set("effort", effort).Set("seed", seed));

            return cancel =>
            {
                cancel.ThrowIfCancellationRequested();
                LuauVm vm = Take(mod, options);
                bool healthy = false;
                try
                {
                    Ask(vm, "restore", restore);
                    cancel.ThrowIfCancellationRequested();
                    JsonValue command = Ask(vm, "query", think)["value"];
                    healthy = true;
                    return command.IsObject ? command : null;
                }
                finally
                {
                    // A VM that failed may be anywhere; the next search starts with a new one.
                    if (!healthy || !Return(mod, vm))
                        vm.Dispose();
                }
            };
        }

        private LuauVm Take(ModPackage mod, VmOptions options)
        {
            LuauVm vm;
            lock (_gate)
            {
                vm = _idle;
                _idle = null;
                if (vm != null && !ReferenceEquals(_mod, mod))
                {
                    vm.Dispose();
                    vm = null;
                }
            }
            return vm ?? mod.CreateVm(options);
        }

        private bool Return(ModPackage mod, LuauVm vm)
        {
            lock (_gate)
            {
                if (_disposed || _idle != null)
                    return false;
                _idle = vm;
                _mod = mod;
                return true;
            }
        }

        // Gives the idle VM back. A search that is still running finishes and disposes its own.
        public void Dispose()
        {
            LuauVm vm;
            lock (_gate)
            {
                _disposed = true;
                vm = _idle;
                _idle = null;
                _mod = null;
            }
            vm?.Dispose();
        }

        private static JsonValue Ask(LuauVm vm, string method, JsonValue args)
        {
            JsonValue response = vm.CallJson(method, args);
            vm.TakeLogs();
            if (response["ok"].AsBool())
                return response;
            JsonValue error = response["error"];
            throw new GameSessionException(error["code"].AsString("SCRIPT_ERROR"), error["message"].AsString("the script failed"));
        }
    }
}
