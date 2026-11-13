using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Net;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The full-screen list of games. It only reads state while it draws and changes it through the deferral it was
    // given, so that every pass of one frame sees the same controls.
    public sealed class GameLibraryPanel
    {
        private const string SearchField = "bse-library-search";
        private const float Margin = 12f;
        private const float RowHeight = 58f;
        private const float RowGap = 4f;
        private const float RowPad = 7f;
        private const float TileSize = 44f;
        private const int VirtualizeAbove = 60;
        private const int MaxName = 32;
        private const int MaxDescription = 1200;
        private const int MaxSeatNames = 8;
        private const int MaxOptions = 8;
        private const int MaxProblems = 40;
        private const int MaxRooms = 30;

        private readonly GameHost _host;
        private readonly Multiplayer _multiplayer;
        private readonly GuiTheme _theme;
        private readonly GameLibrary _library;
        private readonly Action<Action> _defer;
        private readonly Action _releaseFocus;
        private readonly Action<ModEntry> _openStudio;
        private readonly Func<ModEntry, bool> _canDelete;
        private readonly Func<ModEntry, bool> _delete;

        private string _queryText = string.Empty;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private Vector2 _problemScroll;
        private float _listHeight;
        private int _firstRow;
        private int _lastRow;
        private int _pressClicks;
        private bool _focusSearch;
        private bool _revealSelected;
        private bool _showProblems;
        private ModOrigin[] _filterOrigins = new ModOrigin[0];
        private string[] _filterNames = { "All" };

        private ModEntry _detailFor;
        private string _detailName = string.Empty;
        private string _detailInfo = string.Empty;
        private string _detailDescription = string.Empty;
        private string _detailSeats = string.Empty;
        private string _detailOptions = string.Empty;
        private string _roomsFor = string.Empty;
        private bool _detailDeletable;
        private bool _confirmDelete;
        private Vector2 _roomScroll;

        // openStudio opens the AI console for a new game (null) or for the game of an entry; without it the buttons
        // for that are left out. canDelete says whether a game may be removed and delete removes it; without them
        // nothing can be deleted here.
        public GameLibraryPanel(GameHost host, Multiplayer multiplayer, GuiTheme theme, Action<Action> defer, Action releaseFocus, Action<ModEntry> openStudio = null,
            Func<ModEntry, bool> canDelete = null, Func<ModEntry, bool> delete = null)
        {
            _openStudio = openStudio;
            _canDelete = canDelete;
            _delete = delete;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _multiplayer = multiplayer ?? throw new ArgumentNullException(nameof(multiplayer));
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            _defer = defer ?? throw new ArgumentNullException(nameof(defer));
            _releaseFocus = releaseFocus ?? throw new ArgumentNullException(nameof(releaseFocus));
            _library = new GameLibrary(host);
        }

        public bool IsOpen { get; private set; }

        // Call it between two frames, not from inside OnGUI.
        public void Open()
        {
            _library.Reset();
            _queryText = string.Empty;
            _listScroll = Vector2.zero;
            _showProblems = false;
            _detailFor = null;
            _roomsFor = string.Empty;
            _focusSearch = true;
            _revealSelected = true;
            RefreshFilter();
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
            _releaseFocus();
        }

        public void Draw()
        {
            Event e = Event.current;
            if (e.type == EventType.Layout && _revealSelected)
            {
                RevealSelected();
                _revealSelected = false;
            }
            if (e.type == EventType.KeyDown)
                HandleKey(e);

            float width = Mathf.Min(Screen.width - 2f * Margin, _theme.Sf(960f));
            float height = Mathf.Min(Screen.height - 2f * Margin, _theme.Sf(620f));
            if (e.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _theme.DimTexture);

            GUILayout.BeginArea(new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height));
            GUILayout.BeginVertical(_theme.Panel, GUILayout.Width(width), GUILayout.Height(height));
            DrawHeader();
            DrawSearch();
            GUILayout.Space(_theme.Sf(6f));
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            DrawList(Mathf.Min(_theme.Sf(380f), width * 0.45f));
            DrawDetails();
            GUILayout.EndHorizontal();
            GUILayout.Label("Up / Down  choose      Enter  play      Esc  close", _theme.Muted);
            GUILayout.EndVertical();
            GUILayout.EndArea();

            if (e.type == EventType.Repaint && _focusSearch)
            {
                GUI.FocusControl(SearchField);
                _focusSearch = false;
            }
        }

        private float Pitch => _theme.Sf(RowHeight + RowGap);

        // The text field uses up the arrow keys and Return, so these are looked at before it is drawn.
        private void HandleKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    e.Use();
                    _defer(Close);
                    break;
                case KeyCode.UpArrow:
                    e.Use();
                    _defer(() => Move(-1));
                    break;
                case KeyCode.DownArrow:
                    e.Use();
                    _defer(() => Move(1));
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    e.Use();
                    _defer(Confirm);
                    break;
            }
        }

        private void Move(int steps)
        {
            _library.Move(steps);
            _revealSelected = true;
        }

        private void SetQuery(string text)
        {
            _library.SetQuery(text);
            _revealSelected = true;
        }

        private void SetOrigin(ModOrigin? origin)
        {
            _library.SetOrigin(origin);
            _revealSelected = true;
        }

        private void Play()
        {
            if (_library.Play())
                Close();
        }

        // Return on the game that is on the table goes back to it; on any other game it starts that one.
        private void Confirm()
        {
            ModEntry entry = _library.Selected;
            if (entry == null)
                return;
            if (_library.IsRunning(entry))
                Close();
            else
                Play();
        }

        private void Rescan()
        {
            _library.Rescan();
            RefreshFilter();
            _revealSelected = true;
            int count = _host.Catalog.Entries.Count;
            _host.Toasts.Add(count == 1 ? "1 game found." : count + " games found.", ToastLevel.Info, _host.Now);
        }

        private void RefreshFilter()
        {
            IReadOnlyList<ModOrigin> origins = _library.Origins;
            _filterOrigins = new ModOrigin[origins.Count];
            _filterNames = new string[origins.Count + 1];
            _filterNames[0] = "All";
            for (int i = 0; i < origins.Count; i++)
            {
                _filterOrigins[i] = origins[i];
                _filterNames[i + 1] = origins[i].Label();
            }
        }

        // Scrolls the list just far enough for the selected row to be in view.
        private void RevealSelected()
        {
            int index = _library.SelectedIndex;
            if (index < 0)
                return;
            float view = _listHeight > 0f ? _listHeight : _theme.Sf(360f);
            float top = index * Pitch;
            float bottom = top + _theme.Sf(RowHeight);
            if (top < _listScroll.y)
                _listScroll.y = top;
            else if (bottom > _listScroll.y + view)
                _listScroll.y = bottom - view;
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Games", _theme.Heading);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", _theme.Button, GUILayout.Width(_theme.Sf(80f))))
                _defer(Close);
            GUILayout.EndHorizontal();
        }

        private void DrawSearch()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", _theme.Label, GUILayout.Width(_theme.Sf(56f)));
            GUI.SetNextControlName(SearchField);
            string edited = GUILayout.TextField(_queryText, 64, _theme.Field);
            if (!string.Equals(edited, _queryText, StringComparison.Ordinal))
            {
                _queryText = edited;
                _defer(() => SetQuery(edited));
            }
            DrawOriginFilter();
            GUILayout.EndHorizontal();
        }

        private void DrawOriginFilter()
        {
            if (_filterOrigins.Length < 2)
                return;
            int current = 0;
            for (int i = 0; i < _filterOrigins.Length; i++)
            {
                if (_library.Origin == _filterOrigins[i])
                    current = i + 1;
            }
            int picked = GUILayout.Toolbar(current, _filterNames, _theme.Button, GUILayout.Width(_theme.Sf(96f) * _filterNames.Length));
            if (picked == current)
                return;
            ModOrigin? origin = picked == 0 ? (ModOrigin?)null : _filterOrigins[picked - 1];
            _defer(() => SetOrigin(origin));
        }

        private void DrawList(float width)
        {
            IReadOnlyList<ModEntry> rows = _library.Visible;
            Event e = Event.current;
            GUILayout.BeginVertical(GUILayout.Width(width), GUILayout.ExpandHeight(true));

            if (e.type == EventType.Layout)
                PlanRows(rows.Count);
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));
            if (rows.Count == 0)
                GUILayout.Label(_host.Catalog.Entries.Count == 0 ? "No games found." : "No game matches the search.", _theme.Muted);
            else
                DrawRows(rows);
            GUILayout.EndScrollView();
            if (e.type == EventType.Repaint)
                _listHeight = GUILayoutUtility.GetLastRect().height;

            DrawProblems();
            GUILayout.EndVertical();
        }

        // Long lists only get controls for the rows in view and a little around them. The range is fixed in the
        // layout pass, so that the pass that draws has the same controls even when the list scrolls in between.
        private void PlanRows(int count)
        {
            float pitch = Pitch;
            if (count <= VirtualizeAbove || pitch <= 0f)
            {
                _firstRow = 0;
                _lastRow = count - 1;
                return;
            }
            int first = Mathf.Clamp(Mathf.FloorToInt(_listScroll.y / pitch) - 3, 0, count - 1);
            int inView = Mathf.CeilToInt(Mathf.Max(_listHeight, pitch) / pitch) + 7;
            _firstRow = first;
            _lastRow = Mathf.Min(count - 1, first + inView);
        }

        private void DrawRows(IReadOnlyList<ModEntry> rows)
        {
            int first = Mathf.Clamp(_firstRow, 0, rows.Count - 1);
            int last = Mathf.Clamp(_lastRow, first, rows.Count - 1);
            if (first > 0)
                GUILayout.Space(first * Pitch);

            string selectedId = _library.SelectedId;
            for (int i = first; i <= last; i++)
            {
                DrawRow(rows[i], string.Equals(rows[i].Id, selectedId, StringComparison.Ordinal));
                GUILayout.Space(_theme.Sf(RowGap));
            }
            if (last < rows.Count - 1)
                GUILayout.Space((rows.Count - 1 - last) * Pitch);
        }

        private void DrawRow(ModEntry entry, bool selected)
        {
            Rect rect = GUILayoutUtility.GetRect(1f, _theme.Sf(RowHeight), GUILayout.ExpandWidth(true));
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
                _pressClicks = e.clickCount;

            bool clicked = GUI.Toggle(rect, selected, GUIContent.none, _theme.RowButton) != selected;
            if (clicked)
            {
                string id = entry.Id;
                bool open = _pressClicks >= 2;
                _defer(() =>
                {
                    _library.Select(id);
                    if (open)
                        Confirm();
                });
            }
            if (e.type == EventType.Repaint)
                PaintRow(rect, entry);
        }

        private void PaintRow(Rect rect, ModEntry entry)
        {
            float pad = _theme.Sf(RowPad);
            float size = _theme.Sf(TileSize);
            Rect tile = new Rect(rect.x + pad, rect.y + (rect.height - size) * 0.5f, size, size);
            DrawTile(tile, entry);

            bool running = _library.IsRunning(entry);
            float badge = running ? _theme.Sf(72f) : 0f;
            float textX = tile.xMax + pad;
            float textWidth = Mathf.Max(0f, rect.xMax - pad - badge - textX);
            float line = size * 0.5f;
            GUI.Label(new Rect(textX, tile.y, textWidth, line + 2f), DisplayText.Clip(entry.Name, MaxName), _theme.RowName);
            GUI.Label(new Rect(textX, tile.y + line, textWidth, line), entry.Summary, _theme.RowInfo);
            if (running)
                GUI.Label(new Rect(rect.xMax - pad - badge, rect.y, badge, rect.height), "Playing", _theme.Badge);
        }

        private void DrawTile(Rect rect, ModEntry entry)
        {
            Color previous = GUI.color;
            GUI.color = GuiTheme.TileColor(entry.TileHue);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(rect, entry.Initial, _theme.TileLetter);
        }

        private void DrawProblems()
        {
            IReadOnlyList<ModLoadError> errors = _host.Catalog.Errors;
            if (errors.Count == 0)
                return;

            GUILayout.Space(_theme.Sf(4f));
            bool show = GUILayout.Toggle(_showProblems, "Problems (" + errors.Count + ")", _theme.Toggle);
            if (show != _showProblems)
                _defer(() => _showProblems = show);
            if (!_showProblems)
                return;

            _problemScroll = GUILayout.BeginScrollView(_problemScroll, GUILayout.Height(_theme.Sf(120f)));
            int shown = Math.Min(errors.Count, MaxProblems);
            for (int i = 0; i < shown; i++)
                GUILayout.Label(DisplayText.Clip(Path.GetFileName(errors[i].Directory) + ": " + errors[i].Message, 240), _theme.Muted);
            if (errors.Count > shown)
                GUILayout.Label("and " + (errors.Count - shown) + " more", _theme.Muted);
            GUILayout.EndScrollView();
        }

        private void DrawDetails()
        {
            ModEntry entry = _library.Selected;
            GUILayout.BeginVertical(_theme.Card, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (entry != null)
            {
                DrawEntry(entry);
                DrawRooms(entry);
            }
            else
            {
                DrawNothingSelected();
            }
            DrawActions(entry);
            GUILayout.EndVertical();
        }

        private void DrawEntry(ModEntry entry)
        {
            EnsureDetails(entry);
            float size = _theme.Sf(TileSize);

            GUILayout.BeginHorizontal();
            Rect tile = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            if (Event.current.type == EventType.Repaint)
                DrawTile(tile, entry);
            GUILayout.Space(_theme.Sf(8f));
            GUILayout.BeginVertical();
            GUILayout.Label(_detailName, _theme.Heading);
            GUILayout.Label(_detailInfo, _theme.Muted);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(_theme.Sf(6f));
            _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));
            GUILayout.Label(_detailDescription, _theme.Wrapped);
            if (_detailSeats.Length > 0)
                GUILayout.Label(_detailSeats, _theme.Muted);
            if (_detailOptions.Length > 0)
                GUILayout.Label(_detailOptions, _theme.Muted);
            GUILayout.EndScrollView();
        }

        // The rooms that play the selected game. The list is fetched whenever another game is selected.
        private void DrawRooms(ModEntry entry)
        {
            GUILayout.Space(_theme.Sf(6f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Online rooms", _theme.Label, GUILayout.ExpandWidth(true));
            bool available = _multiplayer.Available;
            if (available)
            {
                string id = entry.Id;
                if (Event.current.type == EventType.Layout && _roomsFor != id && !_multiplayer.Busy)
                {
                    _roomsFor = id;
                    _defer(() => _multiplayer.Refresh(id));
                }
                GUI.enabled = !_multiplayer.Busy;
                if (GUILayout.Button("Refresh", _theme.Button, GUILayout.Width(_theme.Sf(80f))))
                    _defer(() => _multiplayer.Refresh(id));
                if (GUILayout.Button("Host", _theme.Button, GUILayout.Width(_theme.Sf(80f))))
                {
                    ModPackage mod = entry.Mod;
                    _defer(() => _multiplayer.HostGame(mod));
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();

            if (!available)
            {
                GUILayout.Label(DisplayText.Clip(_multiplayer.Status, 200), _theme.Muted);
                return;
            }

            IReadOnlyList<LobbyInfo> rooms = _multiplayer.Lobbies(entry.Id);
            _roomScroll = GUILayout.BeginScrollView(_roomScroll, GUILayout.Height(_theme.Sf(132f)));
            if (rooms.Count == 0)
                GUILayout.Label(_multiplayer.Busy ? "Looking for rooms…" : "Nobody hosts this game right now. Press Host to open a room.", _theme.Muted);
            for (int i = 0; i < rooms.Count && i < MaxRooms; i++)
            {
                LobbyInfo room = rooms[i];
                bool same = room.ModHash == entry.Mod.ContentHash;
                GUILayout.BeginHorizontal();
                GUILayout.Label(DisplayText.Clip(room.Name.Length > 0 ? room.Name : "Room", 40), _theme.Label, GUILayout.ExpandWidth(true));
                GUILayout.Label(same ? room.Players + " / " + room.Capacity : "v" + DisplayText.Clip(room.ModVersion, 12) + " differs", _theme.Muted, GUILayout.ExpandWidth(false));
                GUI.enabled = same && !_multiplayer.Busy && room.Players < room.Capacity;
                if (GUILayout.Button("Join", _theme.Button, GUILayout.Width(_theme.Sf(64f))))
                    _defer(() => _multiplayer.Join(room));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawNothingSelected()
        {
            if (_host.Catalog.Entries.Count == 0)
            {
                GUILayout.Label("No games were found", _theme.Heading);
                GUILayout.Label("Board Sandbox looked in:", _theme.Wrapped);
                foreach (IModSource source in _host.Catalog.Sources)
                    GUILayout.Label(source.Origin.Label() + ": " + DisplayText.Clip(source.Description, 120), _theme.Muted);
                GUILayout.Label("A game is a folder with a config.json and its .luau files. Put such a folder into one of the places above and press Rescan, or press Create to have an AI agent write one.", _theme.Wrapped);
            }
            GUILayout.FlexibleSpace();
        }

        private void DrawActions(ModEntry entry)
        {
            GUILayout.Space(_theme.Sf(6f));
            GUILayout.BeginHorizontal();
            if (entry != null)
            {
                bool running = _library.IsRunning(entry);
                if (GUILayout.Button(running ? "Resume" : "Play", _theme.Button, GUILayout.Width(_theme.Sf(110f)), GUILayout.Height(_theme.Sf(34f))))
                {
                    if (running)
                        _defer(Close);
                    else
                        _defer(Play);
                }
                if (running && GUILayout.Button("New game", _theme.Button, GUILayout.Width(_theme.Sf(96f)), GUILayout.Height(_theme.Sf(34f))))
                    _defer(Play);
            }
            GUILayout.FlexibleSpace();
            if (_openStudio != null)
            {
                // Only games in a folder of the player can be changed; the ones that come with the app cannot.
                if (entry != null && entry.Origin == ModOrigin.Local && entry.Mod.Directory != null
                    && GUILayout.Button("Edit with AI", _theme.Button, GUILayout.Width(_theme.Sf(100f)), GUILayout.Height(_theme.Sf(34f))))
                {
                    ModEntry edited = entry;
                    _defer(() => _openStudio(edited));
                }
                if (GUILayout.Button("Create", _theme.Button, GUILayout.Width(_theme.Sf(80f)), GUILayout.Height(_theme.Sf(34f))))
                    _defer(() => _openStudio(null));
            }
            if (GUILayout.Button("Rescan", _theme.Button, GUILayout.Width(_theme.Sf(80f)), GUILayout.Height(_theme.Sf(34f))))
                _defer(Rescan);
            GUILayout.EndHorizontal();
            if (entry != null && _detailDeletable)
                DrawDelete(entry);
        }

        // Deleting cannot be taken back, so it asks once more before it happens.
        private void DrawDelete(ModEntry entry)
        {
            GUILayout.Space(_theme.Sf(4f));
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (!_confirmDelete)
            {
                if (GUILayout.Button("Delete", _theme.Button, GUILayout.Width(_theme.Sf(80f)), GUILayout.Height(_theme.Sf(30f))))
                    _defer(() => _confirmDelete = true);
            }
            else
            {
                GUILayout.Label("Delete '" + _detailName + "' and its folder for good?", _theme.Label);
                if (GUILayout.Button("Delete", _theme.Button, GUILayout.Width(_theme.Sf(80f)), GUILayout.Height(_theme.Sf(30f))))
                {
                    ModEntry deleted = entry;
                    _defer(() => Delete(deleted));
                }
                if (GUILayout.Button("Keep", _theme.Button, GUILayout.Width(_theme.Sf(80f)), GUILayout.Height(_theme.Sf(30f))))
                    _defer(() => _confirmDelete = false);
            }
            GUILayout.EndHorizontal();
        }

        private void Delete(ModEntry entry)
        {
            _confirmDelete = false;
            if (_delete == null || !_delete(entry))
                return;
            // The catalog was read again; the list follows it.
            _library.Rescan();
            RefreshFilter();
            _detailFor = null;
            _revealSelected = true;
        }

        // The texts about a game are put together once, not on every pass.
        private void EnsureDetails(ModEntry entry)
        {
            if (ReferenceEquals(_detailFor, entry))
                return;
            _detailFor = entry;
            ModManifest manifest = entry.Mod.Manifest;
            _detailName = DisplayText.Clip(manifest.Name, MaxName);
            _detailInfo = entry.Summary + " · v" + DisplayText.Clip(manifest.Version, 16);
            _detailDescription = manifest.Description.Length > 0 ? DisplayText.Clip(manifest.Description, MaxDescription, false) : "No description.";
            _detailSeats = SeatsText(manifest);
            _detailOptions = OptionsText(manifest);
            _detailScroll = Vector2.zero;
            _detailDeletable = _canDelete != null && _canDelete(entry);
            _confirmDelete = false;
        }

        private static string SeatsText(ModManifest manifest)
        {
            StringBuilder builder = new StringBuilder("Seats: ");
            int shown = Math.Min(manifest.Seats.Count, MaxSeatNames);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(DisplayText.Clip(manifest.Seats[i].Name, 24));
            }
            if (manifest.Seats.Count > shown)
                builder.Append(", …");
            return builder.ToString();
        }

        private static string OptionsText(ModManifest manifest)
        {
            JsonValue options = manifest.DefaultOptions;
            if (options == null || options.Count == 0)
                return string.Empty;
            StringBuilder builder = new StringBuilder("Options:");
            int shown = 0;
            foreach (KeyValuePair<string, JsonValue> option in options.Members)
            {
                if (shown == MaxOptions)
                {
                    builder.Append("\n…");
                    break;
                }
                builder.Append('\n').Append(DisplayText.Clip(option.Key, 24)).Append(" = ").Append(DisplayText.Clip(option.Value.ToJson(), 32));
                shown++;
            }
            return builder.ToString();
        }
    }
}
