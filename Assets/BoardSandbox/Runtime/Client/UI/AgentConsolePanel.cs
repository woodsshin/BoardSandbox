using System;
using System.Collections.Generic;
using BoardSandbox.Core.Agents;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Mods;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The AI console: a window at the right edge in which the player tells an agent what game to write. The table
    // stays visible beside it, so that every change can be seen and tried at once. Like the other panels it only
    // reads state while it draws and changes it through the deferral it was given.
    public sealed class AgentConsolePanel
    {
        private const string InputField = "bse-agent-input";
        private const float Margin = 12f;
        private const int MaxShownLines = 300;
        private const int MaxInput = 4000;

        // How many frames a request waits after Enter before it is sent. A syllable that the input method was still
        // putting together is finished by Enter and arrives as a character a moment after the key.
        private const int SubmitFrames = 2;

        private readonly ModStudio _studio;
        private readonly GuiTheme _theme;
        private readonly Func<double> _clock;
        private readonly Action<Action> _defer;
        private readonly Action _releaseFocus;
        private readonly List<string> _recalled = new List<string>();

        private string _input = string.Empty;
        private string _keyText = string.Empty;
        private string _modelText = string.Empty;
        private string _fieldsFor = string.Empty;
        private Vector2 _scroll;
        private Vector2 _agentScroll;
        private int _shownVersion = -1;
        private int _recallIndex;
        private bool _focusInput;
        private bool _inputFocused;
        private int _submitIn;
        private bool _drawn;
        private bool _choosing;
        private bool _showSetup;
        private string _state = string.Empty;
        private int _busySeconds;

        public AgentConsolePanel(ModStudio studio, GuiTheme theme, Func<double> clock, Action<Action> defer, Action releaseFocus)
        {
            _studio = studio ?? throw new ArgumentNullException(nameof(studio));
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _defer = defer ?? throw new ArgumentNullException(nameof(defer));
            _releaseFocus = releaseFocus ?? throw new ArgumentNullException(nameof(releaseFocus));
        }

        public bool IsOpen { get; private set; }

        // Whether anything was ever started in the console, so that it can be opened again as it was left.
        public bool HasSession => _studio.Lines.Count > 0 || _studio.Workspace != null;

        // The part of the screen the window takes, in GUI coordinates; empty while it is closed.
        public Rect Area { get; private set; }

        // Call these between two frames, not from inside OnGUI.
        public void OpenNew()
        {
            _studio.BeginNew();
            Show();
        }

        public void OpenEdit(ModPackage mod)
        {
            _studio.BeginEdit(mod);
            Show();
        }

        public void Show()
        {
            if (!HasSession)
                _studio.BeginNew();
            IsOpen = true;
            _state = _studio.Readiness;
            _focusInput = true;
            _choosing = false;
            _shownVersion = -1;
        }

        public void Close()
        {
            IsOpen = false;
            _submitIn = 0;
            _inputFocused = false;
            ImeSupport.Prefer(false);
            Area = default;
            _releaseFocus();
        }

        // Samples what changes with time; call it once per frame before OnGUI, so that every pass of a frame draws
        // the same controls.
        public void Snapshot()
        {
            if (!IsOpen)
                return;
            _state = _studio.Readiness;
            _busySeconds = _studio.Busy ? Mathf.Max(0, (int)(_clock() - _studio.BusySince)) : 0;

            // While something else covers the console its field cannot have the keyboard.
            if (!_drawn && _inputFocused)
            {
                _inputFocused = false;
                ImeSupport.Prefer(false);
            }
            _drawn = false;

            // The request goes out once the last syllable has become part of the text.
            if (_submitIn > 0 && !ImeSupport.Composing && --_submitIn == 0)
                SubmitInput();
        }

        public void Draw()
        {
            Event e = Event.current;
            _drawn = true;

            // The text area uses up the keys it understands, so these are looked at before it is drawn.
            if (_inputFocused && e.type == EventType.KeyDown)
                HandleInputKey(e);

            float width = Mathf.Clamp(Screen.width * 0.36f, _theme.Sf(380f), _theme.Sf(640f));
            width = Mathf.Min(width, Screen.width - 2f * Margin);
            Rect area = new Rect(Screen.width - width - Margin, Margin, width, Screen.height - 2f * Margin);
            if (e.type == EventType.Repaint)
                Area = area;

            if (e.type == EventType.Layout && _shownVersion != _studio.Version)
            {
                _shownVersion = _studio.Version;
                _scroll.y = float.MaxValue;
            }

            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(_theme.Panel, GUILayout.Width(width), GUILayout.Height(area.height));
            DrawHeader();
            DrawAgent();
            DrawLines();
            DrawInput();
            GUILayout.EndVertical();
            GUILayout.EndArea();

            if (e.type == EventType.Repaint)
            {
                if (_focusInput)
                {
                    GUI.FocusControl(InputField);
                    _focusInput = false;
                }
                _inputFocused = GUI.GetNameOfFocusedControl() == InputField;
                ImeSupport.Prefer(_inputFocused);
            }
        }

        private void DrawHeader()
        {
            ModWorkspace workspace = _studio.Workspace;
            GUILayout.BeginHorizontal();
            GUILayout.Label("AI console", _theme.Heading, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Close", _theme.Button, GUILayout.Width(_theme.Sf(80f))))
                _defer(Close);
            GUILayout.EndHorizontal();
            GUILayout.Label(workspace != null ? "Game: " + workspace.Id : "New game (made with the first request)", _theme.Muted);
        }

        private void DrawAgent()
        {
            AgentSettings settings = _studio.Settings;
            AgentProfile profile = settings.Selected;
            string state = _state;
            bool ready = state.Length == 0;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Agent", _theme.Label, GUILayout.Width(_theme.Sf(48f)));
            bool choosing = GUILayout.Toggle(_choosing, DisplayText.Clip(profile.Name, 28), _theme.Button, GUILayout.ExpandWidth(true));
            if (choosing != _choosing)
                _defer(() => _choosing = choosing);
            bool setup = GUILayout.Toggle(_showSetup || !ready, "Setup", _theme.Button, GUILayout.Width(_theme.Sf(70f)));
            if (ready && setup != _showSetup)
                _defer(() => _showSetup = setup);
            GUILayout.EndHorizontal();

            if (_choosing)
                DrawChoices(settings, profile);
            if (!ready)
                GUILayout.Label(DisplayText.Clip(state, 200, false), _theme.ConsoleError);
            if (!ready || _showSetup)
                DrawSetup(settings, profile);
        }

        private void DrawChoices(AgentSettings settings, AgentProfile selected)
        {
            IReadOnlyList<AgentProfile> profiles = settings.Profiles;
            float height = Mathf.Min(profiles.Count, 8) * _theme.Sf(32f);
            _agentScroll = GUILayout.BeginScrollView(_agentScroll, GUILayout.Height(height));
            for (int i = 0; i < profiles.Count && i < 40; i++)
            {
                AgentProfile profile = profiles[i];
                bool current = ReferenceEquals(profile, selected);
                string label = DisplayText.Clip(profile.Name, 28) + (profile.Kind == AgentKind.Cli ? "   (program)" : "   (web)");
                if (GUILayout.Toggle(current, label, _theme.Button) != current && !current)
                {
                    string id = profile.Id;
                    _defer(() =>
                    {
                        if (_studio.Busy)
                            _studio.Submit("/agent " + id);
                        else
                            _studio.SelectAgent(id);
                        _choosing = false;
                        _showSetup = false;
                    });
                }
            }
            GUILayout.EndScrollView();
        }

        // The key and the model of the chosen agent, for the agents that have them.
        private void DrawSetup(AgentSettings settings, AgentProfile profile)
        {
            if (_fieldsFor != profile.Id)
            {
                // The fields are filled between two frames, so that both passes of a frame draw the same text.
                string id = profile.Id;
                string model = settings.ModelFor(profile);
                _defer(() =>
                {
                    _fieldsFor = id;
                    _modelText = model;
                    _keyText = string.Empty;
                });
            }

            if (profile.Hint.Length > 0)
                GUILayout.Label(DisplayText.Clip(profile.Hint, 400, false), _theme.Muted);
            if (profile.KeyVariable.Length > 0 || profile.Kind == AgentKind.Api)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Key", _theme.Label, GUILayout.Width(_theme.Sf(48f)));
                _keyText = GUILayout.PasswordField(_keyText, '*', 400, _theme.Field);
                GUI.enabled = _keyText.Trim().Length > 0;
                if (GUILayout.Button("Save", _theme.Button, GUILayout.Width(_theme.Sf(70f))))
                {
                    string key = _keyText.Trim();
                    _defer(() =>
                    {
                        _studio.Submit("/key " + key);
                        _keyText = string.Empty;
                    });
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.Label(settings.KeyFor(profile).Length > 0 ? "A key is set." : profile.KeyOptional ? "No key is needed." : "No key yet.", _theme.Muted);
            }
            if (profile.Kind == AgentKind.Api)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Model", _theme.Label, GUILayout.Width(_theme.Sf(48f)));
                _modelText = GUILayout.TextField(_modelText, 120, _theme.Field);
                GUI.enabled = _modelText.Trim().Length > 0 && _modelText.Trim() != settings.ModelFor(profile);
                if (GUILayout.Button("Set", _theme.Button, GUILayout.Width(_theme.Sf(70f))))
                {
                    string model = _modelText.Trim();
                    _defer(() => _studio.Submit("/model " + model));
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawLines()
        {
            IReadOnlyList<StudioLine> lines = _studio.Lines;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            for (int i = Math.Max(0, lines.Count - MaxShownLines); i < lines.Count; i++)
            {
                StudioLine line = lines[i];
                switch (line.Kind)
                {
                    case StudioLineKind.Prompt:
                        GUILayout.Label("> " + line.Text, _theme.ConsolePrompt);
                        break;
                    case StudioLineKind.Agent:
                        GUILayout.Label(line.Text.Length > 0 ? line.Text : " ", _theme.ConsoleAgent);
                        break;
                    case StudioLineKind.Error:
                        GUILayout.Label("! " + line.Text, _theme.ConsoleError);
                        break;
                    default:
                        GUILayout.Label(line.Text.Length > 0 ? line.Text : " ", _theme.ConsoleInfo);
                        break;
                }
            }
            GUILayout.EndScrollView();
        }

        private void DrawInput()
        {
            bool busy = _studio.Busy;
            if (busy)
            {
                int seconds = _busySeconds;
                GUILayout.BeginHorizontal();
                GUILayout.Label("The agent is working" + new string('.', seconds % 4) + "   " + seconds + " s", _theme.Muted, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Cancel", _theme.Button, GUILayout.Width(_theme.Sf(80f))))
                    _defer(_studio.Cancel);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputField);
            _input = GUILayout.TextArea(_input, MaxInput, _theme.Area, GUILayout.MinHeight(_theme.Sf(54f)), GUILayout.MaxHeight(_theme.Sf(140f)));
            GUI.enabled = _input.Trim().Length > 0;
            if (GUILayout.Button("Send", _theme.Button, GUILayout.Width(_theme.Sf(70f)), GUILayout.Height(_theme.Sf(54f))))
                RequestSubmit();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Enter  send      Shift+Enter  new line      /help  commands", _theme.Muted);
        }

        private void HandleInputKey(Event e)
        {
            bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
            bool composing = ImeSupport.Composing;
            if (enter && !e.shift)
            {
                e.Use();
                RequestSubmit();
            }
            else if (e.character == '\n' && !e.shift)
            {
                // The character that follows the key would put a line break into the emptied field.
                e.Use();
            }
            else if (composing)
            {
                // The arrow keys belong to the input method while it puts a syllable together.
            }
            else if (e.keyCode == KeyCode.UpArrow && _input.IndexOf('\n') < 0 && (_input.Length == 0 || _recallIndex < _recalled.Count))
            {
                e.Use();
                Recall(-1);
            }
            else if (e.keyCode == KeyCode.DownArrow && _input.IndexOf('\n') < 0 && _recallIndex < _recalled.Count)
            {
                e.Use();
                Recall(1);
            }
        }

        // Sends what is in the field, a moment later: see SubmitFrames.
        private void RequestSubmit()
        {
            if (_submitIn == 0)
                _submitIn = SubmitFrames;
        }

        private void SubmitInput()
        {
            string text = _input.Trim();
            _input = string.Empty;
            _focusInput = true;
            if (text.Length == 0)
                return;
            // A key is not something to bring back with the arrow keys.
            if (!text.StartsWith("/key", StringComparison.OrdinalIgnoreCase))
            {
                if (_recalled.Count == 0 || _recalled[_recalled.Count - 1] != text)
                    _recalled.Add(text);
                if (_recalled.Count > 50)
                    _recalled.RemoveAt(0);
            }
            _recallIndex = _recalled.Count;
            _defer(() => _studio.Submit(text));
        }

        private void Recall(int direction)
        {
            if (_recalled.Count == 0)
                return;
            _recallIndex = Mathf.Clamp(_recallIndex + direction, 0, _recalled.Count);
            _input = _recallIndex < _recalled.Count ? _recalled[_recallIndex] : string.Empty;
        }
    }
}
