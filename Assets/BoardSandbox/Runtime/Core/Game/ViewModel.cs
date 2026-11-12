using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Game
{
    public sealed class ViewObject
    {
        public ViewObject(int id, string kind, double x, double y, int owner, JsonValue props)
        {
            Id = id;
            Kind = kind;
            X = x;
            Y = y;
            Owner = owner;
            Props = props.IsObjectLike ? props.DeepClone() : JsonValue.NewObject();
            if (!Props.IsObject)
                Props = JsonValue.NewObject();
        }

        public int Id { get; }
        public string Kind { get; }
        public double X { get; internal set; }
        public double Y { get; internal set; }
        public int Owner { get; }
        public JsonValue Props { get; internal set; }
    }

    public sealed class OverInfo
    {
        public OverInfo(int winner, string reason, IReadOnlyList<Point2> cells)
        {
            Winner = winner;
            Reason = reason;
            Cells = cells;
        }

        public int Winner { get; }
        public string Reason { get; }
        public IReadOnlyList<Point2> Cells { get; }
    }

    // A mirror of what the script has put on the table; it changes only by applying events.
    public sealed class ViewModel
    {
        private readonly Dictionary<int, ViewObject> _objects = new Dictionary<int, ViewObject>();
        private readonly Dictionary<string, ViewEvent> _customByType = new Dictionary<string, ViewEvent>(StringComparer.Ordinal);
        private readonly List<string> _customOrder = new List<string>();

        public int Turn { get; private set; }
        public OverInfo Over { get; private set; }
        public bool IsOver => Over != null;
        public long Version { get; private set; }
        public int ObjectCount => _objects.Count;
        public IEnumerable<ViewObject> Objects => _objects.Values;

        public ViewObject Get(int id)
        {
            _objects.TryGetValue(id, out ViewObject value);
            return value;
        }

        public ViewEvent GetCustom(string type)
        {
            _customByType.TryGetValue(type, out ViewEvent value);
            return value;
        }

        public void Clear()
        {
            _objects.Clear();
            _customByType.Clear();
            _customOrder.Clear();
            Turn = 0;
            Over = null;
            Version++;
        }

        // Returns false when the event refers to an object that does not exist.
        public bool Apply(ViewEvent e)
        {
            if (e.Kind == ViewEventKind.Effect)
                return true;
            Version++;
            switch (e.Kind)
            {
                case ViewEventKind.Reset:
                    Clear();
                    return true;
                case ViewEventKind.Spawn:
                    _objects[e.Id] = new ViewObject(e.Id, e.ObjectKind, e.X, e.Y, e.Owner, e.Props);
                    return true;
                case ViewEventKind.Move:
                {
                    if (!_objects.TryGetValue(e.Id, out ViewObject moved))
                        return false;
                    moved.X = e.X;
                    moved.Y = e.Y;
                    return true;
                }
                case ViewEventKind.Set:
                {
                    if (!_objects.TryGetValue(e.Id, out ViewObject changed))
                        return false;
                    foreach (KeyValuePair<string, JsonValue> pair in e.Props.Members)
                        changed.Props.Set(pair.Key, pair.Value);
                    return true;
                }
                case ViewEventKind.Remove:
                    return _objects.Remove(e.Id);
                case ViewEventKind.Turn:
                    Turn = e.Seat;
                    return true;
                case ViewEventKind.Over:
                    Over = new OverInfo(e.Winner, e.Reason, e.Cells);
                    return true;
                case ViewEventKind.Custom:
                    if (!_customByType.TryGetValue(e.Type, out ViewEvent earlier))
                        _customOrder.Add(e.Type);
                    else if (e.Type == ViewEvent.StageType)
                        e = ViewEvent.Parse(JsonMerge.Deep(earlier.Raw, e.Raw));
                    _customByType[e.Type] = e;
                    return true;
                default:
                    return true;
            }
        }

        // Events that rebuild this state from nothing, for a view that attaches late.
        public List<ViewEvent> Synthesize()
        {
            List<ViewEvent> events = new List<ViewEvent>(_objects.Count + _customOrder.Count + 3);
            events.Add(ViewEvent.Parse(JsonValue.NewObject().Set("t", "reset")));
            foreach (string type in _customOrder)
                events.Add(_customByType[type]);

            List<int> ids = new List<int>(_objects.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                ViewObject o = _objects[id];
                JsonValue raw = JsonValue.NewObject()
                    .Set("t", "spawn")
                    .Set("id", o.Id)
                    .Set("kind", o.Kind)
                    .Set("x", o.X)
                    .Set("y", o.Y)
                    .Set("props", o.Props.DeepClone());
                if (o.Owner != 0)
                    raw.Set("owner", o.Owner);
                events.Add(ViewEvent.Parse(raw));
            }
            if (Turn > 0)
                events.Add(ViewEvent.Parse(JsonValue.NewObject().Set("t", "turn").Set("seat", Turn)));
            if (Over != null)
            {
                JsonValue raw = JsonValue.NewObject().Set("t", "over").Set("winner", Over.Winner);
                if (!string.IsNullOrEmpty(Over.Reason))
                    raw.Set("reason", Over.Reason);
                if (Over.Cells.Count > 0)
                {
                    JsonValue cells = JsonValue.NewArray();
                    foreach (Point2 p in Over.Cells)
                        cells.Add(JsonValue.NewArray().Add(p.X).Add(p.Y));
                    raw.Set("cells", cells);
                }
                events.Add(ViewEvent.Parse(raw));
            }
            return events;
        }
    }
}
