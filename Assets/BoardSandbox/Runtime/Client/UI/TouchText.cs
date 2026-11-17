using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // Text fields for a device whose keyboard is on the screen.
    //
    // A text field of IMGUI is made for a keyboard with keys: on a phone or a tablet a tap on it may put the cursor
    // in the field without anything to type with. So on those devices a field is drawn as a button that shows its
    // text; a tap opens the keyboard of the system on that text, the field follows what is typed, and closing the
    // keyboard ends the edit (cancelling it puts the old text back). Everywhere else a field is the IMGUI one.
    //
    // One keyboard is open at a time. A field is known by its name, which has to be different for every field
    // that can be on the screen together.
    public static class TouchText
    {
        private static TouchScreenKeyboard _keyboard;
        private static string _owner;
        private static string _original;
        private static string _submitted;

        // Whether fields are drawn the touch way on this device.
        public static bool Active => Application.isMobilePlatform && TouchScreenKeyboard.isSupported;

        // True while the keyboard is open on a field.
        public static bool Editing => _keyboard != null;

        public static string Field(string name, string text, int maxLength, GUIStyle style, params GUILayoutOption[] options)
        {
            return Draw(name, text, maxLength, style, TouchScreenKeyboardType.Default, false, false, options);
        }

        // A field for a number: the keyboard opens on its digits.
        public static string NumberField(string name, string text, int maxLength, GUIStyle style, params GUILayoutOption[] options)
        {
            return Draw(name, text, maxLength, style, TouchScreenKeyboardType.NumbersAndPunctuation, false, false, options);
        }

        public static string SecretField(string name, string text, int maxLength, GUIStyle style, params GUILayoutOption[] options)
        {
            return Draw(name, text, maxLength, style, TouchScreenKeyboardType.Default, true, false, options);
        }

        public static string Area(string name, string text, int maxLength, GUIStyle style, params GUILayoutOption[] options)
        {
            return Draw(name, text, maxLength, style, TouchScreenKeyboardType.Default, false, true, options);
        }

        // True once after the keyboard of the field was closed with its done key, which stands for the enter key.
        public static bool Submitted(string name)
        {
            if (_submitted != name)
                return false;
            _submitted = null;
            return true;
        }

        private static string Draw(string name, string text, int maxLength, GUIStyle style, TouchScreenKeyboardType type, bool secret, bool multiline,
            GUILayoutOption[] options)
        {
            if (text == null)
                text = string.Empty;
            if (!Active)
            {
                if (secret)
                    return GUILayout.PasswordField(text, '*', maxLength, style, options);
                return multiline ? GUILayout.TextArea(text, maxLength, style, options) : GUILayout.TextField(text, maxLength, style, options);
            }

            if (_keyboard != null && _owner == name)
                text = Follow(name, maxLength);

            string shown = secret ? new string('*', text.Length) : text;
            if (GUILayout.Button(shown, style, options) && !(_keyboard != null && _owner == name))
            {
                _keyboard = TouchScreenKeyboard.Open(text, type, false, multiline, secret, false, string.Empty, maxLength);
                _owner = name;
                _original = text;
                _submitted = null;
            }
            return text;
        }

        // What the keyboard holds now. When it has been closed, the edit is over: the text is what was typed, or what
        // the field held before when the edit was cancelled.
        private static string Follow(string name, int maxLength)
        {
            string typed = _keyboard.text ?? string.Empty;
            if (maxLength >= 0 && typed.Length > maxLength)
                typed = typed.Substring(0, maxLength);
            TouchScreenKeyboard.Status status = _keyboard.status;
            if (status == TouchScreenKeyboard.Status.Visible)
                return typed;

            _keyboard = null;
            _owner = null;
            if (status == TouchScreenKeyboard.Status.Canceled)
                return _original;
            if (status == TouchScreenKeyboard.Status.Done)
                _submitted = name;
            return typed;
        }
    }
}
