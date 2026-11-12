using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Net;
using BoardSandbox.Core.Sim;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The immediate-mode interface around the board. Every change of game state is deferred to RunDeferred, which
    // the owner calls outside of OnGUI, so that the controls drawn in one frame are the same in every GUI pass.
    public sealed class Hud
    {
        private const string ConsoleField = "bse-console-input";
        private const int MaxLogLines = 400;
        private const int MaxLogCharacters = 12000;
        private const int MaxRecalled = 100;
        private const float Margin = 12f;

        private static readonly string[] SeatModeNames = { "Human", "Computer" };

        private readonly GameHost _host;
        private readonly Multiplayer _multiplayer;
        private readonly GuiTheme _theme;
        private readonly GameLibraryPanel _library;
        private readonly Action _fitView;
        private readonly Queue<Action> _deferred = new Queue<Action>();
        private readonly List<Rect> _rects = new List<Rect>();
        private readonly List<string> _lines = new List<string>();
        private readonly List<string> _recalled = new List<string>();
        private readonly List<Toast> _toasts = new List<Toast>();
        private readonly Dictionary<string, string> _optionText = new Dictionary<string, string>();

        private Vector2 _origin;
        private string _logText = string.Empty;
        private Vector2 _logScroll;
        private string _input = string.Empty;
        private int _recallIndex;
        private bool _focusInput;
        private bool _consoleFocused;
        private bool _releaseFocus;
        private bool _showConsole;
        private bool _showSimulation;
        private bool _showOptions = true;
        private string _simGames = "1000";
        private string _simSeed = "1";

        private double _now;
        private string _status = string.Empty;
        private bool _hasSwatch;
        private Color _swatch = Color.white;
        private bool _simRunning;
        private bool _simWasRunning;
        private SimulationProgress _simProgress;
        private SimulationResult _simResult;

        public Hud(GameHost host, Multiplayer multiplayer, GuiTheme theme, Action fitView)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _multiplayer = multiplayer ?? throw new ArgumentNullException(nameof(multiplayer));
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            _fitView = fitView;
            _library = new GameLibraryPanel(host, multiplayer, theme, Defer, () => _releaseFocus = true);
            _host.SessionReplaced += OnSessionReplaced;
            _multiplayer.RoomChanged += OnRoomChanged;
            _multiplayer.RoomLost += OpenLibrary;
        }

        // True while the panels are folded away so that they do not cover the table.
        public bool Collapsed { get; private set; }

        public void ToggleCollapsed()
        {
            Collapsed = !Collapsed;
            _releaseFocus = true;
        }

        // The room of a game that somebody else hosts; null when the local player hosts or plays alone.
        private NetRoom GuestRoom => _multiplayer.Room != null && !_multiplayer.Room.IsHost ? _multiplayer.Room : null;

        // Entering a room puts its game on the table, so the list of games gets out of the way.
        private void OnRoomChanged()
        {
            if (_multiplayer.Room != null && _library.IsOpen)
                _library.Close();
        }

        // True while a text field has the keyboard, so that shortcuts leave the typed characters alone.
        public bool TextFocused => GUIUtility.keyboardControl != 0;

        public bool ConsoleOpen => _showConsole;

        // True while the list of games covers the screen; the board and the keyboard shortcuts of the app are idle then.
        public bool ModalOpen => _library.IsOpen || _multiplayer.Loading != null;

        // A line of explanation under the title of the game; empty for none.
        public string Note { get; set; } = string.Empty;

        public bool Contains(Vector2 guiPoint)
        {
            foreach (Rect rect in _rects)
            {
                if (rect.Contains(guiPoint))
                    return true;
            }
            return false;
        }

        // Opens the list of games. Call it between two frames, not from inside OnGUI.
        public void OpenLibrary()
        {
            _library.Open();
            _rects.Clear();
            _rects.Add(new Rect(0f, 0f, Width, Height));
        }

        public void Defer(Action action)
        {
            _deferred.Enqueue(action);
        }

        public void RunDeferred()
        {
            int count = _deferred.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    _deferred.Dequeue()();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    _host.Toasts.Add("Unexpected error: " + e.Message, ToastLevel.Error, _host.Now);
                }
            }
        }

        public void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            foreach (string line in text.Split('\n'))
                _lines.Add(line.TrimEnd('\r'));
            if (_lines.Count > MaxLogLines)
                _lines.RemoveRange(0, _lines.Count - MaxLogLines);
            RebuildLog();
        }

        // Samples everything that changes behind the interface's back; call it once per frame before OnGUI.
        public void Snapshot(double now)
        {
            _now = now;
            _toasts.Clear();
            _toasts.AddRange(_host.Toasts.Active(now));

            _simRunning = _host.Simulation.IsRunning;
            _simProgress = _host.Simulation.Progress;
            _simResult = _host.Simulation.LastResult;
            if (_simWasRunning && !_simRunning && _simResult != null)
                _host.Toasts.Add("Simulation finished: " + _simResult, _simResult.Succeeded ? ToastLevel.Info : ToastLevel.Warning, now);
            _simWasRunning = _simRunning;

            SampleStatus();
        }

        public void Draw()
        {
            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                _rects.Clear();
                if (_releaseFocus)
                {
                    GUI.FocusControl(null);
                    _releaseFocus = false;
                }
            }

            if (_library.IsOpen)
            {
                if (e.type == EventType.Repaint)
                    _rects.Add(new Rect(0f, 0f, Width, Height));
                _library.Draw();
                DrawToasts();
                return;
            }

            string loading = _multiplayer.Loading;
            if (loading != null)
            {
                DrawLoading(loading);
                DrawToasts();
                return;
            }

            HandleKeys(e);
            DrawBanner();
            if (Collapsed)
            {
                DrawCollapsed();
            }
            else if (GuestRoom != null)
            {
                DrawGuestPanel(GuestRoom);
            }
            else
            {
                DrawGamePanel();
                DrawToolColumn();
                DrawConsole();
            }
            DrawToasts();
        }

        // Covers the table while a room is being opened or entered.
        private void DrawLoading(string text)
        {
            Rect screen = new Rect(0f, 0f, Width, Height);
            if (Event.current.type == EventType.Repaint)
                _rects.Add(screen);
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.DrawTexture(screen, Texture2D.whiteTexture);
            GUI.color = previous;

            float width = _theme.Sf(360);
            float height = _theme.Sf(150);
            GUILayout.BeginArea(new Rect((Width - width) * 0.5f, (Height - height) * 0.5f, width, height));
            GUILayout.BeginVertical(_theme.Panel);
            NetRoom room = _multiplayer.Room;
            GUILayout.Label(room != null ? DisplayText.Clip(room.Mod.Manifest.Name, 40) : "Online", _theme.Heading);
            int dots = (int)(_host.Now * 2.0) % 4;
            GUILayout.Label(text.TrimEnd('…') + new string('.', dots), _theme.Label);
            GUILayout.Space(_theme.Sf(10));
            GUI.enabled = room != null;
            if (GUILayout.Button("Cancel", _theme.Button))
                Defer(_multiplayer.Leave);
            GUI.enabled = true;
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawPanelHeader(string title)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, _theme.Heading, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Collapsed ? "Show (Esc)" : "Hide (Esc)", _theme.Button, GUILayout.ExpandWidth(false)))
                Defer(ToggleCollapsed);
            GUILayout.EndHorizontal();
        }

        // All that is left of the panels while they are folded away.
        private void DrawCollapsed()
        {
            NetRoom guest = GuestRoom;
            ModManifest manifest = guest != null ? guest.Mod.Manifest : _host.Session?.Mod.Manifest;
            float width = _theme.Sf(250);
            BeginArea(new Rect(Margin, Margin, width, Height - 2f * Margin));
            BeginBox(width);
            DrawPanelHeader(manifest != null ? DisplayText.Clip(manifest.Name, 18) : "Board Sandbox");
            EndBox();
            EndArea();
        }

        private void SampleStatus()
        {
            GameSession session = _host.Session;
            _hasSwatch = false;
            NetRoom room = _multiplayer.Room;
            NetRoom guest = GuestRoom;
            ViewModel view;
            ModManifest manifest;
            if (guest != null)
            {
                NetBoard board = guest.LocalBoard ?? (guest.Boards.Count > 0 ? guest.Boards[0] : null);
                if (board == null || board.Queue.Display.Turn <= 0 && !board.Queue.Display.IsOver)
                {
                    _status = board == null ? "Joining…" : "Waiting for the host to start";
                    return;
                }
                view = board.Queue.Display;
                manifest = guest.Mod.Manifest;
            }
            else
            {
                if (session == null)
                {
                    _status = _host.Catalog.Mods.Count == 0 ? "No games found" : "Pick a game";
                    return;
                }
                if (!session.IsStarted)
                {
                    _status = "Press New game";
                    return;
                }
                view = session.View;
                manifest = session.Mod.Manifest;
            }

            if (view.IsOver)
            {
                OverInfo over = view.Over;
                string reason = over.Reason.Length > 0 ? " - " + over.Reason.Replace('_', ' ') : string.Empty;
                if (over.Winner > 0)
                {
                    _status = SeatName(manifest, over.Winner) + " wins" + reason;
                    _hasSwatch = true;
                    _swatch = SeatColor(manifest, over.Winner);
                }
                else
                {
                    _status = "Draw" + reason;
                }
                return;
            }
            if (view.Turn > 0)
            {
                string who = room == null ? (_host.AcceptsHumanInput ? string.Empty : " (computer)")
                    : room.SplitScreen ? string.Empty
                    : room.CanAct ? " (you)" : guest == null && !_host.AcceptsHumanInput ? " (computer)" : string.Empty;
                _status = SeatName(manifest, view.Turn) + " to move" + who;
                _hasSwatch = true;
                _swatch = SeatColor(manifest, view.Turn);
                return;
            }
            _status = string.Empty;
        }

        private static string SeatName(ModManifest manifest, int seat)
        {
            SeatInfo info = manifest.GetSeat(seat);
            return info != null ? info.Name : "Seat " + seat;
        }

        private static Color SeatColor(ModManifest manifest, int seat)
        {
            SeatInfo info = manifest.GetSeat(seat);
            return info == null ? Color.gray : (Color)ColorParser.Parse(info.Color, new Color32(0x8a, 0x8a, 0x8a, 255));
        }

        private void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown)
                return;
            if (e.keyCode == KeyCode.BackQuote || e.keyCode == KeyCode.F1)
            {
                e.Use();
                Defer(ToggleConsole);
            }
            else if (e.character == '`')
            {
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                e.Use();
                if (_showConsole && !Collapsed && GuestRoom == null)
                    Defer(ToggleConsole);
                else
                    Defer(ToggleCollapsed);
            }
        }

        private void ToggleConsole()
        {
            _showConsole = !_showConsole;
            _focusInput = _showConsole;
            _consoleFocused = false;
            _releaseFocus = !_showConsole;
            if (_showConsole)
                _logScroll.y = float.MaxValue;
        }

        private void OnSessionReplaced()
        {
            _optionText.Clear();
            GameSession session = _host.Session;
            if (session != null)
                AppendLog("-- " + session.Mod.Manifest.Name + " --");
        }

        private static float Width => Screen.width;
        private static float Height => Screen.height;

        private void BeginArea(Rect area)
        {
            _origin = area.position;
            GUILayout.BeginArea(area);
        }

        private void EndArea()
        {
            GUILayout.EndArea();
        }

        private void BeginBox(float width)
        {
            GUILayout.BeginVertical(_theme.Panel, GUILayout.Width(width));
        }

        private void EndBox()
        {
            GUILayout.EndVertical();
            Track();
        }

        private void Track()
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Rect rect = GUILayoutUtility.GetLastRect();
            _rects.Add(new Rect(rect.x + _origin.x, rect.y + _origin.y, rect.width, rect.height));
        }

        private void DrawBanner()
        {
            if (_status.Length == 0)
                return;
            float width = _theme.Sf(420);
            BeginArea(new Rect((Width - width) * 0.5f, Margin, width, _theme.Sf(64)));
            GUILayout.BeginHorizontal(_theme.Banner);
            GUILayout.FlexibleSpace();
            if (_hasSwatch)
            {
                Color previous = GUI.color;
                GUI.color = _swatch;
                GUILayout.Box(GUIContent.none, _theme.Swatch);
                GUI.color = previous;
            }
            GUILayout.Label(_status, _theme.Heading, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            Track();
            EndArea();
        }

        private void DrawGamePanel()
        {
            GameSession session = _host.Session;
            ModManifest manifest = session?.Mod.Manifest;
            float width = _theme.Sf(310);

            BeginArea(new Rect(Margin, Margin, width, Height - 2f * Margin));
            BeginBox(width);

            DrawPanelHeader(manifest != null ? DisplayText.Clip(manifest.Name, 24) : "Board Sandbox");
            if (manifest != null && manifest.Description.Length > 0)
                GUILayout.Label(DisplayText.Clip(manifest.Description, 600, false), _theme.Muted);
            if (session != null && Note.Length > 0)
                GUILayout.Label(Note, _theme.Muted);

            DrawGamesButton();
            if (session != null)
            {
                DrawSeats(manifest);
                DrawActions(session);
                DrawTimeline(session);
                DrawOptions();
            }
            if (_multiplayer.Room != null)
                DrawRoom(_multiplayer.Room);

            EndBox();
            EndArea();
        }

        // What somebody who joined a room sees instead of the controls of a game.
        private void DrawGuestPanel(NetRoom room)
        {
            float width = _theme.Sf(310);
            BeginArea(new Rect(Margin, Margin, width, Height - 2f * Margin));
            BeginBox(width);
            ModManifest manifest = room.Mod.Manifest;
            DrawPanelHeader(DisplayText.Clip(manifest.Name, 24));
            if (manifest.Description.Length > 0)
                GUILayout.Label(DisplayText.Clip(manifest.Description, 600, false), _theme.Muted);
            if (GUILayout.Button("Fit view (F)", _theme.Button))
                Defer(() => _fitView?.Invoke());
            DrawRoom(room);
            EndBox();
            EndArea();
        }

        private void DrawRoom(NetRoom room)
        {
            ModManifest manifest = room.Mod.Manifest;
            GUILayout.Space(_theme.Sf(8));
            GUILayout.Label(room.IsHost ? "Online room (you host)" : "Online room", _theme.Muted);
            IReadOnlyList<NetPlayer> players = room.Players;
            for (int i = 0; i < players.Count && i < 16; i++)
            {
                NetPlayer player = players[i];
                string role = room.SplitScreen ? (player.Board > 0 ? "plays" : "watches") : player.Seat > 0 ? SeatName(manifest, player.Seat) : "watches";
                GUILayout.BeginHorizontal();
                if (!room.SplitScreen && player.Seat > 0)
                {
                    Color previous = GUI.color;
                    GUI.color = SeatColor(manifest, player.Seat);
                    GUILayout.Box(GUIContent.none, _theme.Swatch);
                    GUI.color = previous;
                }
                GUILayout.Label(DisplayText.Clip(player.Name, 24) + (player.Id == room.LocalId ? " (you)" : string.Empty), _theme.Label, GUILayout.ExpandWidth(true));
                GUILayout.Label(role, _theme.Muted, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(room.IsHost ? "Close room" : "Leave room", _theme.Button))
                Defer(_multiplayer.Leave);
        }

        private void DrawGamesButton()
        {
            if (GUILayout.Button("Games (G)", _theme.Button))
                Defer(OpenLibrary);
        }

        private void DrawSeats(ModManifest manifest)
        {
            GUILayout.Space(_theme.Sf(6));
            GUILayout.Label("Players", _theme.Muted);
            for (int seat = 1; seat <= manifest.MaxPlayers; seat++)
            {
                GUILayout.BeginHorizontal();
                Color previous = GUI.color;
                GUI.color = SeatColor(manifest, seat);
                GUILayout.Box(GUIContent.none, _theme.Swatch);
                GUI.color = previous;
                GUILayout.Label(SeatName(manifest, seat), _theme.Label, GUILayout.ExpandWidth(true));

                int mode = (int)_host.GetSeatMode(seat);
                int picked = GUILayout.Toolbar(mode, SeatModeNames, _theme.Button, GUILayout.Width(_theme.Sf(150)));
                if (picked != mode)
                {
                    int chosenSeat = seat;
                    SeatMode chosenMode = (SeatMode)picked;
                    Defer(() => _host.SetSeatMode(chosenSeat, chosenMode));
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawActions(GameSession session)
        {
            GUILayout.Space(_theme.Sf(6));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("New game", _theme.Button, GUILayout.ExpandWidth(true)))
                Defer(() => _host.StartNewGame());
            GUI.enabled = session.CanUndo;
            if (GUILayout.Button("Undo", _theme.Button))
                Defer(() => _host.Undo());
            GUI.enabled = session.CanRedo;
            if (GUILayout.Button("Redo", _theme.Button))
                Defer(() => _host.Redo());
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawTimeline(GameSession session)
        {
            if (!session.IsStarted || !session.HistoryEnabled || session.HistoryCount < 2)
                return;
            int last = session.HistoryCount - 1;
            int position = session.HistoryIndex;
            GUILayout.Label("History   " + position + " / " + last, _theme.Muted);
            int target = Mathf.RoundToInt(GUILayout.HorizontalSlider(position, 0f, last));
            if (target != position)
                Defer(() => _host.JumpTo(target));
        }

        private void DrawOptions()
        {
            JsonValue options = _host.NewGameOptions;
            if (options == null || options.Count == 0)
                return;

            GUILayout.Space(_theme.Sf(6));
            bool show = GUILayout.Toggle(_showOptions, "Options (apply to the next game)", _theme.Toggle);
            if (show != _showOptions)
            {
                Defer(() =>
                {
                    _showOptions = show;
                    _releaseFocus = true;
                });
            }
            if (!_showOptions)
                return;

            foreach (KeyValuePair<string, JsonValue> option in options.Members)
                DrawOption(option.Key, option.Value);
        }

        private void DrawOption(string key, JsonValue value)
        {
            if (value.IsBool)
            {
                bool current = value.AsBool();
                bool next = GUILayout.Toggle(current, key, _theme.Toggle);
                if (next != current)
                    Defer(() => _host.SetOption(key, JsonValue.Of(next)));
                return;
            }

            if (!value.IsNumber && !value.IsString)
            {
                GUILayout.Label(key + ": " + value.ToJson(), _theme.Muted);
                return;
            }

            if (!_optionText.TryGetValue(key, out string text))
                text = value.IsNumber ? value.AsDouble().ToString("R", CultureInfo.InvariantCulture) : value.AsString();
            GUILayout.BeginHorizontal();
            GUILayout.Label(key, _theme.Label, GUILayout.Width(_theme.Sf(130)));
            string edited = GUILayout.TextField(text, 64, _theme.Field);
            GUILayout.EndHorizontal();
            if (edited == text)
                return;

            _optionText[key] = edited;
            if (value.IsString)
            {
                Defer(() => _host.SetOption(key, JsonValue.Of(edited)));
            }
            else if (double.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !double.IsNaN(number) && !double.IsInfinity(number))
            {
                bool whole = Math.Abs(number) < 1e15 && Math.Abs(number - Math.Round(number)) < 1e-9;
                JsonValue parsed = whole ? JsonValue.Of((long)Math.Round(number)) : JsonValue.Of(number);
                Defer(() => _host.SetOption(key, parsed));
            }
        }

        private void DrawToolColumn()
        {
            float width = _theme.Sf(230);
            BeginArea(new Rect(Width - width - Margin, Margin, width, Height - 2f * Margin));

            BeginBox(width);
            if (GUILayout.Button("Fit view", _theme.Button))
                Defer(() => _fitView?.Invoke());
            bool console = GUILayout.Toggle(_showConsole, "Console", _theme.Button);
            if (console != _showConsole)
                Defer(ToggleConsole);
            bool simulation = GUILayout.Toggle(_showSimulation, "Simulation", _theme.Button);
            if (simulation != _showSimulation)
            {
                Defer(() =>
                {
                    _showSimulation = simulation;
                    _releaseFocus = true;
                });
            }
            EndBox();

            if (_showSimulation)
                DrawSimulation(width);
            EndArea();
        }

        private void DrawSimulation(float width)
        {
            BeginBox(width);
            GUILayout.Label("Simulation", _theme.Heading);
            GUILayout.Label("Random players, no display.", _theme.Muted);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Games", _theme.Label, GUILayout.Width(_theme.Sf(60)));
            _simGames = GUILayout.TextField(_simGames, 9, _theme.Field);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed", _theme.Label, GUILayout.Width(_theme.Sf(60)));
            _simSeed = GUILayout.TextField(_simSeed, 9, _theme.Field);
            GUILayout.EndHorizontal();

            if (_simRunning)
            {
                if (GUILayout.Button("Cancel", _theme.Button))
                    Defer(() => _host.Simulation.Cancel());
                SimulationProgress progress = _simProgress;
                float fraction = progress != null && progress.GamesTotal > 0 ? Mathf.Clamp01((float)progress.GamesDone / progress.GamesTotal) : 0f;
                Rect bar = GUILayoutUtility.GetRect(1f, _theme.Sf(10), GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(bar, _theme.TrackTexture);
                    GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), _theme.FillTexture);
                }
                if (progress != null)
                {
                    GUILayout.Label(progress.GamesDone + " / " + progress.GamesTotal + " games, "
                        + progress.GamesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " per second", _theme.Muted);
                }
            }
            else
            {
                GUI.enabled = _host.Mod != null;
                if (GUILayout.Button("Run", _theme.Button))
                    Defer(StartSimulation);
                GUI.enabled = true;
                if (_simResult != null)
                    GUILayout.Label(_simResult.ToString(), _theme.Muted);
            }
            EndBox();
        }

        private void StartSimulation()
        {
            ModPackage mod = _host.Mod;
            if (mod == null)
                return;
            SimulationOptions options = new SimulationOptions
            {
                Games = ParseNumber(_simGames, 1000, 1, 1000000),
                Seed = ParseNumber(_simSeed, 1, 1, int.MaxValue),
                GameOptions = _host.NewGameOptions.DeepClone()
            };
            if (!_host.Simulation.Start(mod, options))
                _host.Toasts.Add("A simulation is already running.", ToastLevel.Warning, _host.Now);
        }

        private static int ParseNumber(string text, int fallback, int min, int max)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private void DrawConsole()
        {
            if (!_showConsole)
                return;

            // The text field uses up the keys it understands, so these are looked at before it is drawn.
            Event e = Event.current;
            if (_consoleFocused && e.type == EventType.KeyDown)
                HandleConsoleKey(e);

            float height = _theme.Sf(240);
            float width = Width - 2f * Margin;
            BeginArea(new Rect(Margin, Height - height - Margin, width, height));
            BeginBox(width);

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(height - _theme.Sf(76)));
            GUILayout.Label(_logText, _theme.Mono);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(ConsoleField);
            _input = GUILayout.TextField(_input, 400, _theme.Field);
            if (GUILayout.Button("Run", _theme.Button, GUILayout.Width(_theme.Sf(70))))
                SubmitInput();
            GUILayout.EndHorizontal();

            if (e.type == EventType.Repaint)
            {
                if (_focusInput)
                {
                    GUI.FocusControl(ConsoleField);
                    _focusInput = false;
                }
                _consoleFocused = GUI.GetNameOfFocusedControl() == ConsoleField;
            }

            EndBox();
            EndArea();
        }

        private void HandleConsoleKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    e.Use();
                    SubmitInput();
                    break;
                case KeyCode.UpArrow:
                    e.Use();
                    Recall(-1);
                    break;
                case KeyCode.DownArrow:
                    e.Use();
                    Recall(1);
                    break;
            }
        }

        private void SubmitInput()
        {
            string line = _input.Trim();
            _input = string.Empty;
            _focusInput = true;
            if (line.Length == 0)
                return;

            if (_recalled.Count == 0 || _recalled[_recalled.Count - 1] != line)
                _recalled.Add(line);
            if (_recalled.Count > MaxRecalled)
                _recalled.RemoveAt(0);
            _recallIndex = _recalled.Count;

            AppendLog("> " + line);
            Defer(() =>
            {
                CommandConsole console = _host.Console;
                AppendLog(console != null ? console.Execute(line) : "No game is loaded.");
            });
        }

        private void Recall(int direction)
        {
            if (_recalled.Count == 0)
                return;
            _recallIndex = Mathf.Clamp(_recallIndex + direction, 0, _recalled.Count);
            _input = _recallIndex < _recalled.Count ? _recalled[_recallIndex] : string.Empty;
        }

        private void RebuildLog()
        {
            int first = _lines.Count;
            int characters = 0;
            while (first > 0 && characters + _lines[first - 1].Length + 1 <= MaxLogCharacters)
            {
                first--;
                characters += _lines[first].Length + 1;
            }
            StringBuilder builder = new StringBuilder(characters);
            for (int i = first; i < _lines.Count; i++)
            {
                if (i > first)
                    builder.Append('\n');
                builder.Append(_lines[i]);
            }
            _logText = builder.ToString();
            _logScroll.y = float.MaxValue;
        }

        private void DrawToasts()
        {
            if (_toasts.Count == 0)
                return;
            float width = _theme.Sf(520);
            float reserved = _showConsole ? _theme.Sf(240) + Margin : 0f;
            BeginArea(new Rect((Width - width) * 0.5f, 0f, width, Height - reserved - Margin));
            GUILayout.FlexibleSpace();
            Color previous = GUI.color;
            foreach (Toast toast in _toasts)
            {
                GUI.color = new Color(1f, 1f, 1f, toast.Opacity(_now));
                GUIStyle style = toast.Level == ToastLevel.Error ? _theme.ErrorToast : toast.Level == ToastLevel.Warning ? _theme.WarningToast : _theme.InfoToast;
                GUILayout.Label(toast.Text, style, GUILayout.Width(width));
                Track();
            }
            GUI.color = previous;
            EndArea();
        }
    }
}
