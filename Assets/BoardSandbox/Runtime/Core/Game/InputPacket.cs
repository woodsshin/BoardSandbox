using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Game
{
    // The single shape in which every source of input (mouse, console, AI agent, network peer) reaches the rules.
    public sealed class InputPacket
    {
        public InputPacket(int seat, JsonValue command, string source = "local")
        {
            Seat = seat;
            Command = command;
            Source = source ?? "local";
        }

        public int Seat { get; }
        public JsonValue Command { get; }
        public string Source { get; }

        // Assigned by the session in submission order.
        public long Sequence { get; internal set; }

        public static InputPacket Action(int seat, string action, string source = "local")
        {
            return new InputPacket(seat, JsonValue.NewObject().Set("action", action), source);
        }

        public InputPacket With(string key, double value)
        {
            Command.Set(key, value);
            return this;
        }

        public InputPacket With(string key, string value)
        {
            Command.Set(key, value);
            return this;
        }

        public InputPacket With(string key, bool value)
        {
            Command.Set(key, value);
            return this;
        }

        public override string ToString()
        {
            return "seat " + Seat + " " + (Command == null ? "null" : Command.ToJson()) + " [" + Source + "]";
        }
    }
}
