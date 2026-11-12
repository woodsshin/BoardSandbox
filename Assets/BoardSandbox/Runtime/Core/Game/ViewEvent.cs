using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Game
{
    public readonly struct Point2
    {
        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public enum ViewEventKind
    {
        Reset,
        Spawn,
        Move,
        Set,
        Remove,
        Turn,
        Over,
        Status,
        Custom,

        // A step of the presentation that changes no state: "fx".
        Effect
    }

    public sealed class ViewEvent
    {
        public const string EffectType = "fx";
        public const string StageType = "stage";
        public const double MaxEffectSeconds = 10.0;

        private static readonly Point2[] NoCells = new Point2[0];

        private ViewEvent()
        {
        }

        public ViewEventKind Kind { get; private set; }

        // The raw "t" field.
        public string Type { get; private set; }

        // The complete event object as emitted by the script.
        public JsonValue Raw { get; private set; }

        public int Id { get; private set; }
        public string ObjectKind { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }

        // 0 when the object has no owner.
        public int Owner { get; private set; }

        public JsonValue Props { get; private set; }
        public int Seat { get; private set; }

        // 0 for a draw or an ended game without a winner.
        public int Winner { get; private set; }

        public string Reason { get; private set; }
        public IReadOnlyList<Point2> Cells { get; private set; }
        public string Text { get; private set; }
        public string Level { get; private set; }

        // How long an effect takes; 0 for every other event.
        public double Seconds { get; private set; }

        public static ViewEvent Parse(JsonValue raw)
        {
            if (raw == null || !raw.IsObject)
                throw new FormatException("a view event must be a JSON object");
            string type = raw["t"].AsString();
            if (type.Length == 0)
                throw new FormatException("a view event needs a string field 't'");

            ViewEvent e = new ViewEvent
            {
                Raw = raw,
                Type = type,
                Props = JsonValue.Null,
                ObjectKind = string.Empty,
                Reason = string.Empty,
                Text = string.Empty,
                Level = string.Empty,
                Cells = NoCells
            };
            switch (type)
            {
                case "reset":
                    e.Kind = ViewEventKind.Reset;
                    break;
                case "spawn":
                    e.Kind = ViewEventKind.Spawn;
                    e.Id = RequireId(raw);
                    e.ObjectKind = raw["kind"].AsString();
                    e.X = raw["x"].AsDouble();
                    e.Y = raw["y"].AsDouble();
                    e.Owner = raw["owner"].AsInt();
                    e.Props = raw["props"];
                    break;
                case "move":
                    e.Kind = ViewEventKind.Move;
                    e.Id = RequireId(raw);
                    e.X = raw["x"].AsDouble();
                    e.Y = raw["y"].AsDouble();
                    break;
                case "set":
                    e.Kind = ViewEventKind.Set;
                    e.Id = RequireId(raw);
                    e.Props = raw["props"];
                    break;
                case "remove":
                    e.Kind = ViewEventKind.Remove;
                    e.Id = RequireId(raw);
                    break;
                case "turn":
                    e.Kind = ViewEventKind.Turn;
                    e.Seat = raw["seat"].AsInt();
                    break;
                case "over":
                    e.Kind = ViewEventKind.Over;
                    e.Winner = raw["winner"].AsInt();
                    e.Reason = raw["reason"].AsString();
                    e.Cells = ParseCells(raw["cells"]);
                    break;
                case "status":
                    e.Kind = ViewEventKind.Status;
                    e.Text = raw["text"].AsString();
                    e.Level = raw["level"].AsString("info");
                    break;
                case EffectType:
                    e.Kind = ViewEventKind.Effect;
                    e.Seconds = ClampSeconds(raw["seconds"].AsDouble());
                    break;
                default:
                    e.Kind = ViewEventKind.Custom;
                    break;
            }
            return e;
        }

        public static List<ViewEvent> ParseList(JsonValue array)
        {
            List<ViewEvent> events = new List<ViewEvent>(array.Count);
            foreach (JsonValue raw in array.Items)
                events.Add(Parse(raw));
            return events;
        }

        private static double ClampSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0)
                return 0;
            return Math.Min(seconds, MaxEffectSeconds);
        }

        private static int RequireId(JsonValue raw)
        {
            JsonValue id = raw["id"];
            if (!id.IsNumber)
                throw new FormatException("the '" + raw["t"].AsString() + "' event needs a numeric 'id'");
            return id.AsInt();
        }

        private static IReadOnlyList<Point2> ParseCells(JsonValue cells)
        {
            if (!cells.IsArray || cells.Count == 0)
                return NoCells;
            Point2[] points = new Point2[cells.Count];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Point2(cells[i][0].AsDouble(), cells[i][1].AsDouble());
            return points;
        }

        public override string ToString()
        {
            return Raw.ToJson();
        }
    }
}
