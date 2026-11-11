using System;
using System.Globalization;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    // A mod in a catalog, and where it was found.
    public sealed class ModEntry
    {
        public ModEntry(ModPackage mod, ModOrigin origin)
        {
            Mod = mod ?? throw new ArgumentNullException(nameof(mod));
            Origin = origin;
            PlayersText = FormatPlayers(mod.Manifest.MinPlayers, mod.Manifest.MaxPlayers);
            Summary = PlayersText + " · " + origin.Label();
            Initial = FirstTextElement(mod.Manifest.Name);
            TileHue = HueOf(mod.Manifest.Id);
        }

        public ModPackage Mod { get; }
        public ModOrigin Origin { get; }

        public string Id => Mod.Manifest.Id;
        public string Name => Mod.Manifest.Name;

        // "1 player", "2 players" or "2-4 players".
        public string PlayersText { get; }

        // One line for a list: the players and the origin, such as "2 players · Built-in".
        public string Summary { get; }

        // The first letter of the name in capitals, for a tile that stands for the game.
        public string Initial { get; }

        // A color for the tile in [0, 1), taken from the id so that it is the same on every run.
        public float TileHue { get; }

        // True when every word of the query occurs in the name, the id or the description, ignoring case.
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;
            ModManifest manifest = Mod.Manifest;
            foreach (string word in query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Contains(manifest.Name, word) || Contains(manifest.Id, word) || Contains(manifest.Description, word))
                    continue;
                return false;
            }
            return true;
        }

        private static bool Contains(string text, string word)
        {
            return text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatPlayers(int min, int max)
        {
            if (min == max)
                return min + (min == 1 ? " player" : " players");
            return min + "-" + max + " players";
        }

        private static string FirstTextElement(string name)
        {
            string trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            if (trimmed.Length == 0)
                return "?";
            return StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
        }

        // FNV-1a over the characters of the id.
        private static float HueOf(string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in id)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (hash % 360u) / 360f;
            }
        }
    }
}
