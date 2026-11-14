using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Ai
{
    // How many games the player has finished against the thinking computer player of each mod. The computer plays
    // the first game at the first level of the mod and moves up a level with every game until the last.
    public sealed class AiProgress
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly Dictionary<string, int> _games = new Dictionary<string, int>(StringComparer.Ordinal);

        // A null path keeps the count in memory, for as long as the object lives.
        public AiProgress(string path = null)
        {
            Path = path;
        }

        public string Path { get; }

        // Why the file could not be read or written the last time; empty when it went well.
        public string Problem { get; private set; } = string.Empty;

        public int GamesPlayed(string modId)
        {
            return modId != null && _games.TryGetValue(modId, out int games) ? games : 0;
        }

        // The index into the levels of a mod for the next move: one level per finished game.
        public int LevelIndex(string modId, int levelCount)
        {
            return Math.Max(0, Math.Min(levelCount - 1, GamesPlayed(modId)));
        }

        public void RecordGame(string modId)
        {
            if (string.IsNullOrEmpty(modId))
                return;
            _games[modId] = Math.Min(int.MaxValue - 1, GamesPlayed(modId)) + 1;
            Save();
        }

        // Back to the first level.
        public void Reset(string modId)
        {
            if (modId != null && _games.Remove(modId))
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
                    int games = pair.Value.AsInt(0);
                    if (games > 0)
                        _games[pair.Key] = games;
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonParseException)
            {
                Problem = "The record of games against the computer could not be read: " + e.Message;
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
                JsonValue games = JsonValue.NewObject();
                foreach (KeyValuePair<string, int> pair in _games)
                    games.Set(pair.Key, pair.Value);
                File.WriteAllText(Path, JsonValue.NewObject().Set("games", games).ToJson(true), Utf8);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Problem = "The record of games against the computer could not be saved: " + e.Message;
                return false;
            }
        }
    }
}
