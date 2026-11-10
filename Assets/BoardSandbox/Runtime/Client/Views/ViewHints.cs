using System;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Client.Views
{
    // What the "client" section of a mod says about how its game is drawn and played.
    public sealed class ViewHints
    {
        private readonly JsonValue _pieces;

        public ViewHints(JsonValue client)
        {
            JsonValue root = client != null && client.IsObject ? client : JsonValue.NewObject();

            JsonValue board = root["board"];
            Style = string.Equals(board["style"].AsString(), "checker", StringComparison.OrdinalIgnoreCase) ? BoardStyle.Checker : BoardStyle.Lines;
            Columns = board["columns"].AsInt(0);
            Rows = board["rows"].AsInt(0);

            JsonValue click = root["click"];
            ClickAction = click["action"].AsString();
            string preview = click["preview"].AsString();
            PreviewKind = preview.Length > 0 ? preview : null;

            _pieces = root["pieces"];
        }

        public BoardStyle Style { get; }

        // 0 when the mod does not say.
        public int Columns { get; }
        public int Rows { get; }

        // The action a click on a board coordinate sends; empty when the mod does not take clicks.
        public string ClickAction { get; }
        public bool CanClick => ClickAction.Length > 0;

        // The kind of piece shown as a preview where a click would place one; null when the mod names none.
        public string PreviewKind { get; }

        public PieceShape ShapeFor(string kind)
        {
            string name = _pieces[kind ?? string.Empty]["shape"].AsString();
            if (string.Equals(name, "stone", StringComparison.OrdinalIgnoreCase))
                return PieceShape.Stone;
            if (string.Equals(name, "cube", StringComparison.OrdinalIgnoreCase))
                return PieceShape.Cube;
            if (string.Equals(name, "disc", StringComparison.OrdinalIgnoreCase))
                return PieceShape.Disc;
            return kind == "stone" ? PieceShape.Stone : PieceShape.Disc;
        }
    }
}
