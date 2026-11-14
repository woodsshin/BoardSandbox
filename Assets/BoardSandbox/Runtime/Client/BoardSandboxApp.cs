using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Client.Cameras;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Rendering;
using BoardSandbox.Client.UI;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.Agents;
using BoardSandbox.Core.Ai;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Net;
using UnityEngine;

namespace BoardSandbox.Client
{
    // Puts a game on screen: finds the mods, runs a GameHost, draws the board and the interface and turns the
    // mouse and keyboard into commands. Dropping this component into any scene is all it takes.
    [DisallowMultipleComponent]
    public sealed class BoardSandboxApp : MonoBehaviour
    {
        private const float DragThreshold = 6f;
        private const float GestureThreshold = 18f;
        private const double ComputerDelaySeconds = 0.45;

        [SerializeField] private string initialMod = "";
        [SerializeField] private string[] extraModFolders = new string[0];
        [SerializeField] private bool startGame = true;

        // 0 follows the height of the screen.
        [SerializeField] private float uiScale = 0f;

        // One table on screen: a view of it, the camera that looks at it and the part of the screen it gets.
        private sealed class Panel
        {
            public SceneView View;
            public CameraRig Rig;
            public GameObject OwnedCamera;
            public PlaybackQueue Queue;
            public string Title = string.Empty;
            public Rect Area;
            public int FramedVersion = -1;
            public bool OverflowReported;
        }

        private const int MaxPanels = 8;
        private const int FirstPanelLayer = 31;

        private GameHost _host;
        private Multiplayer _multiplayer;
        private ModStudio _studio;
        private float _dock;
        private MaterialSet _materials;
        private readonly List<Panel> _panels = new List<Panel>();
        private Panel _active;
        private Panel _pressPanel;
        private PlaybackQueue _queue;
        private PlaybackQueue _statusSource;
        private ModManifest _manifest;
        private int _boardsVersion = -1;
        private int _mainMask;
        private GuiTheme _theme;
        private Hud _hud;
        private GameObject _ownedCamera;

        private GameSession _boundSession;
        private ViewHints _hints = new ViewHints(null);
        private string _failure;

        private bool _pressing;
        private bool _dragged;
        private int _pressButton;
        private Vector2 _pressPosition;
        private bool _pressOnObject;
        private bool _gestureSent;
        private PickHit _pressHit;
        private bool _hovering;
        private int _hoverX;
        private int _hoverY;

        public GameHost Host => _host;

#if !BSE_NO_AUTOSTART
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Start_()
        {
            if (Environment.GetEnvironmentVariable("BSE_AUTOSTART") == "0")
                return;
            if (FindFirstObjectByType<BoardSandboxApp>() != null)
                return;
            new GameObject("Board Sandbox").AddComponent<BoardSandboxApp>();
        }
#endif

        private void Awake()
        {
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void OnDestroy()
        {
            Unbind();
            if (_host != null)
                _host.SessionReplaced -= OnSessionReplaced;
            if (_multiplayer != null)
                _multiplayer.RoomChanged -= OnRoomChanged;
            _multiplayer?.Dispose();
            _studio?.Dispose();
            foreach (Panel panel in _panels)
            {
                panel.View.Dispose();
                if (panel.OwnedCamera != null)
                    ObjectUtil.Destroy(panel.OwnedCamera);
            }
            _panels.Clear();
            _materials?.Dispose();
            _theme?.Dispose();
            _host?.Dispose();
            if (_ownedCamera != null)
                ObjectUtil.Destroy(_ownedCamera);
        }

        private void Build()
        {
            _theme = new GuiTheme();
            _materials = new MaterialSet();
            _queue = new PlaybackQueue();
            Camera main = ResolveCamera();
            _mainMask = main.cullingMask;
            _panels.Add(new Panel { View = new SceneView(_materials), Rig = new CameraRig(main), Queue = _queue });
            _active = _panels[0];
            Listen(_queue);

            // The computer player gets stronger with the games played against it, also after the app was closed.
            AiProgress progress = new AiProgress(Path.Combine(Application.persistentDataPath, "ai_progress.json"));
            progress.Load();
            _host = new GameHost(new ModCatalog(ModSources()),
                new GameHostOptions { AiDelaySeconds = ComputerDelaySeconds, AiProgress = progress });
            if (progress.Problem.Length > 0)
                _host.Toasts.Add(progress.Problem, ToastLevel.Warning, _host.Now);
            _host.SessionReplaced += OnSessionReplaced;
            _multiplayer = new Multiplayer(_host, CreateLobbyBackend());
            _multiplayer.RoomChanged += OnRoomChanged;

            // Games written in the AI console go into the player's own folder of games, which ModSources lists.
            AgentSettings agents = new AgentSettings(Path.Combine(Application.persistentDataPath, "agents.json"));
            agents.Load();
            if (agents.Problem.Length > 0)
                _host.Toasts.Add(agents.Problem, ToastLevel.Warning, _host.Now);
            _studio = new ModStudio(_host, LocalModsFolder, agents);
            _hud = new Hud(_host, _multiplayer, _studio, _theme, () => (_active ?? _panels[0]).Rig.Reframe());

            foreach (ModLoadError error in _host.Catalog.Errors)
                _host.Toasts.Add("Could not load " + Path.GetFileName(error.Directory) + ": " + error.Message, ToastLevel.Error, _host.Now);

            string requested = initialMod == null ? string.Empty : initialMod.Trim();
            ModEntry first = _host.Catalog.FindStartup(requested);
            if (requested.Length > 0 && first == null)
                _host.Toasts.Add("The game '" + requested + "' was not found.", ToastLevel.Warning, _host.Now);
            if (first != null)
            {
                if (startGame)
                    _host.StartGame(first.Mod);
                else
                    _host.SelectMod(first.Mod);
            }
            if (_host.Session == null)
                _hud.OpenLibrary();
        }

        private static ILobbyBackend CreateLobbyBackend()
        {
#if BSE_STEAM && UNITY_STANDALONE_WIN
            return new Steam.SteamBackend();
#else
            return null;
#endif
        }

        private Camera ResolveCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
                camera = FindFirstObjectByType<Camera>();
            if (camera != null)
                return camera;

            _ownedCamera = new GameObject("Board Camera");
            _ownedCamera.tag = "MainCamera";
            return _ownedCamera.AddComponent<Camera>();
        }

        // The games that come with the app are listed first, so that nothing else can take their ids.
        private List<IModSource> ModSources()
        {
            List<IModSource> sources = new List<IModSource>
            {
                new FolderModSource(Path.Combine(Application.streamingAssetsPath, "Mods"), ModOrigin.BuiltIn)
            };
            string environment = Environment.GetEnvironmentVariable("BSE_MODS_DIR");
            if (!string.IsNullOrEmpty(environment))
            {
                foreach (string part in environment.Split(Path.PathSeparator))
                    AddLocalFolder(sources, part);
            }
            if (extraModFolders != null)
            {
                foreach (string folder in extraModFolders)
                    AddLocalFolder(sources, folder);
            }
            AddLocalFolder(sources, LocalModsFolder);
            return sources;
        }

        private static string LocalModsFolder => Path.Combine(Application.persistentDataPath, "Mods");

        private static void AddLocalFolder(List<IModSource> sources, string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
                sources.Add(new FolderModSource(folder, ModOrigin.Local));
        }

        private void OnSessionReplaced()
        {
            Unbind();
            _boundSession = _host.Session;
            _queue.Reset(_boundSession?.View);
            if (_boundSession != null)
            {
                _boundSession.EventApplied += _queue.Enqueue;
                _boundSession.ScriptLog += OnScriptLog;
                _boundSession.HandlerFailed += OnHandlerFailed;
            }
            // Choosing a game of one's own while being a guest somewhere means leaving there.
            if (_multiplayer != null && _multiplayer.Room != null && !_multiplayer.Room.IsHost)
                _multiplayer.Leave();
            else
                Arrange();
        }

        private void OnRoomChanged()
        {
            Arrange();
        }

        // Decides which tables are on screen: the one of the local game, the one a room shares, or one per player
        // of a split game with the local player's first.
        private void Arrange()
        {
            NetRoom room = _multiplayer?.Room;
            _boardsVersion = room != null ? room.BoardsVersion : -1;
            _manifest = room != null ? room.Mod.Manifest : _boundSession?.Mod.Manifest;
            _hints = new ViewHints(_manifest?.Client);
            _hud.Note = _manifest == null || _hints.CanClick || _hints.CanDrag || _hints.KeyCount > 0 ? string.Empty
                : "Clicks do nothing in this game: its config.json has no client.click.action. Use the console command 'play'.";

            List<PlaybackQueue> queues = new List<PlaybackQueue>();
            List<string> titles = new List<string>();
            PlaybackQueue acting = _queue;
            if (room == null)
            {
                queues.Add(_queue);
                titles.Add(string.Empty);
            }
            else
            {
                NetBoard own = room.LocalBoard;
                acting = own?.Queue;
                if (own != null)
                {
                    queues.Add(own.Queue);
                    titles.Add(room.SplitScreen ? own.Title + " (you)" : string.Empty);
                }
                foreach (NetBoard board in room.Boards)
                {
                    if (board != own && queues.Count < MaxPanels)
                    {
                        queues.Add(board.Queue);
                        titles.Add(board.Title);
                    }
                }
                if (queues.Count == 0)
                {
                    queues.Add(_queue);
                    titles.Add(string.Empty);
                    acting = null;
                }
            }

            while (_panels.Count > queues.Count)
            {
                Panel last = _panels[_panels.Count - 1];
                last.View.Dispose();
                ObjectUtil.Destroy(last.OwnedCamera);
                _panels.RemoveAt(_panels.Count - 1);
            }
            Camera main = _panels[0].Rig.Camera;
            while (_panels.Count < queues.Count)
            {
                int layer = FirstPanelLayer + 1 - _panels.Count;
                GameObject holder = new GameObject("Board Camera " + (_panels.Count + 1));
                Camera camera = holder.AddComponent<Camera>();
                camera.depth = main.depth;
                camera.cullingMask = 1 << layer;
                _panels.Add(new Panel { View = new SceneView(_materials, layer), Rig = new CameraRig(camera), OwnedCamera = holder });
            }

            int extraLayers = 0;
            for (int i = 1; i < _panels.Count; i++)
                extraLayers |= 1 << (FirstPanelLayer + 1 - i);
            main.cullingMask = _mainMask & ~extraLayers;

            Place();
            _active = null;
            for (int i = 0; i < _panels.Count; i++)
            {
                Panel panel = _panels[i];
                panel.Title = titles[i];
                if (!ReferenceEquals(panel.Queue, queues[i]) || !ReferenceEquals(panel.View.Manifest, _manifest))
                {
                    panel.Queue = queues[i];
                    panel.FramedVersion = -1;
                    panel.OverflowReported = false;
                    panel.View.Bind(_hints, _manifest);
                }
                if (ReferenceEquals(panel.Queue, acting))
                    _active = panel;
            }
            Listen(acting);
            _pressing = false;
            _pressPanel = null;
        }

        // Gives every table its part of the screen: a grid in what the AI console leaves free at the right edge.
        private void Place()
        {
            float free = 1f - _dock;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(_panels.Count));
            int rows = Mathf.CeilToInt(_panels.Count / (float)columns);
            for (int i = 0; i < _panels.Count; i++)
                _panels[i].Rig.Camera.rect = new Rect((i % columns) / (float)columns * free, 1f - (i / columns + 1) / (float)rows, free / columns, 1f / rows);
        }

        // The messages of the game the local player plays become toasts.
        private void Listen(PlaybackQueue queue)
        {
            if (ReferenceEquals(queue, _statusSource))
                return;
            if (_statusSource != null)
                _statusSource.Played -= OnEvent;
            _statusSource = queue;
            if (queue != null)
                queue.Played += OnEvent;
        }

        private void Unbind()
        {
            if (_boundSession == null)
                return;
            _boundSession.EventApplied -= _queue.Enqueue;
            _boundSession.ScriptLog -= OnScriptLog;
            _boundSession.HandlerFailed -= OnHandlerFailed;
            _boundSession = null;
        }

        private void OnEvent(ViewEvent e)
        {
            if (e.Kind != ViewEventKind.Status || e.Text.Length == 0)
                return;
            ToastLevel level = e.Level == "error" ? ToastLevel.Error : e.Level == "warning" || e.Level == "warn" ? ToastLevel.Warning : ToastLevel.Info;
            _host.Toasts.Add(e.Text, level, _host.Now);
        }

        private void OnScriptLog(LogLine line)
        {
            _hud?.AppendLog(line.ToString());
        }

        private void OnHandlerFailed(Exception e)
        {
            _host.Toasts.Add("The game script failed: " + e.Message, ToastLevel.Error, _host.Now);
        }

        private void Update()
        {
            if (_failure != null)
                return;
            try
            {
                _hud.RunDeferred();
                _studio.Update();
                _multiplayer.Update();
                NetRoom room = _multiplayer.Room;
                _host.AiPaused = _hud.ModalOpen || (room != null && !room.IsHost);
                _host.Update();
                if (room != null)
                {
                    // The room shows the local game through a queue of its own.
                    _queue.Finish();
                    if (room.BoardsVersion != _boardsVersion)
                        Arrange();
                }

                float dock = _hud.DockFraction;
                if (!Mathf.Approximately(dock, _dock))
                {
                    _dock = dock;
                    Place();
                }

                float delta = Time.unscaledDeltaTime;
                foreach (Panel panel in _panels)
                {
                    panel.Queue.Update(delta);
                    panel.View.Sync(panel.Queue.Display);
                    panel.View.SetEffect(panel.Queue.ActiveEffect, panel.Queue.Progress);
                    if (panel.View.Overflowed && !panel.OverflowReported)
                    {
                        panel.OverflowReported = true;
                        _host.Toasts.Add("This game has more than " + SceneView.MaxPieces + " pieces; the rest are not drawn.", ToastLevel.Warning, _host.Now);
                    }
                    panel.Rig.Background = panel.View.Stage.Background;
                    if (panel.View.FrameVersion != panel.FramedVersion)
                    {
                        bool first = panel.FramedVersion < 0;
                        panel.FramedVersion = panel.View.FrameVersion;
                        panel.Rig.Frame(panel.View.Stage.Camera, panel.View.Layout, first);
                    }
                    panel.View.Tick(delta);
                    panel.Rig.Tick(delta);
                    Rect pixels = panel.Rig.Camera.pixelRect;
                    panel.Area = new Rect(pixels.x, Screen.height - pixels.yMax, pixels.width, pixels.height);
                }
                UpdatePreview();
                _hud.Snapshot(_host.Now);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void UpdatePreview()
        {
            foreach (Panel panel in _panels)
            {
                bool show = panel == _active && _hovering && _hints.CanClick && !_hints.ClickObjects && InputOpen && !_pressing;
                Color32 color = show ? ObjectLook.SeatColor(_manifest, ActingSeat) : new Color32(255, 255, 255, 255);
                panel.View.SetPreview(show, _hoverX, _hoverY, color);
            }
        }

        private int ActingSeat
        {
            get
            {
                NetRoom room = _multiplayer.Room;
                if (room != null)
                    return room.ActingSeat;
                return _host.Session != null ? _host.Session.View.Turn : 0;
            }
        }

        private Panel PanelAt(Vector2 guiPoint)
        {
            foreach (Panel panel in _panels)
            {
                if (panel.Area.Contains(guiPoint))
                    return panel;
            }
            return null;
        }

        private void DrawTitles()
        {
            if (_panels.Count < 2 || Event.current.type != EventType.Repaint)
                return;
            foreach (Panel panel in _panels)
            {
                Rect area = panel.Area;
                GUI.Label(new Rect(area.x, area.yMax - _theme.Sf(34f), area.width, _theme.Sf(30f)), DisplayText.Clip(panel.Title, 40), _theme.PanelTitle);
            }
        }

        private void OnGUI()
        {
            if (_failure != null)
            {
                DrawFailure();
                return;
            }
            try
            {
                _theme.Ensure(uiScale > 0f ? uiScale : Mathf.Round(Mathf.Clamp(Screen.height / 900f, 1f, 2.5f) * 4f) / 4f);
                Event e = Event.current;
                DrawTitles();
                _hud.Draw();
                if (e.type == EventType.Repaint)
                    UpdateHover(e.mousePosition);
                HandlePointer(e);
                HandleKeys(e);
            }
            catch (Exception ex) when (!(ex is ExitGUIException))
            {
                Fail(ex);
            }
        }

        private void HandlePointer(Event e)
        {
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (_hud.Contains(e.mousePosition))
                        return;
                    _pressPanel = PanelAt(e.mousePosition);
                    if (_pressPanel == null)
                        return;
                    GUI.FocusControl(null);
                    _pressing = true;
                    _dragged = false;
                    _pressButton = e.button;
                    _pressPosition = e.mousePosition;
                    _gestureSent = false;
                    _pressOnObject = e.button == 0 && _pressPanel == _active && _hints.CanDrag && InputOpen
                        && _active.View.TryPickObject(_active.Rig.RayFromScreen(GuiToScreen(e.mousePosition)), out _pressHit);
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!_pressing || _pressPanel == null)
                        return;
                    if ((e.mousePosition - _pressPosition).magnitude > DragThreshold)
                        _dragged = true;
                    if (_pressOnObject)
                    {
                        if (!_gestureSent && (e.mousePosition - _pressPosition).magnitude > GestureThreshold)
                        {
                            _gestureSent = true;
                            Vector2 to = e.mousePosition;
                            _hud.Defer(() => DragObject(to));
                        }
                    }
                    else if (_dragged || _pressButton != 0)
                    {
                        CameraSettings camera = _pressPanel.View.Stage.Camera;
                        _pressPanel.Rig.Drag(_pressButton == 0 ? camera.LeftDrag : _pressButton == 1 ? camera.RightDrag : camera.MiddleDrag, e.delta,
                            _pressPanel.Area.height);
                    }
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (!_pressing || e.button != _pressButton)
                        return;
                    _pressing = false;
                    if (!_dragged && _pressPanel == _active && (_pressButton == 0 || (_pressButton == 1 && _hints.ClickObjects)))
                    {
                        Vector2 at = e.mousePosition;
                        int button = _pressButton;
                        bool shift = e.shift;
                        _hud.Defer(() => Click(at, button, shift));
                    }
                    e.Use();
                    break;

                case EventType.ScrollWheel:
                    if (_hud.Contains(e.mousePosition))
                        return;
                    Panel under = PanelAt(e.mousePosition);
                    if (under == null)
                        return;
                    under.Rig.Zoom(e.delta.y > 0f ? 1f : -1f);
                    e.Use();
                    break;
            }
        }

        private void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown || _hud.TextFocused || _hud.ModalOpen)
                return;
            bool modifier = e.control || e.command;
            bool guest = _multiplayer.Room != null && !_multiplayer.Room.IsHost;
            JsonValue bound = modifier || e.alt ? null : _hints.KeyCommand(KeyName(e.keyCode), e.shift);
            if (bound != null)
            {
                _hud.Defer(() => SubmitHuman(bound.DeepClone()));
                e.Use();
                return;
            }
            switch (e.keyCode)
            {
                case KeyCode.F:
                    foreach (Panel panel in _panels)
                        panel.Rig.Reframe();
                    break;
                case KeyCode.G:
                    _hud.Defer(_hud.OpenLibrary);
                    break;
                case KeyCode.N when !guest:
                    _hud.Defer(() => _host.StartNewGame());
                    break;
                case KeyCode.Z when modifier && !e.shift && !guest:
                case KeyCode.LeftArrow when !guest:
                    _hud.Defer(() => _host.Undo());
                    break;
                case KeyCode.Y when modifier && !guest:
                case KeyCode.Z when modifier && e.shift && !guest:
                case KeyCode.RightArrow when !guest:
                    _hud.Defer(() => _host.Redo());
                    break;
                default:
                    return;
            }
            e.Use();
        }

        private void UpdateHover(Vector2 mouse)
        {
            _hovering = false;
            if (_pressing && _dragged)
                return;
            if (_active == null || _hints.ClickObjects || _hud.Contains(mouse) || !_active.Area.Contains(mouse)
                || !_active.View.TryPickCell(_active.Rig.RayFromScreen(GuiToScreen(mouse)), out int x, out int y))
                return;
            _hovering = true;
            _hoverX = x;
            _hoverY = y;
        }

        private bool InputOpen
        {
            get
            {
                if (_active == null || _active.Queue.IsBusy)
                    return false;
                NetRoom room = _multiplayer.Room;
                return room != null ? room.CanAct : _host.AcceptsHumanInput;
            }
        }

        private void SubmitHuman(JsonValue command)
        {
            if (!InputOpen)
                return;
            NetRoom room = _multiplayer.Room;
            if (room != null)
                room.Submit(command);
            else
                _host.Submit(_host.Session.View.Turn, command, "human");
        }

        private void Click(Vector2 guiPoint, int button, bool shift)
        {
            if (!_hints.CanClick || !InputOpen)
                return;
            Ray ray = _active.Rig.RayFromScreen(GuiToScreen(guiPoint));
            JsonValue command = JsonValue.NewObject().Set("action", _hints.ClickAction);
            if (_hints.ClickObjects)
            {
                if (!_active.View.TryPickObject(ray, out PickHit hit))
                    return;
                command.Set("id", hit.Id).Set("point", ModVector(hit.Point)).Set("normal", ModVector(hit.Normal)).Set("button", button).Set("shift", shift);
            }
            else
            {
                if (button != 0 || !_active.View.TryPickCell(ray, out int x, out int y))
                    return;
                command.Set("x", x).Set("y", y);
            }
            SubmitHuman(command);
        }

        // A drag that began on an object: the command names the object, where it was touched and which way
        // the cursor went along the touched surface.
        private void DragObject(Vector2 guiPoint)
        {
            if (!_hints.CanDrag || !InputOpen)
                return;
            Ray ray = _active.Rig.RayFromScreen(GuiToScreen(guiPoint));
            if (!ScenePicker.RayPlane(ray.origin, ray.direction, _pressHit.Point, _pressHit.Normal, out Vector3 reached))
                return;
            Vector3 along = reached - _pressHit.Point;
            along -= _pressHit.Normal * Vector3.Dot(along, _pressHit.Normal);
            if (along.sqrMagnitude < 1e-8f)
                return;
            JsonValue command = JsonValue.NewObject().Set("action", _hints.DragAction).Set("id", _pressHit.Id)
                .Set("point", ModVector(_pressHit.Point)).Set("normal", ModVector(_pressHit.Normal)).Set("drag", ModVector(along.normalized));
            SubmitHuman(command);
        }

        private static JsonValue ModVector(Vector3 scene)
        {
            Vector3 v = Frame.Flip(scene);
            return JsonValue.NewArray().Add(Math.Round(v.x, 4)).Add(Math.Round(v.y, 4)).Add(Math.Round(v.z, 4));
        }

        private static string KeyName(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((int)(key - KeyCode.Alpha0)).ToString();
            return key == KeyCode.None ? string.Empty : key.ToString();
        }

        // GUI coordinates have their origin at the top left, the screen's at the bottom left.
        private static Vector2 GuiToScreen(Vector2 guiPoint)
        {
            return new Vector2(guiPoint.x, Screen.height - guiPoint.y);
        }

        private void Fail(Exception e)
        {
            _failure = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        private void DrawFailure()
        {
            GUI.color = new Color(1f, 0.55f, 0.5f);
            GUI.Label(new Rect(20f, 20f, Screen.width - 40f, Screen.height - 40f), "Board Sandbox stopped.\n\n" + _failure);
            GUI.color = Color.white;
        }
    }
}
