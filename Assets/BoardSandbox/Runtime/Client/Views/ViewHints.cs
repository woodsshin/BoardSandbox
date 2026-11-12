using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Client.Views
{
    // What the "client" section of a mod says about how its game is played and how its pieces look.
    public sealed class ViewHints
    {
        private readonly JsonValue _pieces;
        private readonly Dictionary<string, JsonValue> _keys = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        public ViewHints(JsonValue client)
        {
            Client = client != null && client.IsObject ? client : JsonValue.NewObject();

            JsonValue click = Client["click"];
            ClickAction = click["action"].AsString();
            ClickObjects = string.Equals(click["target"].AsString(), "object", StringComparison.OrdinalIgnoreCase);
            string preview = click["preview"].AsString();
            PreviewKind = preview.Length > 0 ? preview : null;
            DragAction = Client["drag"]["action"].AsString();

            _pieces = Client["pieces"];
            foreach (KeyValuePair<string, JsonValue> pair in Client["keys"].Members)
            {
                string name = NormalizeKey(pair.Key);
                if (name.Length > 0 && pair.Value.IsObject && pair.Value["action"].AsString().Length > 0)
                    _keys[name] = pair.Value;
            }
        }

        public JsonValue Client { get; }

        // The action a click sends; empty when the mod does not take clicks.
        public string ClickAction { get; }
        public bool CanClick => ClickAction.Length > 0;

        // Clicks name the object under the cursor instead of a coordinate of the board.
        public bool ClickObjects { get; }

        // The action a drag that starts on an object sends; empty when the mod does not take drags.
        public string DragAction { get; }
        public bool CanDrag => DragAction.Length > 0;

        // The kind of piece shown as a preview where a click would place one; null when the mod names none.
        public string PreviewKind { get; }

        public int KeyCount => _keys.Count;

        // What the mod says about every piece of a kind; the properties of a piece come on top of it.
        public JsonValue PieceDefaults(string kind)
        {
            return _pieces[kind ?? string.Empty];
        }

        public PieceShape ShapeFor(string kind)
        {
            if (BoardShapes.TryParseShape(PieceDefaults(kind)["shape"].AsString(), out PieceShape shape))
                return shape;
            return kind == "stone" ? PieceShape.Stone : PieceShape.Disc;
        }

        // The command bound to a key such as "u" or "shift+u"; null when there is none.
        public JsonValue KeyCommand(string key, bool shift)
        {
            string name = NormalizeKey(key);
            if (name.Length == 0)
                return null;
            _keys.TryGetValue(shift ? "shift+" + name : name, out JsonValue command);
            return command;
        }

        private static string NormalizeKey(string key)
        {
            return (key ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }
}
