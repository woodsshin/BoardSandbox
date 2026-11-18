using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Net;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // The chat of a room: what the players have said, and a field to say something. Like the other panels it only reads state while
    // it draws, and what it changes goes through Snapshot, which runs between two frames.
    public sealed class ChatPanel
    {
        private const string InputField = "bse-chat-input";
        private const int MaxNameLength = 24;

        // How many frames a line waits after Enter before it is sent. A syllable that the input method was still putting together is
        // finished by Enter and arrives as a character a moment after the key (see AgentConsolePanel).
        private const int SubmitFrames = 2;

        private readonly GuiTheme _theme;
        private readonly List<string> _lines = new List<string>();
        private readonly List<bool> _mine = new List<bool>();

        private NetRoom _room;
        private int _shownTotal = -1;
        private string _input = string.Empty;
        private Vector2 _scroll;
        private bool _focusInput;
        private bool _inputFocused;
        private bool _drawn;
        private int _submitIn;

        public ChatPanel(GuiTheme theme)
        {
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
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
            if (room == null)
                return;

            // While something else covers the chat its field cannot have the keyboard.
            if (!_drawn && _inputFocused)
            {
                _inputFocused = false;
                ImeSupport.Prefer(false);
            }
            _drawn = false;

            if (_shownTotal != room.ChatTotal)
                Rebuild(room);

            // The line goes out once the last syllable has become part of the text.
            if (_submitIn > 0 && !ImeSupport.Composing && --_submitIn == 0)
                Submit(room, now);
        }

        // Draws the chat in the given part of the screen.
        public void Draw(NetRoom room, Rect area)
        {
            Event e = Event.current;
            _drawn = true;

            // The field uses up the keys it understands, so these are looked at before it is drawn.
            if (_inputFocused && e.type == EventType.KeyDown)
                HandleInputKey(e);

            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(_theme.Panel, GUILayout.Width(area.width), GUILayout.Height(area.height));
            GUILayout.Label("Chat", _theme.Heading);
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

        private void DrawLines()
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_lines.Count == 0)
                GUILayout.Label("Nothing has been said yet.", _theme.Muted);
            for (int i = 0; i < _lines.Count; i++)
                GUILayout.Label(_lines[i], _mine[i] ? _theme.Muted : _theme.Wrapped);
            GUILayout.EndScrollView();
        }

        private void DrawInput()
        {
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputField);
            _input = TouchText.Field(InputField, _input, ChatText.MaxLength, _theme.Field);
            // The done key of a keyboard on the screen is the enter key of this field.
            bool done = TouchText.Submitted(InputField);
            GUI.enabled = _input.Trim().Length > 0;
            if (GUILayout.Button("Send", _theme.Button, GUILayout.Width(_theme.Sf(64f))) || done)
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
