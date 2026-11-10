using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Sim;

namespace BoardSandbox.Core.Commands
{
    // A text front end to a session, for people and for agents that drive the game by typing.
    public sealed class CommandConsole
    {
        private const int MaxListedItems = 24;

        private readonly Dictionary<string, string> _saves = new Dictionary<string, string>(StringComparer.Ordinal);

        public CommandConsole(GameSession session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public GameSession Session { get; set; }

        public static readonly string[] CommandNames =
        {
            "help", "new", "state", "board", "play", "raw", "legal", "random", "ff", "undo", "redo", "goto", "history", "save", "load", "sim", "query", "eval", "logs", "stats",
            "rejections"
        };

        public string Execute(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;
            try
            {
                string trimmed = line.Trim();
                int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
                string name = (space < 0 ? trimmed : trimmed.Substring(0, space)).ToLowerInvariant();
                string rest = space < 0 ? string.Empty : trimmed.Substring(space + 1).Trim();
                return Dispatch(name, rest);
            }
            catch (GameSessionException e)
            {
                return "error " + e.Code + ": " + e.Detail + (string.IsNullOrEmpty(e.Traceback) ? string.Empty : "\n" + e.Traceback);
            }
            catch (LuauException e)
            {
                return "error " + e.Message;
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException || e is InvalidOperationException || e is JsonParseException)
            {
                return "error: " + e.Message;
            }
        }

        private string Dispatch(string name, string rest)
        {
            switch (name)
            {
                case "help":
                case "?":
                    return Help();
                case "new":
                    return NewGame(rest);
                case "state":
                    return State();
                case "board":
                    return AsciiBoard.Render(Session.View);
                case "play":
                    return Play(rest);
                case "raw":
                    return Raw(rest);
                case "legal":
                    return Legal(rest);
                case "random":
                    return RandomMove(rest);
                case "ff":
                    return FastForward(rest);
                case "undo":
                    return Step(rest, true);
                case "redo":
                    return Step(rest, false);
                case "goto":
                    return Goto(rest);
                case "history":
                    return History();
                case "save":
                    return Save(rest);
                case "load":
                    return Load(rest);
                case "sim":
                    return Simulate(rest);
                case "query":
                    return Query(rest);
                case "eval":
                    return Eval(rest);
                case "logs":
                    return Logs();
                case "stats":
                    return Stats();
                case "rejections":
                    return Rejections();
                default:
                    return "unknown command '" + name + "' (try: help)";
            }
        }

        private static string Help()
        {
            return string.Join("\n", new[]
            {
                "help | ?                       this list",
                "new [key=value ...]            start a game with options",
                "state                          turn, result, history position",
                "board                          draw the grid",
                "play [seat] <action> [k=v ...] submit a command (seat defaults to the seat on turn)",
                "raw <seat> <json>              submit a raw JSON command",
                "legal [seat]                   list legal commands",
                "random [seat] [seed=N]         play one random command",
                "ff <n> [seed=N]                play up to n random commands",
                "undo [n] | redo [n] | goto <i> | history",
                "save <name> | load <name>      named snapshots",
                "sim <games> [seed=N] [max_moves=N] [k=v ...]   random games on a private VM (blocks)",
                "query <name> [json]            ask the mod for data",
                "eval <luau>                    dev mode only",
                "logs | stats | rejections"
            });
        }

        private string NewGame(string rest)
        {
            JsonValue options = ParseAssignments(Split(rest), 0, out string problem);
            if (problem != null)
                return problem;
            Session.NewGame(options);
            return "new game started\n" + State();
        }

        private string State()
        {
            GameSession s = Session;
            StringBuilder sb = new StringBuilder();
            sb.Append("mod: ").Append(s.Mod.Manifest.Name).Append(' ').Append(s.Mod.Manifest.Version).Append('\n');
            if (!s.IsStarted)
            {
                sb.Append("no game is running");
                return sb.ToString();
            }
            sb.Append("options: ").Append(s.GameOptions.ToJson()).Append('\n');
            if (s.View.IsOver)
            {
                OverInfo over = s.View.Over;
                sb.Append("result: ").Append(over.Winner > 0 ? SeatName(over.Winner) + " wins" : "no winner");
                if (!string.IsNullOrEmpty(over.Reason))
                    sb.Append(" (").Append(over.Reason).Append(')');
                sb.Append('\n');
            }
            else
            {
                sb.Append("turn: ").Append(s.View.Turn > 0 ? "seat " + s.View.Turn + " (" + SeatName(s.View.Turn) + ")" : "none").Append('\n');
            }
            sb.Append("objects: ").Append(s.View.ObjectCount);
            if (s.HistoryEnabled)
                sb.Append("\nhistory: ").Append(s.HistoryIndex).Append('/').Append(s.HistoryCount - 1);
            return sb.ToString();
        }

        private string SeatName(int seat)
        {
            Mods.SeatInfo info = Session.Mod.Manifest.GetSeat(seat);
            return info == null ? "seat " + seat : info.Name;
        }

        private string Play(string rest)
        {
            List<string> tokens = Split(rest);
            int index = 0;
            int seat = DefaultSeat();
            if (tokens.Count > 0 && int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int explicitSeat))
            {
                seat = explicitSeat;
                index = 1;
            }
            if (index >= tokens.Count)
                return "usage: play [seat] <action> [key=value ...]";
            JsonValue command = JsonValue.NewObject().Set("action", tokens[index]);
            JsonValue extras = ParseAssignments(tokens, index + 1, out string problem);
            if (problem != null)
                return problem;
            foreach (KeyValuePair<string, JsonValue> pair in extras.Members)
                command.Set(pair.Key, pair.Value);
            return Describe(Session.Submit(new InputPacket(seat, command, "console")));
        }

        private string Raw(string rest)
        {
            int space = rest.IndexOf(' ');
            if (space < 0 || !int.TryParse(rest.Substring(0, space), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seat))
                return "usage: raw <seat> <json>";
            JsonValue command = JsonValue.Parse(rest.Substring(space + 1));
            return Describe(Session.Submit(new InputPacket(seat, command, "console")));
        }

        private string Legal(string rest)
        {
            int seat = ParseSeatOrDefault(rest);
            IReadOnlyList<JsonValue> commands = Session.LegalCommands(seat);
            StringBuilder sb = new StringBuilder();
            sb.Append(commands.Count).Append(" legal commands for seat ").Append(seat);
            for (int i = 0; i < commands.Count && i < MaxListedItems; i++)
                sb.Append("\n  ").Append(commands[i].ToJson());
            if (commands.Count > MaxListedItems)
                sb.Append("\n  ...");
            return sb.ToString();
        }

        private string RandomMove(string rest)
        {
            List<string> tokens = Split(rest);
            int seat = DefaultSeat();
            int? seed = null;
            foreach (string token in tokens)
            {
                if (token.StartsWith("seed=", StringComparison.Ordinal) && int.TryParse(token.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                    seed = s;
                else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int explicitSeat))
                    seat = explicitSeat;
            }
            JsonValue command = Session.RandomCommand(seat, seed);
            if (!command.IsObject)
                return "seat " + seat + " has no command to play";
            return "play " + command.ToJson() + "\n" + Describe(Session.Submit(new InputPacket(seat, command, "console")));
        }

        private string FastForward(string rest)
        {
            List<string> tokens = Split(rest);
            if (tokens.Count == 0 || !int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1)
                return "usage: ff <moves> [seed=N]";
            int seed = 1;
            for (int i = 1; i < tokens.Count; i++)
            {
                if (tokens[i].StartsWith("seed=", StringComparison.Ordinal))
                    int.TryParse(tokens[i].Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
            }
            int played = 0;
            string stop = "move limit reached";
            for (int i = 0; i < count; i++)
            {
                if (!Session.IsStarted || Session.View.IsOver)
                {
                    stop = Session.View.IsOver ? "game over" : "no game is running";
                    break;
                }
                int seat = Session.View.Turn;
                if (seat <= 0)
                {
                    stop = "no seat is on turn";
                    break;
                }
                JsonValue command = Session.RandomCommand(seat, seed + i);
                if (!command.IsObject)
                {
                    stop = "seat " + seat + " has no command";
                    break;
                }
                SubmitResult result = Session.Submit(new InputPacket(seat, command, "console"));
                if (!result.IsAccepted)
                {
                    stop = result.ToString();
                    break;
                }
                played++;
            }
            return "played " + played + " commands; stopped: " + stop + "\n" + State();
        }

        private string Step(string rest, bool undo)
        {
            int steps = 1;
            if (rest.Length > 0 && !int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out steps))
                return "usage: " + (undo ? "undo" : "redo") + " [n]";
            bool moved = undo ? Session.Undo(steps) : Session.Redo(steps);
            if (!moved)
                return undo ? "nothing to undo" : "nothing to redo";
            return (undo ? "undone" : "redone") + "\n" + State();
        }

        private string Goto(string rest)
        {
            if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return "usage: goto <history index>";
            return Session.JumpTo(index) ? "at history " + index + "\n" + State() : "no such history entry";
        }

        private string History()
        {
            GameSession s = Session;
            if (!s.HistoryEnabled)
                return "history is not available for this mod";
            StringBuilder sb = new StringBuilder();
            int first = Math.Max(0, s.HistoryCount - MaxListedItems);
            if (first > 0)
                sb.Append("  ...\n");
            for (int i = first; i < s.HistoryCount; i++)
            {
                InputPacket packet = s.GetHistoryPacket(i);
                sb.Append(i == s.HistoryIndex ? "> " : "  ").Append(i).Append(": ");
                sb.Append(packet == null ? "(start)" : "seat " + packet.Seat + " " + packet.Command.ToJson());
                if (i < s.HistoryCount - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        private string Save(string rest)
        {
            if (rest.Length == 0)
                return "usage: save <name>";
            string snapshot = Session.CaptureSnapshot();
            _saves[rest] = snapshot;
            return "saved '" + rest + "' (" + snapshot.Length + " characters)";
        }

        private string Load(string rest)
        {
            if (rest.Length == 0 || !_saves.TryGetValue(rest, out string snapshot))
                return "no save named '" + rest + "'";
            Session.LoadSnapshot(snapshot);
            return "loaded '" + rest + "'\n" + State();
        }

        private string Simulate(string rest)
        {
            List<string> tokens = Split(rest);
            if (tokens.Count == 0 || !int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int games) || games < 1)
                return "usage: sim <games> [seed=N] [max_moves=N] [key=value ...]";
            JsonValue extras = ParseAssignments(tokens, 1, out string problem);
            if (problem != null)
                return problem;
            SimulationOptions options = new SimulationOptions { Games = games, GameOptions = JsonValue.NewObject() };
            foreach (KeyValuePair<string, JsonValue> pair in extras.Members)
            {
                if (pair.Key == "seed")
                    options.Seed = pair.Value.AsInt(1);
                else if (pair.Key == "max_moves")
                    options.MaxMovesPerGame = pair.Value.AsInt(10000);
                else
                    options.GameOptions.Set(pair.Key, pair.Value);
            }
            if (Session.GameOptions.IsObject && options.GameOptions.Count == 0 && Session.IsStarted)
                options.GameOptions = Session.GameOptions.DeepClone();
            return SimulationRunner.Run(Session.Mod, options).ToString();
        }

        private string Query(string rest)
        {
            int space = rest.IndexOf(' ');
            string name = space < 0 ? rest : rest.Substring(0, space);
            if (name.Length == 0)
                return "usage: query <name> [json]";
            JsonValue args = space < 0 ? null : JsonValue.Parse(rest.Substring(space + 1));
            return Session.Query(name, args).ToJson(true);
        }

        private string Eval(string rest)
        {
            if (rest.Length == 0)
                return "usage: eval <luau expression or statements>";
            return string.Join("\n", Session.Evaluate(rest));
        }

        private string Logs()
        {
            if (Session.RecentLogs.Count == 0)
                return "no script output";
            StringBuilder sb = new StringBuilder();
            foreach (LogLine line in Session.RecentLogs)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line.ToString());
            }
            return sb.ToString();
        }

        private string Stats()
        {
            VmStats stats = Session.VmStats;
            return "calls " + stats.CallCount + ", memory " + (stats.MemoryBytes / 1024) + " KiB (peak " + (stats.MemoryPeakBytes / 1024) + " KiB), last call " +
                   stats.LastCallMicros + " us, dropped logs " + stats.DroppedLogs + (stats.Faulted ? ", FAULTED" : string.Empty);
        }

        private string Rejections()
        {
            foreach (ICommandInterceptor interceptor in Session.Interceptors)
            {
                RejectionLog log = interceptor as RejectionLog;
                if (log == null)
                    continue;
                StringBuilder sb = new StringBuilder();
                sb.Append(log.TotalCount).Append(" refused or failed commands");
                foreach (KeyValuePair<string, int> pair in log.CountsByCode)
                    sb.Append("\n  ").Append(pair.Key).Append(": ").Append(pair.Value);
                return sb.ToString();
            }
            return "no rejection log is attached";
        }

        private string Describe(SubmitResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (result.IsAccepted)
            {
                sb.Append("accepted");
                int shown = 0;
                foreach (ViewEvent e in result.Events)
                {
                    if (++shown > MaxListedItems)
                    {
                        sb.Append("\n  ...");
                        break;
                    }
                    sb.Append("\n  ").Append(e.Raw.ToJson());
                }
                return sb.ToString();
            }
            sb.Append(result.Status.ToString().ToLowerInvariant()).Append(' ').Append(result.Code).Append(": ").Append(result.Message);
            if (result.Details.IsObject)
                sb.Append(' ').Append(result.Details.ToJson());
            if (result.Status == SubmitStatus.Faulted)
            {
                sb.Append(result.Recovered ? "\nthe game was restored to the last good state" : "\nthe game could not be restored");
                if (!string.IsNullOrEmpty(result.Traceback))
                    sb.Append('\n').Append(result.Traceback);
            }
            return sb.ToString();
        }

        private int DefaultSeat()
        {
            int turn = Session.View.Turn;
            return turn > 0 ? turn : 1;
        }

        private int ParseSeatOrDefault(string text)
        {
            if (text.Length > 0 && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seat))
                return seat;
            return DefaultSeat();
        }

        private static List<string> Split(string text)
        {
            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;
            bool any = false;
            foreach (char c in text)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                    current.Append(c);
                }
                else if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (any)
                        tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
                else
                {
                    current.Append(c);
                    any = true;
                }
            }
            if (any)
                tokens.Add(current.ToString());
            return tokens;
        }

        private static JsonValue ParseAssignments(List<string> tokens, int start, out string problem)
        {
            problem = null;
            JsonValue values = JsonValue.NewObject();
            for (int i = start; i < tokens.Count; i++)
            {
                int equals = tokens[i].IndexOf('=');
                if (equals <= 0)
                {
                    problem = "expected key=value but found '" + tokens[i] + "'";
                    return values;
                }
                values.Set(tokens[i].Substring(0, equals), ParseValue(tokens[i].Substring(equals + 1)));
            }
            return values;
        }

        private static JsonValue ParseValue(string text)
        {
            return JsonValue.TryParse(text, out JsonValue value, out _) ? value : JsonValue.Of(text);
        }
    }
}
