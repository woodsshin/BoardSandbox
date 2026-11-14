using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BoardSandbox.Core.Ai;
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

        // The computer plays a random legal move.
        Random,

        // The computer plays the move its engine finds (ModManifest.AiEngine), thinking harder with every game.
        Ai
    }

    public sealed class GameHostOptions
    {
        public SessionOptions Session = new SessionOptions();

        // Pause between two moves of a computer-played seat.
        public double AiDelaySeconds = 0.4;

        // 0 draws the seeds of computer-played moves from the clock.
        public int AiSeed;

        // The thinking computer player searches on a worker thread while the game stays responsive. False runs the
        // search inside Update when the move is due.
        public bool AiThreaded = true;

        // Games finished against the thinking computer player, which set its level. Null keeps them in memory.
        public AiProgress AiProgress;

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
        private IComputerPlayer _player;
        private Task<JsonValue> _aiTask;
        private CancellationTokenSource _aiCancel;
        private Func<CancellationToken, JsonValue> _aiSearch;
        private bool _gameCounted;
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
            AiProgress = _options.AiProgress ?? new AiProgress();
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

        public AiProgress AiProgress { get; }

        // Whether the mod has a computer player that thinks (SeatMode.Ai).
        public bool HasAi => _player != null;

        // The levels of that player and the one it plays at now, counted from 1; 0 without such a player.
        public int AiLevelCount => _player != null ? Session.Mod.Manifest.AiLevels.Count : 0;
        public int AiLevel => _player != null ? AiProgress.LevelIndex(Session.Mod.Manifest.Id, AiLevelCount) + 1 : 0;

        // What the player may spend on a move at this level; for gomoku the number of simulations.
        public int AiEffort => _player != null ? Session.Mod.Manifest.AiLevels[AiLevel - 1] : 0;

        // A search for the computer's move is under way.
        public bool AiThinking => _aiTask != null || _aiSearch != null;
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
            fresh.EventApplied += OnSessionEvent;
            fresh.CommandProcessed += OnCommandProcessed;

            GameSession previous = Session;
            Session = fresh;
            Console = new CommandConsole(fresh);
            NewGameOptions = mod.Manifest.DefaultOptions.DeepClone();
            _seatModes.Clear();
            ResetAi();
            _player = ComputerPlayers.Create(mod.Manifest);
            _gameCounted = false;
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
            ResetAi();
            _gameCounted = false;
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
                    ResetAi();
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
            if (mode == SeatMode.Ai && _player == null)
            {
                Toasts.Add("'" + Session.Mod.Manifest.Name + "' has no thinking computer player.", ToastLevel.Warning, Now);
                return false;
            }
            if (mode == SeatMode.Human)
                _seatModes.Remove(seat);
            else
                _seatModes[seat] = mode;
            ResetAi();
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
                ResetAi();
                return false;
            }
            int seat = s.View.Turn;
            SeatMode mode = seat > 0 ? GetSeatMode(seat) : SeatMode.Human;
            if (mode == SeatMode.Human)
            {
                ResetAi();
                return false;
            }
            if (mode == SeatMode.Ai && _aiDue >= 0 && !AiThinking)
            {
                // The game changed under the search (a console command, say), so its move is for another position.
                _aiDue = -1;
            }

            double now = Now;
            if (_aiDue < 0)
            {
                _aiDue = now + Math.Max(0, AiDelaySeconds);
                if (mode == SeatMode.Ai)
                    BeginThinking(s, seat, now);
                return false;
            }
            if (now < _aiDue)
                return false;

            JsonValue command;
            if (mode == SeatMode.Ai)
            {
                if (_aiTask != null && !_aiTask.IsCompleted)
                    return false;
                try
                {
                    command = _aiTask != null ? _aiTask.GetAwaiter().GetResult() : _aiSearch(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    ResetAi();
                    return false;
                }
                catch (Exception e)
                {
                    FailComputer(seat, e, now);
                    return false;
                }
                ResetAi();
            }
            else
            {
                _aiDue = -1;
                try
                {
                    command = s.RandomCommand(seat, _aiRandom.Next(1, int.MaxValue));
                }
                catch (Exception e) when (e is GameSessionException || e is LuauException)
                {
                    FailComputer(seat, e, now);
                    return false;
                }
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

        // Reads the position now and starts the search for the seat's move, which Update picks up when it is due.
        private void BeginThinking(GameSession session, int seat, double now)
        {
            Func<CancellationToken, JsonValue> search;
            try
            {
                search = _player.Prepare(session, seat, AiEffort, _aiRandom.Next(1, int.MaxValue));
            }
            catch (Exception e) when (e is GameSessionException || e is LuauException)
            {
                FailComputer(seat, e, now);
                return;
            }
            if (_options.AiThreaded)
            {
                _aiCancel = new CancellationTokenSource();
                CancellationToken token = _aiCancel.Token;
                _aiTask = Task.Run(() => search(token), token);
            }
            else
            {
                _aiSearch = search;
            }
        }

        private void FailComputer(int seat, Exception e, double now)
        {
            Toasts.Add("The computer player failed: " + Describe(e), ToastLevel.Error, now);
            _seatModes.Remove(seat);
            ResetAi();
        }

        // Forgets the move that is scheduled or being thought about.
        private void ResetAi()
        {
            _aiDue = -1;
            StopThinking();
        }

        private void StopThinking()
        {
            _aiSearch = null;
            _aiTask = null;
            if (_aiCancel != null)
            {
                // The search notices at its next simulation and its result is dropped.
                _aiCancel.Cancel();
                _aiCancel = null;
            }
        }

        private void OnSessionEvent(ViewEvent applied)
        {
            // Whatever the search was started for, the table is no longer that.
            StopThinking();
        }

        // Counts a finished game between a person and the thinking computer player; the next one is a level harder.
        private void OnCommandProcessed(InputPacket packet, SubmitResult result)
        {
            GameSession s = Session;
            if (_gameCounted || _player == null || s == null || !result.IsAccepted || !s.View.IsOver)
                return;
            bool computer = false;
            bool person = false;
            for (int seat = 1; seat <= s.Mod.Manifest.MaxPlayers; seat++)
            {
                SeatMode mode = GetSeatMode(seat);
                computer |= mode == SeatMode.Ai;
                person |= mode == SeatMode.Human;
            }
            if (!computer || !person)
                return;
            _gameCounted = true;
            int before = AiLevel;
            AiProgress.RecordGame(s.Mod.Manifest.Id);
            if (AiProgress.Problem.Length > 0)
                Toasts.Add(AiProgress.Problem, ToastLevel.Warning, Now);
            if (AiLevel != before)
                Toasts.Add("The computer will think harder next game (level " + AiLevel + " of " + AiLevelCount + ").", ToastLevel.Info, Now);
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
            StopThinking();
            Simulation.Cancel();
            Simulation.Wait(3000);
            Session?.Dispose();
            Session = null;
        }
    }
}
