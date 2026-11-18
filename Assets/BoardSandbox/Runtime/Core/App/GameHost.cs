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

        // The computer plays the move the mod's own player finds (rules.query("think"), or the engine named by
        // ModManifest.AiEngine), thinking harder with every game.
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

        // How the player set up their last games, which the next game of the same mod starts with. Where nobody chose yet,
        // the person at this computer plays seat 1 and the computer plays the other seats of a game that has a computer
        // player. Null leaves every seat to people and remembers nothing.
        public PlayPreferences Preferences;

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
        private int _lastComputerSeat;
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

        // How the player set up their last games; null when they are not kept.
        public PlayPreferences Preferences => _options.Preferences;

        // Whether the mod has a computer player that thinks (SeatMode.Ai).
        public bool HasAi => _player != null;

        // The mode that gives a seat to the computer: the thinking player when the mod has one, random moves otherwise.
        public SeatMode ComputerMode => HasAi ? SeatMode.Ai : SeatMode.Random;

        // Whether a seat of the game on the table may be given to the computer.
        public bool CanPlayComputer
        {
            get
            {
                GameSession s = Session;
                return s != null && s.Mod.Manifest.AllowComputer && (HasAi || s.Capabilities.Random);
            }
        }

        // How many seats the game on the table plays with: what its options say, else all of them.
        public int SeatCount
        {
            get
            {
                GameSession s = Session;
                if (s == null)
                    return 0;
                return s.Mod.Manifest.SeatCount(s.IsStarted ? s.GameOptions : NewGameOptions);
            }
        }

        // The levels of that player and the one it plays at now, counted from 1; 0 without such a player.
        public int AiLevelCount => _player != null ? Session.Mod.Manifest.AiLevels.Count : 0;
        public int AiLevel => _player != null ? AiProgress.LevelIndex(Session.Mod.Manifest.Id, AiLevelCount) + 1 : 0;

        // What the player may spend on a move at this level; what the number means is up to the mod.
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
            _lastComputerSeat = 0;
            ResetAi();
            (_player as IDisposable)?.Dispose();
            _player = ComputerPlayers.Create(mod.Manifest);
            SeatPlayers(mod.Manifest);
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
            _lastComputerSeat = 0;
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
            if (mode != SeatMode.Human && !Session.Mod.Manifest.AllowComputer)
            {
                Toasts.Add("'" + Session.Mod.Manifest.Name + "' is not played against the computer.", ToastLevel.Warning, Now);
                return false;
            }
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

        // SetSeatMode for a choice the player made: the next game of the mod starts with the seats set the same way. Seat 1
        // is the player's own and is not kept.
        public bool ChooseSeatMode(int seat, SeatMode mode)
        {
            if (!SetSeatMode(seat, mode))
                return false;
            RememberSeats();
            return true;
        }

        // Who plays the seats of the game that was just chosen. Seat 1 is the person's. The others are as the player left
        // them the last time and, where they never chose, go to the computer when the game has a computer player that
        // thinks.
        private void SeatPlayers(ModManifest manifest)
        {
            PlayPreferences preferences = _options.Preferences;
            if (preferences == null || !CanPlayComputer)
                return;
            bool byDefault = HasAi && manifest.HasComputerOpponent;
            for (int seat = 2; seat <= manifest.MaxPlayers; seat++)
            {
                if (preferences.ComputerPlays(manifest.Id, seat, byDefault))
                    _seatModes[seat] = ComputerMode;
            }
        }

        private void RememberSeats()
        {
            PlayPreferences preferences = _options.Preferences;
            GameSession s = Session;
            if (preferences == null || s == null)
                return;
            ModManifest manifest = s.Mod.Manifest;
            bool[] computer = new bool[manifest.MaxPlayers];
            for (int seat = 1; seat <= computer.Length; seat++)
                computer[seat - 1] = GetSeatMode(seat) != SeatMode.Human;
            preferences.RememberSeats(manifest.Id, computer);
            if (preferences.Problem.Length > 0)
                Toasts.Add(preferences.Problem, ToastLevel.Warning, Now);
        }

        // The seat a person at this computer plays: the seat on turn, or, in a cooperative game, which has no turns, the
        // first seat the computer does not play. 0 when there is none.
        public int LocalSeat
        {
            get
            {
                GameSession s = Session;
                if (s == null || !s.IsStarted)
                    return 0;
                if (!s.Mod.Manifest.Cooperative)
                    return s.View.Turn;
                for (int seat = 1; seat <= s.Mod.Manifest.MaxPlayers; seat++)
                {
                    if (GetSeatMode(seat) == SeatMode.Human)
                        return seat;
                }
                return 0;
            }
        }

        // Whether a person should be able to place pieces right now: for the seat on turn, or in a cooperative game for
        // any seat that is not played by the computer.
        public bool AcceptsHumanInput
        {
            get
            {
                GameSession s = Session;
                if (s == null || !s.IsStarted || s.View.IsOver)
                    return false;
                if (s.Mod.Manifest.Cooperative)
                    return LocalSeat > 0;
                return s.View.Turn > 0 && GetSeatMode(s.View.Turn) == SeatMode.Human;
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
            int seat = ComputerSeat(s);
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
            _lastComputerSeat = seat;
            if (!result.IsAccepted)
                _seatModes.Remove(seat);
            return result.IsAccepted;
        }

        // The seat the computer is to play now: the seat on turn, or, in a cooperative game, the next seat the computer
        // plays after the one that played last; 0 when it has none.
        private int ComputerSeat(GameSession s)
        {
            if (!s.Mod.Manifest.Cooperative)
                return s.View.Turn;
            int count = s.Mod.Manifest.MaxPlayers;
            for (int i = 0; i < count; i++)
            {
                int seat = (_lastComputerSeat + i) % count + 1;
                if (GetSeatMode(seat) != SeatMode.Human)
                    return seat;
            }
            return 0;
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
                // The result of the search is dropped; a search that has not started yet does not start.
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
            // Whatever the search was started for, the game is no longer that, also when the command showed nothing.
            if (result.IsAccepted)
                StopThinking();
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
            (_player as IDisposable)?.Dispose();
            _player = null;
            Simulation.Cancel();
            Simulation.Wait(3000);
            Session?.Dispose();
            Session = null;
        }
    }
}
