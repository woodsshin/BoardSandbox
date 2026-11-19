using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Agents;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Net;
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
        private readonly AgentConsolePanel _agents;
        private readonly ChatPanel _chat;
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
        private bool _showOptions = true;

        private readonly ChatWatch _chatWatch = new ChatWatch();
        private bool _chatFolded;

        private double _now;
        private string _status = string.Empty;
        private bool _hasSwatch;
        private Color _swatch = Color.white;

        public Hud(GameHost host, Multiplayer multiplayer, ModStudio studio, GuiTheme theme)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _multiplayer = multiplayer ?? throw new ArgumentNullException(nameof(multiplayer));
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            if (studio == null)
                throw new ArgumentNullException(nameof(studio));
            _library = new GameLibraryPanel(host, multiplayer, theme, Defer, () => _releaseFocus = true, OpenStudio,
                entry => studio.CanDelete(entry.Mod), entry => DeleteGame(studio, entry));
            _agents = new AgentConsolePanel(studio, theme, () => _host.Now, Defer, () => _releaseFocus = true);
            _chat = new ChatPanel(theme, () => Defer(ToggleChat));
            _library.GameChosen += PutAwayConsole;
            _host.SessionReplaced += OnSessionReplaced;
            _multiplayer.RoomChanged += OnRoomChanged;
            _multiplayer.RoomLost += OpenLibrary;
        }

        // True while the panels of the game (its options and the room) are folded away so that they do not cover the table. The chat is
        // not one of them: it has a button of its own, see ToggleChat.
        public bool Collapsed { get; private set; }

        public void ToggleCollapsed()
        {
            Collapsed = !Collapsed;
            _releaseFocus = true;
        }

        // Folds the chat of a room away, so that only a tab is left of it, or shows it again. It runs between two frames, deferred.
        private void ToggleChat()
        {
            _chatFolded = !_chatFolded;
            _releaseFocus = true;
        }

        // The room of a game that somebody else hosts; null when the local player hosts or plays alone.
        private NetRoom GuestRoom => _multiplayer.Room != null && !_multiplayer.Room.IsHost ? _multiplayer.Room : null;

        // Entering a room puts its game on the table, so the list of games and the AI console get out of the way.
        private void OnRoomChanged()
        {
            if (_multiplayer.Room == null)
                return;
            if (_library.IsOpen)
                _library.Close();
            PutAwayConsole();
        }

        // The AI console is for making a game; once the player chose a game to play it makes room for the table. An
        // agent that is still at work keeps its window, so that its answer is not missed. Opening the console again
        // brings it back as it was left.
        private void PutAwayConsole()
        {
            if (_agents.IsOpen && !_agents.Busy)
                _agents.Close();
        }

        // Leaves the room the local player is in, a guest leaving the room of somebody else or a host closing their own.
        // Either way the game of the room is over for them, so the list of games opens, as it does for a guest when the
        // host closes the room. Call it between two frames, not from inside OnGUI.
        private void LeaveRoom()
        {
            _multiplayer.Leave();
            OpenLibrary();
        }

        // True while a text field has the keyboard, so that shortcuts leave the typed characters alone.
        public bool TextFocused => GUIUtility.keyboardControl != 0 || TouchText.Editing;

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

        // Opens the AI console beside the table: for a new game, or for the game of the entry when it can be edited.
        // Call it between two frames, not from inside OnGUI.
        public void OpenStudio(ModEntry entry)
        {
            if (_library.IsOpen)
                _library.Close();
            if (entry == null)
                _agents.OpenNew();
            else
                _agents.OpenEdit(entry.Mod);
        }

        // Removes a game of the AI console for good and says how it went.
        private bool DeleteGame(ModStudio studio, ModEntry entry)
        {
            bool deleted = studio.Delete(entry.Mod, out string message);
            _host.Toasts.Add(message, deleted ? ToastLevel.Info : ToastLevel.Error, _host.Now);
            return deleted;
        }

        // How much of the width of the screen the AI console, or the chat when it stands in a column, takes at the right edge, from 0 to 1.
        // The owner keeps the table out of that part.
        public float DockFraction
        {
            get
            {
                if (ModalOpen || Width <= 0f)
                    return 0f;
                float console = _agents.IsOpen ? Mathf.Clamp((Width - _agents.Area.x + Margin * 0.5f) / Width, 0f, 0.7f) : 0f;
                return Mathf.Max(console, ChatNow().RightFraction);
            }
        }

        // How much of the height of the screen the chat takes at the bottom when it stands in a strip, from 0 to 1. The owner keeps the
        // table out of that part as well.
        public float BottomDockFraction => ChatNow().BottomFraction;

        // The width that is left for the table and what is centered over it.
        private float FreeWidth
        {
            get
            {
                float free = _agents.IsOpen && _agents.Area.width > 0f ? _agents.Area.x : Width;
                ChatPlacement chat = ChatNow();
                return chat.Dock == ChatDock.Right ? Mathf.Min(free, chat.X) : free;
            }
        }

        // The height of the panels at the left: all there is, or what is above the chat when that stands in a strip along the bottom.
        private float PanelHeight
        {
            get
            {
                ChatPlacement chat = ChatNow();
                float bottom = chat.Dock == ChatDock.Bottom ? chat.Y - Margin * 0.5f : Height - Margin;
                return Mathf.Max(_theme.Sf(120), bottom - Margin);
            }
        }

        // Where the chat of the room stands on the screen. There is one in a room whose host passes it on, unless a list covers the screen;
        // folded away, it is a tab. The panels of the game being folded away has no say in it. The AI console and the console of commands
        // are in its way where they are.
        private ChatPlacement ChatNow()
        {
            NetRoom room = _multiplayer.Room;
            bool available = room != null && room.CanChat && !ModalOpen;
            float rightEdge = _agents.IsOpen && _agents.Area.width > 0f ? _agents.Area.x : Width;
            float console = _showConsole ? _theme.Sf(240) + Margin : 0f;
            return ChatLayout.Place(Width, Height, _theme.Scale, Margin, available, rightEdge, console, _chatFolded);
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
            _agents.Snapshot();
            _chat.Snapshot(_multiplayer.Room, now);
            // What is said while the chat is put away, or has no room, would be missed.
            ChatDock dock = ChatNow().Dock;
            _chatWatch.Update(_multiplayer.Room, dock == ChatDock.Right || dock == ChatDock.Bottom, _host.Toasts, now);

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
            DrawChatBackground();
            // The chat is drawn before the panels, not after them: the controls of IMGUI are known by the order they are made in, so a
            // field that comes after panels whose buttons come and go with the game would be taken for another control, and lose
            // the keyboard, in the middle of a line.
            DrawChat();
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
                DrawConsole();
            }
            if (_agents.IsOpen)
            {
                // No camera draws into the strip that the console takes, so it gets a background of its own.
                if (e.type == EventType.Repaint && _agents.Area.width > 0f)
                {
                    float left = _agents.Area.x - Margin * 0.5f;
                    GUI.DrawTexture(new Rect(left, 0f, Width - left, Height), _theme.TrackTexture);
                }
                _agents.Draw();
                if (e.type == EventType.Repaint)
                    _rects.Add(_agents.Area);
            }
            DrawToasts();
        }

        // No camera draws into the part of the screen that the chat takes, so it gets a background of its own.
        private void DrawChatBackground()
        {
            if (Event.current.type != EventType.Repaint)
                return;
            ChatPlacement chat = ChatNow();
            if (chat.Dock != ChatDock.Right && chat.Dock != ChatDock.Bottom)
                return;
            Rect rect = chat.Dock == ChatDock.Right
                ? new Rect(chat.X - Margin * 0.5f, 0f, Width - chat.X + Margin * 0.5f, Height)
                : new Rect(0f, chat.Y - Margin * 0.5f, chat.X + chat.Width + Margin, Height - chat.Y + Margin * 0.5f);
            GUI.DrawTexture(rect, _theme.TrackTexture);
        }

        private void DrawChat()
        {
            NetRoom room = _multiplayer.Room;
            ChatPlacement chat = ChatNow();
            if (room == null || chat.Dock == ChatDock.None)
                return;
            _chat.Draw(chat, _chatWatch.Unread);
            if (Event.current.type == EventType.Repaint)
                _rects.Add(new Rect(chat.X, chat.Y, chat.Width, chat.Height));
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
                Defer(LeaveRoom);
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
            BeginArea(new Rect(Margin, Margin, width, PanelHeight));
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
                if (board == null || board.Queue.Display.Turn <= 0 && !board.Queue.Display.IsOver && !guest.Mod.Manifest.Cooperative)
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
                if (manifest.Cooperative)
                {
                    _status = (over.Winner > 0 ? "Everyone wins" : "Game over") + reason;
                }
                else if (over.Winner > 0)
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
            if (manifest.Cooperative)
            {
                // Nobody is on turn: the seats play together, and only a room has more than one of them.
                bool watching = room != null && room.LocalPlayer != null && room.LocalPlayer.Seat <= 0;
                _status = watching ? "Watching" : room != null ? "Playing together" : string.Empty;
                return;
            }
            if (view.Turn > 0)
            {
                _status = SeatName(manifest, view.Turn) + " to move" + TurnNote(room, guest, view.Turn);
                _hasSwatch = true;
                _swatch = SeatColor(manifest, view.Turn);
                return;
            }
            _status = string.Empty;
        }

        // Who is on turn, where that is not plain: the local player, the computer, or nobody yet.
        private string TurnNote(NetRoom room, NetRoom guest, int seat)
        {
            if (room == null)
                return _host.AcceptsHumanInput ? string.Empty : " (computer)";
            if (room.SplitScreen)
                return string.Empty;
            if (room.CanAct)
                return " (you)";
            if (room.IsComputerSeat(seat) || (guest == null && !_host.AcceptsHumanInput))
                return " (computer)";
            return room.WaitsForPlayers && SeatedPlayer(room, seat) == null ? " (waiting for a player)" : string.Empty;
        }

        // The person who sits at the seat of a shared game; null when nobody does.
        private static NetPlayer SeatedPlayer(NetRoom room, int seat)
        {
            IReadOnlyList<NetPlayer> players = room.Players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Seat == seat)
                    return players[i];
            }
            return null;
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
            // While the keyboard of a phone is open its keys are the keyboard's, the back key that closes it too.
            if (e.type != EventType.KeyDown || TouchText.Editing)
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
            BeginArea(new Rect((FreeWidth - width) * 0.5f, Margin, width, _theme.Sf(64)));
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

            BeginArea(new Rect(Margin, Margin, width, PanelHeight));
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
            BeginArea(new Rect(Margin, Margin, width, PanelHeight));
            BeginBox(width);
            ModManifest manifest = room.Mod.Manifest;
            DrawPanelHeader(DisplayText.Clip(manifest.Name, 24));
            if (manifest.Description.Length > 0)
                GUILayout.Label(DisplayText.Clip(manifest.Description, 600, false), _theme.Muted);
            DrawRoom(room);
            EndBox();
            EndArea();
        }

        private void DrawRoom(NetRoom room)
        {
            ModManifest manifest = room.Mod.Manifest;
            GUILayout.Space(_theme.Sf(8));
            GUILayout.Label(room.IsHost ? "Online room (you host)" : "Online room", _theme.Muted);
            if (SeatsAreManaged(room))
            {
                // The host sees the table where the seats are drawn; somebody who joined has no seats of their own.
                if (!room.IsHost)
                    DrawTable(room);
            }
            else
            {
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
            }
            string hint = RoomHint(room);
            if (hint.Length > 0)
                GUILayout.Label(hint, _theme.Muted);
            if (GUILayout.Button(room.IsHost ? "Close room" : "Leave room", _theme.Button))
                Defer(LeaveRoom);
        }

        private void DrawGamesButton()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Games (G)", _theme.Button, GUILayout.ExpandWidth(true)))
                Defer(OpenLibrary);
            bool open = GUILayout.Toggle(_agents.IsOpen, "AI console", _theme.Button, GUILayout.ExpandWidth(true));
            if (open != _agents.IsOpen)
            {
                if (open)
                    Defer(_agents.Show);
                else
                    Defer(_agents.Close);
            }
            GUILayout.EndHorizontal();
        }

        // Whether the seats of the room are looked after by the room: the computer fills the empty ones, or they wait for
        // people. Nobody switches them by hand then.
        private static bool SeatsAreManaged(NetRoom room)
        {
            return room != null && !room.SplitScreen && (room.ComputerFills || room.WaitsForPlayers);
        }

        private void DrawSeats(ModManifest manifest)
        {
            NetRoom room = _multiplayer.Room;
            if (SeatsAreManaged(room))
            {
                DrawTable(room);
                return;
            }
            // A game that is not played against the computer, a puzzle that hands out hints instead, has no seat to switch.
            if (!manifest.AllowComputer)
                return;

            GUILayout.Space(_theme.Sf(6));
            GUILayout.Label("Players", _theme.Muted);
            int seats = Math.Max(1, Math.Min(manifest.MaxPlayers, _host.SeatCount));
            for (int seat = 1; seat <= seats; seat++)
            {
                GUILayout.BeginHorizontal();
                Color previous = GUI.color;
                GUI.color = SeatColor(manifest, seat);
                GUILayout.Box(GUIContent.none, _theme.Swatch);
                GUI.color = previous;
                GUILayout.Label(SeatName(manifest, seat), _theme.Label, GUILayout.ExpandWidth(true));

                // "Computer" is the mod's thinking player when it has one and random moves otherwise.
                int mode = _host.GetSeatMode(seat) == SeatMode.Human ? 0 : 1;
                int picked = GUILayout.Toolbar(mode, SeatModeNames, _theme.Button, GUILayout.Width(_theme.Sf(150)));
                if (picked != mode)
                {
                    int chosenSeat = seat;
                    SeatMode chosenMode = picked == 0 ? SeatMode.Human : _host.HasAi ? SeatMode.Ai : SeatMode.Random;
                    Defer(() => _host.ChooseSeatMode(chosenSeat, chosenMode));
                }
                GUILayout.EndHorizontal();
            }
            if (_host.HasAi && _host.Session != null && manifest == _host.Session.Mod.Manifest)
            {
                GUILayout.Label("Computer level " + _host.AiLevel + "/" + _host.AiLevelCount + " (effort " + _host.AiEffort + ")", _theme.Muted);
            }
        }

        // Who plays each seat of a room that the computer fills or that waits for people: a person, the computer or nobody
        // yet; and below them the people who have no seat.
        private void DrawTable(NetRoom room)
        {
            ModManifest manifest = room.Mod.Manifest;
            IReadOnlyList<NetPlayer> players = room.Players;
            GUILayout.Space(_theme.Sf(6));
            GUILayout.Label("Players", _theme.Muted);
            int seats = Math.Max(1, Math.Min(manifest.MaxPlayers, room.SeatCount));
            for (int seat = 1; seat <= seats; seat++)
            {
                NetPlayer sitter = SeatedPlayer(room, seat);
                bool computer = sitter == null && room.IsComputerSeat(seat);
                string who = sitter != null ? DisplayText.Clip(sitter.Name, 24) + (sitter.Id == room.LocalId ? " (you)" : string.Empty)
                    : computer ? "Computer" : "Waiting for a player…";
                GUILayout.BeginHorizontal();
                Color previous = GUI.color;
                GUI.color = SeatColor(manifest, seat);
                GUILayout.Box(GUIContent.none, _theme.Swatch);
                GUI.color = previous;
                GUILayout.Label(who, sitter != null || computer ? _theme.Label : _theme.Muted, GUILayout.ExpandWidth(true));
                GUILayout.Label(SeatName(manifest, seat), _theme.Muted, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
            }
            for (int i = 0; i < players.Count && i < 16; i++)
            {
                NetPlayer player = players[i];
                if (player.Seat > 0)
                    continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(DisplayText.Clip(player.Name, 24) + (player.Id == room.LocalId ? " (you)" : string.Empty), _theme.Label, GUILayout.ExpandWidth(true));
                GUILayout.Label(room.ComputerFills ? "next game" : "watches", _theme.Muted, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
            }
            if (room.IsHost && room.ComputerSeats.Count > 0 && _host.HasAi && _host.Session != null && manifest == _host.Session.Mod.Manifest)
                GUILayout.Label("Computer level " + _host.AiLevel + "/" + _host.AiLevelCount + " (effort " + _host.AiEffort + ")", _theme.Muted);
        }

        // What the local player should know about the room beyond who sits where.
        private static string RoomHint(NetRoom room)
        {
            if (room.SplitScreen)
                return string.Empty;
            NetPlayer me = room.LocalPlayer;
            if (room.ComputerFills)
            {
                int waiting = room.WaitingCount;
                if (!room.IsHost)
                    return me != null && me.Seat <= 0 ? "You watch this game. When the host starts a new game you play it." : string.Empty;
                if (waiting > 0)
                    return waiting == 1 ? "Someone is waiting to play. Press New game to start a game with them." : waiting + " people are waiting to play. Press New game to start a game with them.";
                return "You play the computer. When someone joins, press New game to play with them.";
            }
            if (room.WaitsForPlayers && room.IsHost && room.Players.Count < Math.Max(1, room.SeatCount))
                return "The room waits for people to join.";
            return string.Empty;
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
            // On a phone a number also has a button to either side that takes one off or adds one, so that a small
            // number is set without the keyboard.
            bool steps = value.IsNumber && TouchText.Active;
            GUILayout.BeginHorizontal();
            GUILayout.Label(key, _theme.Label, GUILayout.Width(_theme.Sf(steps ? 96 : 130)));
            int step = 0;
            if (steps && GUILayout.Button("-", _theme.Button, GUILayout.Width(_theme.Sf(30))))
                step = -1;
            string name = "option:" + key;
            string edited = value.IsNumber ? TouchText.NumberField(name, text, 64, _theme.Field) : TouchText.Field(name, text, 64, _theme.Field);
            if (steps && GUILayout.Button("+", _theme.Button, GUILayout.Width(_theme.Sf(30))))
                step = 1;
            GUILayout.EndHorizontal();
            if (step != 0 && double.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out double now) && !double.IsNaN(now) && !double.IsInfinity(now))
                edited = (Math.Round(now) + step).ToString("R", CultureInfo.InvariantCulture);
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

        private void DrawConsole()
        {
            if (!_showConsole)
                return;

            // The text field uses up the keys it understands, so these are looked at before it is drawn.
            Event e = Event.current;
            if (_consoleFocused && e.type == EventType.KeyDown)
                HandleConsoleKey(e);

            float height = _theme.Sf(240);
            float width = FreeWidth - 2f * Margin;
            BeginArea(new Rect(Margin, Height - height - Margin, width, height));
            BeginBox(width);

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(height - _theme.Sf(76)));
            GUILayout.Label(_logText, _theme.Mono);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(ConsoleField);
            _input = TouchText.Field(ConsoleField, _input, 400, _theme.Field);
            // The done key of a keyboard on the screen is the enter key of this field.
            if (GUILayout.Button("Run", _theme.Button, GUILayout.Width(_theme.Sf(70))) || TouchText.Submitted(ConsoleField))
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
            ChatPlacement chat = ChatNow();
            if (chat.Dock == ChatDock.Bottom)
                reserved = Mathf.Max(reserved, Height - chat.Y);
            float free = _library.IsOpen || _multiplayer.Loading != null ? Width : FreeWidth;
            BeginArea(new Rect((free - width) * 0.5f, 0f, width, Height - reserved - Margin));
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
