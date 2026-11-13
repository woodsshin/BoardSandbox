using System;
using System.Reflection;
using UnityEngine;

namespace BoardSandbox.Client.UI
{
    // What the interface needs to know about the input method editor, with which Korean, Japanese and Chinese are
    // typed: a syllable is put together in a composition and only becomes text when it is finished.
    //
    // The project does without the legacy input module, so UnityEngine.Input is not there. IMGUI keeps the same
    // state for its own text fields in GUIUtility, where it is not public; it is read through reflection, and when
    // a version of Unity does not have it the callers fall back to waiting a moment.
    public static class ImeSupport
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // The values of UnityEngine.IMECompositionMode.
        private const int ModeAuto = 0;
        private const int ModeOn = 1;

        private static readonly PropertyInfo Composition = Find("compositionString", typeof(string));
        private static readonly PropertyInfo Mode = Find("imeCompositionMode", null);
        private static bool _preferred;

        // Whether the state of the composition can be read at all.
        public static bool Known => Composition != null;

        // True while a syllable is being put together and is not part of the text yet.
        public static bool Composing
        {
            get
            {
                if (Composition == null)
                    return false;
                try
                {
                    string text = Composition.GetValue(null, null) as string;
                    return !string.IsNullOrEmpty(text);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Keeps the input method switched on while a field that takes prose has the keyboard, and gives the
        // decision back to Unity when it has not. Unity normally does this by itself for a focused text field; this
        // makes sure of it.
        public static void Prefer(bool on)
        {
            if (on == _preferred || Mode == null)
                return;
            _preferred = on;
            try
            {
                Mode.SetValue(null, Enum.ToObject(Mode.PropertyType, on ? ModeOn : ModeAuto), null);
            }
            catch (Exception)
            {
                // The field still works the way Unity handles it on its own.
            }
        }

        private static PropertyInfo Find(string name, Type type)
        {
            try
            {
                PropertyInfo property = typeof(GUIUtility).GetProperty(name, Flags);
                if (property == null || (type != null && property.PropertyType != type))
                    return null;
                if (type == null && (!property.PropertyType.IsEnum || !property.CanWrite))
                    return null;
                return property;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
