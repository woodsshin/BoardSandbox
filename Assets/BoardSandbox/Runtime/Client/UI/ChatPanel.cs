using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Net;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The chat of a room: what the players have said, and a field to say something. Like the other panels it only reads state while
    // it draws, and what it changes goes through Snapshot, which runs between two frames, and through the action it was given for
    // folding the chat away, which has to defer what it does.
    //
    // The chat has a button of its own for that: "Hide" in its title bar puts it away, and what is left is a tab with "Show chat" on it
    // over a corner of the table (see ChatLayout), which brings it back. The keys of the interface have nothing to do with it.
    public sealed class ChatPanel
    {
        private const string InputField = "bse-chat-input";
        private const int MaxNameLength = 24;

        // How many frames a line waits after Enter before it is sent. A syllable that the input method was still putting together is
        // finished by Enter and arrives as a character a moment after the key (see AgentConsolePanel).
        private const int SubmitFrames = 2;

        private readonly GuiTheme _theme;
        private readonly Action _toggle;
        private readonly List<string> _lines = new List<string>();
        private readonly List<bool> _mine = new List<bool>();

        private NetRoom _room;
        private int _shownTotal = -1;
        private string _input = string.Empty;
        private Vector2 _scroll;
        private bool _focusInput;
        private bool _inputFocused;
        private bool _inputDrawn;
        private int _submitIn;

        // toggle: what the buttons of the chat do to fold it away or show it again; it has to be deferred by the one who gives it.
        public ChatPanel(GuiTheme theme, Action toggle)
        {
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        }

        // Samples what changes with time and sends what was asked for; call it once per frame before OnGUI, so that every pass of a
        // frame draws the same controls. room is the room the chat belongs to, null for none.
        public void Snapshot(NetRoom room, double now)
        {
            if (!ReferenceEquals(room, _room))
            {
                // Another room has another talk, and what was half typed is not for it.
                _room = room;
                _shownTotal = -1;
                _input = string.Empty;
                _submitIn = 0;
                _lines.Clear();
                _mine.Clear();
            }

            // While something else covers the field, or the chat is folded away, it cannot have the keyboard.
            if (!_inputDrawn && _inputFocused)
            {
                _inputFocused = false;
                ImeSupport.Prefer(this, false);
            }
            _inputDrawn = false;

            if (room == null)
                return;
            if (_shownTotal != room.ChatTotal)
                Rebuild(room);

            // The line goes out once the last syllable has become part of the text.
            if (_submitIn > 0 && !ImeSupport.Composing && --_submitIn == 0)
                Submit(room, now);
        }

        // Draws the chat where the placement says: its window, or the tab that is left of it when it is folded away. unread: how many
        // lines of the others came in while it was folded away, for the tab.
        public void Draw(ChatPlacement place, int unread)
        {
            Rect area = new Rect(place.X, place.Y, place.Width, place.Height);
            if (place.Dock == ChatDock.Tab)
            {
                DrawTab(area, unread);
                return;
            }

            Event e = Event.current;

            // The field uses up the keys it understands, so these are looked at before it is drawn.
            if (_inputFocused && e.type == EventType.KeyDown)
                HandleInputKey(e);

            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(_theme.Panel, GUILayout.Width(area.width), GUILayout.Height(area.height));
            DrawHeader();
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
                ImeSupport.Prefer(this, _inputFocused);
            }
        }

        // The lines are put together when one has come in, not for every frame.
        private void Rebuild(NetRoom room)
        {
            _lines.Clear();
            _mine.Clear();
            IReadOnlyList<ChatLine> chat = room.Chat;
            for (int i = 0; i < chat.Count; i++)
            {
                bool mine = chat[i].From == room.LocalId;
                _lines.Add((mine ? "You" : DisplayText.Clip(chat[i].Name, MaxNameLength)) + ": " + chat[i].Text);
                _mine.Add(mine);
            }
            _shownTotal = room.ChatTotal;
            _scroll.y = float.MaxValue;
        }

        // All that is left of the chat while it is folded away.
        private void DrawTab(Rect area, int unread)
        {
            string label = unread <= 0 ? "Show chat" : "Show chat (" + (unread > 99 ? "99+" : unread.ToString()) + ")";
            if (GUI.Button(area, label, _theme.Button))
                _toggle();
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Chat", _theme.Heading, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Hide", _theme.Button, GUILayout.Width(_theme.Sf(64f))))
                _toggle();
            GUILayout.EndHorizontal();
        }

        private void DrawLines()
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_lines.Count == 0)
                GUILayout.Label("Nothing has been said yet.", _theme.Muted);
            for (int i = 0; i < _lines.Count; i++)
                GUILayout.Label(_lines[i], _mine[i] ? _theme.Muted : _theme.Wrapped);
            GUILayout.EndScrollView();
        }

        // Where there is a keyboard, the field is the kind that the AI console has, the one Korean is typed into: it wraps a long line
        // instead of scrolling it sideways. On a screen with a keyboard of its own it is the field that TouchText makes of a button.
        private void DrawInput()
        {
            _inputDrawn = true;
            float height = _theme.Sf(30f);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputField);
            if (TouchText.Active)
                _input = TouchText.Field(InputField, _input, ChatText.MaxLength, _theme.Field);
            else
                _input = TouchText.Area(InputField, _input, ChatText.MaxLength, _theme.Area, GUILayout.MinHeight(height), GUILayout.MaxHeight(_theme.Sf(96f)));
            // The done key of a keyboard on the screen is the enter key of this field.
            bool done = TouchText.Submitted(InputField);
            GUI.enabled = _input.Trim().Length > 0;
            if (GUILayout.Button("Send", _theme.Button, GUILayout.Width(_theme.Sf(64f)), GUILayout.Height(height)) || done)
                RequestSubmit();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void HandleInputKey(Event e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                e.Use();
                RequestSubmit();
            }
            else if (e.character == '\n')
            {
                // The character that follows the key would put a line break into the emptied field.
                e.Use();
            }
        }

        // Sends what is in the field, a moment later: see SubmitFrames.
        private void RequestSubmit()
        {
            if (_submitIn == 0)
                _submitIn = SubmitFrames;
        }

        // What is typed is emptied from the field once it is said. A line that the room does not take, because it has been told too
        // much lately, stays in the field to be sent again.
        private void Submit(NetRoom room, double now)
        {
            _focusInput = true;
            if (ChatText.Clean(_input).Length == 0)
            {
                _input = string.Empty;
                return;
            }
            if (room.Say(_input, now))
                _input = string.Empty;
        }
    }
}
