using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Mods
{
    public sealed class ModException : Exception
    {
        public ModException(string message)
            : base(message)
        {
        }

        public ModException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public sealed class SeatInfo
    {
        public SeatInfo(int seat, string name, string color)
        {
            Seat = seat;
            Name = name;
            Color = color;
        }

        public int Seat { get; }
        public string Name { get; }

        // "#rrggbb"
        public string Color { get; }
    }

    public sealed class ModManifest
    {
        public const int SupportedApi = 1;
        public const int MaxSeats = 16;
        public const int MaxRoomPlayers = 8;
        public const int MaxAiLevels = 16;
        public const int MaxAiEffort = 1000000;

        private static readonly string[] DefaultSeatColors =
        {
            "#d94a3d", "#3d7fd9", "#4cae4f", "#e0b83a", "#9a5bd0", "#3fb8b0", "#e07a2f", "#8a8a8a"
        };

        private ModManifest()
        {
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Description { get; private set; }
        public int Api { get; private set; }
        public string Entry { get; private set; }
        public int MinPlayers { get; private set; }
        public int MaxPlayers { get; private set; }
        public IReadOnlyList<SeatInfo> Seats { get; private set; }
        public JsonValue DefaultOptions { get; private set; }

        // Every player of a networked game has a table of their own instead of a seat at a shared one.
        public bool SplitScreen { get; private set; }

        // How many players a networked game takes.
        public int RoomPlayers { get; private set; }

        // The option that makes the tables of a split game start alike; empty when the mod names none.
        public string SeedOption { get; private set; }

        // Presentation hints for a client; the core never interprets them.
        public JsonValue Client { get; private set; }

        // The thinking computer player the mod asks for ("ai.engine"); empty when it has only random moves.
        public string AiEngine { get; private set; }

        // The effort of that player per level ("ai.levels"), rising with the games played against it. What a
        // number means is up to the engine; for gomoku_mcts it is simulations per move.
        public IReadOnlyList<int> AiLevels { get; private set; }

        public SeatInfo GetSeat(int seat)
        {
            return seat >= 1 && seat <= Seats.Count ? Seats[seat - 1] : null;
        }

        public static ModManifest Parse(string json)
        {
            JsonValue root;
            try
            {
                root = JsonValue.Parse(json);
            }
            catch (JsonParseException e)
            {
                throw new ModException("config.json is not valid JSON: " + e.Message, e);
            }
            if (!root.IsObject)
                throw new ModException("config.json must contain a JSON object.");

            ModManifest manifest = new ModManifest();
            manifest.Id = RequireString(root, "id");
            if (!IsValidId(manifest.Id))
                throw new ModException("'id' must be 1-64 characters of a-z, 0-9, '_' or '-' and start with a letter or digit.");
            manifest.Name = RequireString(root, "name");
            manifest.Version = root["version"].AsString("0.0.0");
            manifest.Description = root["description"].AsString("");

            JsonValue api = root["api"];
            if (!api.IsNumber || api.AsInt(-1) != SupportedApi)
                throw new ModException("'api' must be " + SupportedApi + ".");
            manifest.Api = SupportedApi;

            manifest.Entry = RequireString(root, "entry");
            if (!IsValidModulePath(manifest.Entry))
                throw new ModException("'entry' must be a relative .luau path using '/' separators.");

            JsonValue players = root["players"];
            int min = players["min"].AsInt(2);
            int max = players["max"].AsInt(min);
            if (min < 1 || max < min || max > MaxSeats)
                throw new ModException("'players' must satisfy 1 <= min <= max <= " + MaxSeats + ".");
            manifest.MinPlayers = min;
            manifest.MaxPlayers = max;

            JsonValue seatList = root["seats"];
            List<SeatInfo> seats = new List<SeatInfo>(max);
            for (int i = 0; i < max; i++)
            {
                JsonValue entry = seatList[i];
                string name = entry["name"].AsString("Player " + (i + 1));
                string color = entry["color"].AsString(DefaultSeatColors[i % DefaultSeatColors.Length]);
                if (!IsValidColor(color))
                    throw new ModException("seat " + (i + 1) + " color must look like #rrggbb.");
                seats.Add(new SeatInfo(i + 1, name, color));
            }
            manifest.Seats = seats;

            JsonValue options = root["options"];
            if (!options.IsNull && !options.IsObjectLike)
                throw new ModException("'options' must be an object.");
            manifest.DefaultOptions = options.IsObject ? options : JsonValue.NewObject();

            JsonValue multiplayer = root["multiplayer"];
            manifest.SplitScreen = multiplayer["mode"].AsString() == "split";
            manifest.RoomPlayers = manifest.SplitScreen ? Math.Max(1, Math.Min(MaxRoomPlayers, multiplayer["players"].AsInt(4))) : max;
            manifest.SeedOption = multiplayer["seed_option"].AsString();

            JsonValue client = root["client"];
            if (!client.IsNull && !client.IsObject)
                throw new ModException("'client' must be an object.");
            manifest.Client = client.IsObject ? client : JsonValue.NewObject();

            JsonValue ai = root["ai"];
            if (!ai.IsNull && !ai.IsObject)
                throw new ModException("'ai' must be an object.");
            manifest.AiEngine = ai["engine"].AsString();
            JsonValue levelList = ai["levels"];
            if (!levelList.IsNull && !levelList.IsArray)
                throw new ModException("'ai.levels' must be an array of positive numbers.");
            List<int> levels = new List<int>();
            foreach (JsonValue level in levelList.Items)
            {
                int effort = level.AsInt(0);
                if (effort < 1 || effort > MaxAiEffort)
                    throw new ModException("'ai.levels' must hold numbers from 1 to " + MaxAiEffort + ".");
                levels.Add(effort);
            }
            if (levels.Count > MaxAiLevels)
                throw new ModException("'ai.levels' must not hold more than " + MaxAiLevels + " numbers.");
            if (manifest.AiEngine.Length > 0 && levels.Count == 0)
                throw new ModException("'ai.levels' must hold at least one number when 'ai.engine' is set.");
            manifest.AiLevels = levels;
            return manifest;
        }

        private static string RequireString(JsonValue root, string key)
        {
            JsonValue value = root[key];
            if (!value.IsString || value.AsString().Length == 0)
                throw new ModException("'" + key + "' must be a non-empty string.");
            return value.AsString();
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64)
                return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (!alnum && (i == 0 || (c != '_' && c != '-')))
                    return false;
            }
            return true;
        }

        public static bool IsValidModulePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 200 || !path.EndsWith(".luau", StringComparison.Ordinal))
                return false;
            if (path[0] == '/' || path.IndexOf('\\') >= 0 || path.IndexOf('\0') >= 0)
                return false;
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..")
                    return false;
            }
            return true;
        }

        private static bool IsValidColor(string color)
        {
            if (color == null || color.Length != 7 || color[0] != '#')
                return false;
            for (int i = 1; i < 7; i++)
            {
                char c = color[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }
            return true;
        }
    }
}
