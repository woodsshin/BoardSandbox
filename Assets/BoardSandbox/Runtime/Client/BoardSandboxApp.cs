using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Client.Cameras;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Rendering;
using BoardSandbox.Client.UI;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using UnityEngine;

namespace BoardSandbox.Client
{
    // Puts a game on screen: finds the mods, runs a GameHost, draws the board and the interface and turns the
    // mouse and keyboard into commands. Dropping this component into any scene is all it takes.
    [DisallowMultipleComponent]
    public sealed class BoardSandboxApp : MonoBehaviour
    {
        private const float DragThreshold = 6f;
        private const double ComputerDelaySeconds = 0.45;

        [SerializeField] private string initialMod = "";
        [SerializeField] private string[] extraModFolders = new string[0];
        [SerializeField] private bool startGame = true;

        // 0 follows the height of the screen.
        [SerializeField] private float uiScale = 0f;

        private GameHost _host;
        private MaterialSet _materials;
        private GridBoardView _view;
        private CameraRig _rig;
        private GuiTheme _theme;
        private Hud _hud;
        private GameObject _ownedCamera;

        private GameSession _boundSession;
        private ViewHints _hints = new ViewHints(null);
        private int _framedLayout = -1;
        private bool _overflowReported;
        private string _failure;

        private bool _pressing;
        private bool _dragged;
        private int _pressButton;
        private Vector2 _pressPosition;
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
            _view?.Dispose();
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
            _view = new GridBoardView(_materials);
            _rig = new CameraRig(ResolveCamera());

            _host = new GameHost(new ModCatalog(ModSources()), new GameHostOptions { AiDelaySeconds = ComputerDelaySeconds });
            _host.SessionReplaced += OnSessionReplaced;
            _hud = new Hud(_host, _theme, () => _rig.Reframe());

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
            AddLocalFolder(sources, Path.Combine(Application.persistentDataPath, "Mods"));
            return sources;
        }

        private static void AddLocalFolder(List<IModSource> sources, string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
                sources.Add(new FolderModSource(folder, ModOrigin.Local));
        }

        private void OnSessionReplaced()
        {
            Unbind();
            _boundSession = _host.Session;
            if (_boundSession == null)
                return;
            _boundSession.EventApplied += OnEvent;
            _boundSession.ScriptLog += OnScriptLog;
            _boundSession.HandlerFailed += OnHandlerFailed;

            ModManifest manifest = _boundSession.Mod.Manifest;
            _hints = new ViewHints(manifest.Client);
            _overflowReported = false;
            _hud.Note = _hints.CanClick ? string.Empty : "Clicks do nothing in this game: its config.json has no client.click.action. Use the console command 'play'.";
            _view.Bind(_hints, manifest);
        }

        private void Unbind()
        {
            if (_boundSession == null)
                return;
            _boundSession.EventApplied -= OnEvent;
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
                _host.AiPaused = _hud.ModalOpen;
                _host.Update();

                GameSession session = _host.Session;
                _view.Sync(session?.View);
                if (_view.Overflowed && !_overflowReported)
                {
                    _overflowReported = true;
                    _host.Toasts.Add("This game has more than " + GridBoardView.MaxPieces + " pieces; the rest are not drawn.", ToastLevel.Warning, _host.Now);
                }
                if (_view.Layout != null && _view.LayoutVersion != _framedLayout)
                {
                    bool first = _framedLayout < 0;
                    _framedLayout = _view.LayoutVersion;
                    _rig.Frame(_view.Layout, first);
                }

                float delta = Time.unscaledDeltaTime;
                _view.Tick(delta);
                _rig.Tick(delta);
                UpdatePreview(session);
                _hud.Snapshot(_host.Now);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void UpdatePreview(GameSession session)
        {
            bool show = _hovering && _hints.CanClick && _host.AcceptsHumanInput && !_pressing;
            Color32 color = show ? PieceStyle.SeatColor(session.Mod.Manifest, session.View.Turn) : new Color32(255, 255, 255, 255);
            _view.SetPreview(show, _hoverX, _hoverY, color);
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
                    GUI.FocusControl(null);
                    _pressing = true;
                    _dragged = false;
                    _pressButton = e.button;
                    _pressPosition = e.mousePosition;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!_pressing)
                        return;
                    if ((e.mousePosition - _pressPosition).magnitude > DragThreshold)
                        _dragged = true;
                    if (_dragged || _pressButton != 0)
                    {
                        if (_pressButton == 1)
                            _rig.Orbit(e.delta);
                        else
                            _rig.Pan(e.delta, Screen.height);
                    }
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (!_pressing || e.button != _pressButton)
                        return;
                    _pressing = false;
                    if (_pressButton == 0 && !_dragged)
                    {
                        Vector2 at = e.mousePosition;
                        _hud.Defer(() => ClickBoard(at));
                    }
                    e.Use();
                    break;

                case EventType.ScrollWheel:
                    if (_hud.Contains(e.mousePosition))
                        return;
                    _rig.Zoom(e.delta.y > 0f ? 1f : -1f);
                    e.Use();
                    break;
            }
        }

        private void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown || _hud.TextFocused || _hud.ModalOpen)
                return;
            bool modifier = e.control || e.command;
            switch (e.keyCode)
            {
                case KeyCode.F:
                    _rig.Reframe();
                    break;
                case KeyCode.G:
                    _hud.Defer(_hud.OpenLibrary);
                    break;
                case KeyCode.N:
                    _hud.Defer(() => _host.StartNewGame());
                    break;
                case KeyCode.Z when modifier && !e.shift:
                case KeyCode.LeftArrow:
                    _hud.Defer(() => _host.Undo());
                    break;
                case KeyCode.Y when modifier:
                case KeyCode.Z when modifier && e.shift:
                case KeyCode.RightArrow:
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
            if (_hud.Contains(mouse) || !_view.TryPick(_rig.RayFromScreen(GuiToScreen(mouse)), out int x, out int y))
                return;
            _hovering = true;
            _hoverX = x;
            _hoverY = y;
        }

        private void ClickBoard(Vector2 guiPoint)
        {
            if (!_hints.CanClick || !_host.AcceptsHumanInput)
                return;
            if (!_view.TryPick(_rig.RayFromScreen(GuiToScreen(guiPoint)), out int x, out int y))
                return;
            JsonValue command = JsonValue.NewObject().Set("action", _hints.ClickAction).Set("x", x).Set("y", y);
            _host.Submit(_host.Session.View.Turn, command, "human");
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
