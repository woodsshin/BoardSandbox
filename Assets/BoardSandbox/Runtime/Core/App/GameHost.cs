using System;
using System.Collections.Generic;
using System.Diagnostics;
using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    public enum SeatMode
    {
        Human,
        Random
    }

    public sealed class GameHostOptions
    {
        public SessionOptions Session = new SessionOptions();

        // Pause between two moves of a computer-played seat.
        public double AiDelaySeconds = 0.4;

        // 0 draws the seeds of computer-played moves from the clock.
        public int AiSeed;

        // Seconds on a monotonic clock; defaults to one started with the host.
        public Func<double> Clock;

        public int ToastCapacity = 4;
        public double ToastSeconds = 3.5;
        public int RejectionLogCapacity = 200;
    }

    // Everything a front end needs around one running game: the chosen mod, the session, who plays each seat,
    // messages for the player, the console and background simulations. It knows nothing about any engine.
    public sealed class GameHost : IDisposable
    {
        private readonly GameHostOptions _options;
        private readonly Func<double> _clock;
        private readonly Dictionary<int, SeatMode> _seatModes = new Dictionary<int, SeatMode>();
        private readonly Random _aiRandom;
        private double _aiDue = -1;
        private bool _disposed;

        public GameHost(ModCatalog catalog, GameHostOptions options = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? new GameHostOptions();
            if (_options.Clock != null)
            {
                _clock = _options.Clock;
            }
            else
            {
                Stopwatch watch = Stopwatch.StartNew();
                _clock = () => watch.Elapsed.TotalSeconds;
            }
            _aiRandom = _options.AiSeed != 0 ? new Random(_options.AiSeed) : new Random();
            AiDelaySeconds = _options.AiDelaySeconds;
            Toasts = new ToastQueue(_options.ToastCapacity, _options.ToastSeconds);
            Rejections = new RejectionLog(_options.RejectionLogCapacity);
            Simulation = new SimulationJob();
            NewGameOptions = JsonValue.NewObject();
        }

        public ModCatalog Catalog { get; }
        public GameSession Session { get; private set; }
        public ModPackage Mod => Session?.Mod;
        public CommandConsole Console { get; private set; }
        public RejectionLog Rejections { get; }
        public ToastQueue Toasts { get; }
        public SimulationJob Simulation { get; }

        // Options for the next game: the mod's defaults, changed by the front end.
        public JsonValue NewGameOptions { get; private set; }

        public double AiDelaySeconds { get; set; }
        public bool AiPaused { get; set; }
        public double Now => _clock();

        // Raised after the session was replaced; the table of the previous session is gone.
        public event Action SessionReplaced;

        // Raised right before a game starts with the options it is about to get, which a listener may change,
        // and right after it has started.
        public event Action<JsonValue> GameStarting;
        public event Action GameStarted;

        public SessionOptions SessionOptions => _options.Session;

        public bool SelectMod(string id)
        {
            ModPackage mod = Catalog.Find(id);
            if (mod == null)
            {
                Toasts.Add("There is no mod '" + id + "'.", ToastLevel.Error, Now);
                return false;
            }
            return SelectMod(mod);
        }

        public bool SelectMod(ModPackage mod)
        {
            ThrowIfDisposed();
            if (mod == null)
                throw new ArgumentNullException(nameof(mod));
            GameSession fresh;
            try
            {
                fresh = new GameSession(mod, _options.Session);
            }
            catch (Exception e) when (e is LuauException || e is GameSessionException || e is ModException)
            {
                Toasts.Add("Could not load '" + mod.Manifest.Name + "': " + e.Message, ToastLevel.Error, Now);
                return false;
            }

            Rejections.Clear();
            fresh.AddInterceptor(Rejections);

            GameSession previous = Session;
            Session = fresh;
            Console = new CommandConsole(fresh);
            NewGameOptions = mod.Manifest.DefaultOptions.DeepClone();
            _seatModes.Clear();
            _aiDue = -1;
            SessionReplaced?.Invoke();
            previous?.Dispose();
            return true;
        }

        // Selects the mod and starts a game of it with the mod's default options.
        public bool StartGame(ModPackage mod)
        {
            return SelectMod(mod) && StartNewGame();
        }

        // Whether the game in progress is this mod in this version. A mod that was edited on disk and loaded again
        // is a different version.
        public bool IsRunning(ModPackage mod)
        {
            GameSession s = Session;
            return s != null && s.IsStarted && mod != null
                && string.Equals(s.Mod.Manifest.Id, mod.Manifest.Id, StringComparison.Ordinal)
                && string.Equals(s.Mod.ContentHash, mod.ContentHash, StringComparison.Ordinal);
        }

        public void SetOption(string key, JsonValue value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("an option needs a name", nameof(key));
            NewGameOptions.Set(key, value);
        }

        public bool StartNewGame()
        {
            ThrowIfDisposed();
            if (Session == null)
                return false;
            try
            {
                GameStarting?.Invoke(NewGameOptions);
                Session.NewGame(NewGameOptions);
            }
            catch (Exception e) when (e is GameSessionException || e is LuauException)
            {
                Toasts.Add("Could not start the game: " + Describe(e), ToastLevel.Error, Now);
                return false;
            }
            _aiDue = -1;
            GameStarted?.Invoke();
            return true;
        }

        public SubmitResult Submit(int seat, JsonValue command, string source = "human")
        {
            ThrowIfDisposed();
            if (Session == null)
                return SubmitResult.Invalid("NO_MOD", "no mod is selected");
            SubmitResult result = Session.Submit(new InputPacket(seat, command, source));
            Report(result);
            return result;
        }

        public bool Undo(int steps = 1)
        {
            return Navigate(() => Session.Undo(steps));
        }

        public bool Redo(int steps = 1)
        {
            return Navigate(() => Session.Redo(steps));
        }

        public bool JumpTo(int index)
        {
            return Navigate(() => Session.JumpTo(index));
        }

        private bool Navigate(Func<bool> move)
        {
            ThrowIfDisposed();
            if (Session == null)
                return false;
            try
            {
                bool moved = move();
                if (moved)
                    _aiDue = -1;
                return moved;
            }
            catch (Exception e) when (e is GameSessionException || e is LuauException)
            {
                Toasts.Add("Could not move in the history: " + Describe(e), ToastLevel.Error, Now);
                return false;
            }
        }

        public SeatMode GetSeatMode(int seat)
        {
            return _seatModes.TryGetValue(seat, out SeatMode mode) ? mode : SeatMode.Human;
        }

        public bool SetSeatMode(int seat, SeatMode mode)
        {
            ThrowIfDisposed();
            if (Session == null || seat < 1 || seat > Session.Mod.Manifest.MaxPlayers)
                return false;
            if (mode == SeatMode.Random && !Session.Capabilities.Random)
            {
                Toasts.Add("'" + Session.Mod.Manifest.Name + "' has no computer player.", ToastLevel.Warning, Now);
                return false;
            }
            if (mode == SeatMode.Human)
                _seatModes.Remove(seat);
            else
                _seatModes[seat] = mode;
            _aiDue = -1;
            return true;
        }

        // Whether a person should be able to place pieces for the seat on turn right now.
        public bool AcceptsHumanInput
        {
            get
            {
                GameSession s = Session;
                return s != null && s.IsStarted && !s.View.IsOver && s.View.Turn > 0 && GetSeatMode(s.View.Turn) == SeatMode.Human;
            }
        }

        // Plays the computer's move when it is due; call it once per frame.
        public bool Update()
        {
            if (_disposed)
                return false;
            GameSession s = Session;
            if (s == null || !s.IsStarted || s.View.IsOver || AiPaused)
            {
                _aiDue = -1;
                return false;
            }
            int seat = s.View.Turn;
            if (seat <= 0 || GetSeatMode(seat) != SeatMode.Random)
            {
                _aiDue = -1;
                return false;
            }

            double now = Now;
            if (_aiDue < 0)
            {
                _aiDue = now + Math.Max(0, AiDelaySeconds);
                return false;
            }
            if (now < _aiDue)
                return false;
            _aiDue = -1;

            JsonValue command;
            try
            {
                command = s.RandomCommand(seat, _aiRandom.Next(1, int.MaxValue));
            }
            catch (Exception e) when (e is GameSessionException || e is LuauException)
            {
                Toasts.Add("The computer player failed: " + Describe(e), ToastLevel.Error, now);
                _seatModes.Remove(seat);
                return false;
            }
            if (command == null || !command.IsObject)
            {
                Toasts.Add("The computer has no move for seat " + seat + ".", ToastLevel.Warning, now);
                _seatModes.Remove(seat);
                return false;
            }

            SubmitResult result = Session.Submit(new InputPacket(seat, command, "ai"));
            Report(result);
            if (!result.IsAccepted)
                _seatModes.Remove(seat);
            return result.IsAccepted;
        }

        private void Report(SubmitResult result)
        {
            switch (result.Status)
            {
                case SubmitStatus.Accepted:
                    return;
                case SubmitStatus.Faulted:
                    Toasts.Add("Script error " + result.Code + ": " + result.Message + (result.Recovered ? " (the game was restored)" : " (the game could not be restored)"),
                        ToastLevel.Error, Now);
                    return;
                default:
                    string text = string.IsNullOrEmpty(result.Message) ? result.Code : result.Message;
                    Toasts.Add(text + " [" + result.Code + "]", ToastLevel.Warning, Now);
                    return;
            }
        }

        private static string Describe(Exception e)
        {
            GameSessionException session = e as GameSessionException;
            return session != null ? session.Code + ": " + session.Detail : e.Message;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameHost));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Simulation.Cancel();
            Simulation.Wait(3000);
            Session?.Dispose();
            Session = null;
        }
    }
}
