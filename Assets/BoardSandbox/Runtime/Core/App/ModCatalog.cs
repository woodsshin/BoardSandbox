using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    // The mods found by a list of sources. When two sources provide the same mod id, the earlier source wins.
    public sealed class ModCatalog
    {
        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private readonly List<IModSource> _sources = new List<IModSource>();
        private readonly List<ModEntry> _entries = new List<ModEntry>();
        private readonly List<ModPackage> _mods = new List<ModPackage>();
        private readonly List<ModLoadError> _errors = new List<ModLoadError>();
        private readonly List<ModOrigin> _origins = new List<ModOrigin>();

        public ModCatalog(IEnumerable<IModSource> sources)
        {
            if (sources != null)
            {
                foreach (IModSource source in sources)
                {
                    if (source != null)
                        _sources.Add(source);
                }
            }
            Refresh();
        }

        // Every folder becomes a local source; blank entries are skipped.
        public ModCatalog(IEnumerable<string> folders) : this(ToSources(folders))
        {
        }

        public IReadOnlyList<IModSource> Sources => _sources;

        // In the order the sources gave them.
        public IReadOnlyList<ModEntry> Entries => _entries;

        public IReadOnlyList<ModPackage> Mods => _mods;
        public IReadOnlyList<ModLoadError> Errors => _errors;

        // The origins that have at least one mod, in display order.
        public IReadOnlyList<ModOrigin> Origins => _origins;

        public void Refresh()
        {
            _entries.Clear();
            _mods.Clear();
            _errors.Clear();
            _origins.Clear();
            Dictionary<string, ModEntry> byId = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            foreach (IModSource source in _sources)
            {
                ModDiscovery found;
                try
                {
                    found = source.Discover();
                }
                catch (Exception e)
                {
                    _errors.Add(new ModLoadError(source.Description ?? string.Empty, "the mods could not be listed: " + e.Message));
                    continue;
                }

                _errors.AddRange(found.Errors);
                foreach (ModPackage mod in found.Mods)
                {
                    if (byId.TryGetValue(mod.Manifest.Id, out ModEntry existing))
                    {
                        if (!IsSameFolder(existing.Mod, mod))
                        {
                            _errors.Add(new ModLoadError(mod.Directory ?? source.Description ?? string.Empty,
                                "the mod id '" + mod.Manifest.Id + "' is already provided by " + (existing.Mod.Directory ?? "another source") + "."));
                        }
                        continue;
                    }
                    ModEntry entry = new ModEntry(mod, source.Origin);
                    byId[entry.Id] = entry;
                    _entries.Add(entry);
                    _mods.Add(mod);
                    if (!_origins.Contains(entry.Origin))
                        _origins.Add(entry.Origin);
                }
            }
            _origins.Sort();
        }

        public ModPackage Find(string id)
        {
            return FindEntry(id)?.Mod;
        }

        public ModEntry FindEntry(string id)
        {
            foreach (ModEntry entry in _entries)
            {
                if (string.Equals(entry.Id, id, StringComparison.Ordinal))
                    return entry;
            }
            return null;
        }

        // The game to open with. A requested id selects that game, or none when there is no such game; without a request
        // the only game of a catalog is opened, and with several or none the choice is left to the player.
        public ModEntry FindStartup(string requestedId)
        {
            if (!string.IsNullOrWhiteSpace(requestedId))
                return FindEntry(requestedId.Trim());
            return _entries.Count == 1 ? _entries[0] : null;
        }

        // The mods that match the query, built-in ones first and each origin in alphabetical order. A null origin
        // means all origins and an empty query matches everything.
        public List<ModEntry> Search(string query, ModOrigin? origin = null)
        {
            List<ModEntry> result = new List<ModEntry>();
            foreach (ModEntry entry in _entries)
            {
                if ((origin == null || entry.Origin == origin.Value) && entry.Matches(query))
                    result.Add(entry);
            }
            result.Sort(CompareForDisplay);
            return result;
        }

        private static int CompareForDisplay(ModEntry a, ModEntry b)
        {
            int byOrigin = a.Origin.CompareTo(b.Origin);
            if (byOrigin != 0)
                return byOrigin;
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
        }

        private static bool IsSameFolder(ModPackage a, ModPackage b)
        {
            return a.Directory != null && b.Directory != null && string.Equals(a.Directory, b.Directory, PathComparison);
        }

        private static IEnumerable<IModSource> ToSources(IEnumerable<string> folders)
        {
            List<IModSource> sources = new List<IModSource>();
            if (folders != null)
            {
                foreach (string folder in folders)
                {
                    if (!string.IsNullOrWhiteSpace(folder))
                        sources.Add(new FolderModSource(folder, ModOrigin.Local));
                }
            }
            return sources;
        }
    }
}
