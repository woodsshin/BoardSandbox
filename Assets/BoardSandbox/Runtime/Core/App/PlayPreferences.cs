using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    // What the player chose the last time they played each game, kept between runs so that the next game, or the next
    // room, of that mod starts the same way: who a room was opened against and how many play, and who plays the seats
    // at this computer. The person at this computer always plays seat 1, so that seat is not kept.
    public sealed class PlayPreferences
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly Dictionary<string, Choices> _games = new Dictionary<string, Choices>(StringComparer.Ordinal);

        // A null path keeps the choices in memory, for as long as the object lives.
        public PlayPreferences(string path = null)
        {
            Path = path;
        }

        public string Path { get; }

        // Why the file could not be read or written the last time; empty when it went well.
        public string Problem { get; private set; } = string.Empty;

        // How the last room of the game was opened: against the computer or not, and how many played (0 when the game
        // does not ask). False when no room of the game was opened yet.
        public bool TryGetRoom(string modId, out bool againstComputer, out int players)
        {
            againstComputer = false;
            players = 0;
            if (modId == null || !_games.TryGetValue(modId, out Choices choices) || !choices.HasRoom)
                return false;
            againstComputer = choices.RoomComputer;
            players = choices.RoomPlayers;
            return true;
        }

        public void RememberRoom(string modId, bool againstComputer, int players)
        {
            Problem = string.Empty;
            if (string.IsNullOrEmpty(modId))
                return;
            players = Math.Max(0, players);
            Choices choices = Of(modId);
            if (choices.HasRoom && choices.RoomComputer == againstComputer && choices.RoomPlayers == players)
                return;
            choices.HasRoom = true;
            choices.RoomComputer = againstComputer;
            choices.RoomPlayers = players;
            Save();
        }

        // Whether the computer plays a seat at this computer: never seat 1; any other seat as the player left it the last
        // time, or, where they never chose, as the fallback says.
        public bool ComputerPlays(string modId, int seat, bool fallback)
        {
            if (seat <= 1)
                return false;
            if (modId != null && _games.TryGetValue(modId, out Choices choices) && choices.Seats != null && seat - 2 < choices.Seats.Length)
                return choices.Seats[seat - 2];
            return fallback;
        }

        // The seats as the player left them: computer[i] says whether the computer plays seat i + 1.
        public void RememberSeats(string modId, IReadOnlyList<bool> computer)
        {
            Problem = string.Empty;
            if (string.IsNullOrEmpty(modId) || computer == null)
                return;
            int count = Math.Max(0, Math.Min(computer.Count, ModManifest.MaxSeats) - 1);
            bool[] seats = new bool[count];
            for (int i = 0; i < count; i++)
                seats[i] = computer[i + 1];
            Choices choices = Of(modId);
            if (choices.Seats == null ? count == 0 : SameSeats(choices.Seats, seats))
                return;
            choices.Seats = seats;
            Save();
        }

        public bool Load()
        {
            Problem = string.Empty;
            _games.Clear();
            if (Path == null || !File.Exists(Path))
                return true;
            try
            {
                JsonValue root = JsonValue.Parse(File.ReadAllText(Path, Utf8));
                foreach (KeyValuePair<string, JsonValue> pair in root["games"].Members)
                {
                    Choices choices = Read(pair.Value);
                    if (choices != null)
                        _games[pair.Key] = choices;
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonParseException)
            {
                Problem = "The settings of your last games could not be read: " + e.Message;
                return false;
            }
        }

        public bool Save()
        {
            Problem = string.Empty;
            if (Path == null)
                return true;
            try
            {
                string folder = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                File.WriteAllText(Path, JsonValue.NewObject().Set("games", Write()).ToJson(true), Utf8);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Problem = "The settings of your last games could not be saved: " + e.Message;
                return false;
            }
        }

        private Choices Of(string modId)
        {
            if (!_games.TryGetValue(modId, out Choices choices))
            {
                choices = new Choices();
                _games[modId] = choices;
            }
            return choices;
        }

        private static bool SameSeats(bool[] a, bool[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        // What a file makes of one game; anything in it that is not what it should be is left out.
        private static Choices Read(JsonValue value)
        {
            if (!value.IsObject)
                return null;
            Choices choices = new Choices();
            JsonValue room = value["room"];
            if (room.IsObject && room["computer"].IsBool)
            {
                choices.HasRoom = true;
                choices.RoomComputer = room["computer"].AsBool();
                choices.RoomPlayers = Math.Max(0, room["players"].AsInt(0));
            }
            JsonValue seats = value["seats"];
            if (seats.IsArray)
            {
                // A list with anything but true and false in it is not one that this class wrote.
                bool[] computer = new bool[Math.Min(seats.Count, ModManifest.MaxSeats - 1)];
                bool valid = true;
                for (int i = 0; i < computer.Length && valid; i++)
                {
                    valid = seats[i].IsBool;
                    computer[i] = seats[i].AsBool();
                }
                if (valid)
                    choices.Seats = computer;
            }
            return choices.HasRoom || choices.Seats != null ? choices : null;
        }

        private JsonValue Write()
        {
            string[] ids = new string[_games.Count];
            _games.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            JsonValue games = JsonValue.NewObject();
            foreach (string id in ids)
            {
                Choices choices = _games[id];
                if (!choices.HasRoom && choices.Seats == null)
                    continue;
                JsonValue game = JsonValue.NewObject();
                if (choices.HasRoom)
                    game.Set("room", JsonValue.NewObject().Set("computer", choices.RoomComputer).Set("players", choices.RoomPlayers));
                if (choices.Seats != null)
                {
                    JsonValue seats = JsonValue.NewArray();
                    foreach (bool computer in choices.Seats)
                        seats.Add(computer);
                    game.Set("seats", seats);
                }
                games.Set(id, game);
            }
            return games;
        }

        private sealed class Choices
        {
            public bool HasRoom;
            public bool RoomComputer;
            public int RoomPlayers;

            // For seats 2 and on, true where the computer plays; null while nobody chose.
            public bool[] Seats;
        }
    }
}
