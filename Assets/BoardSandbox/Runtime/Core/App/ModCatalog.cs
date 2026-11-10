using System;
using System.Collections.Generic;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.App
{
    // The mods found in a list of folders. When two folders hold the same mod id, the earlier folder wins.
    public sealed class ModCatalog
    {
        private readonly List<string> _roots;
        private readonly List<ModPackage> _mods = new List<ModPackage>();
        private readonly List<ModLoadError> _errors = new List<ModLoadError>();

        public ModCatalog(IEnumerable<string> roots)
        {
            _roots = new List<string>();
            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (!string.IsNullOrWhiteSpace(root))
                        _roots.Add(root);
                }
            }
            Refresh();
        }

        public IReadOnlyList<string> Roots => _roots;
        public IReadOnlyList<ModPackage> Mods => _mods;
        public IReadOnlyList<ModLoadError> Errors => _errors;

        public void Refresh()
        {
            _mods.Clear();
            _errors.Clear();
            Dictionary<string, ModPackage> byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
            foreach (string root in _roots)
            {
                ModDiscovery found = ModLoader.Discover(root);
                _errors.AddRange(found.Errors);
                foreach (ModPackage mod in found.Mods)
                {
                    if (byId.TryGetValue(mod.Manifest.Id, out ModPackage existing))
                    {
                        _errors.Add(new ModLoadError(mod.Directory, "the mod id '" + mod.Manifest.Id + "' is already provided by " + existing.Directory + "."));
                        continue;
                    }
                    byId[mod.Manifest.Id] = mod;
                    _mods.Add(mod);
                }
            }
        }

        public ModPackage Find(string id)
        {
            foreach (ModPackage mod in _mods)
            {
                if (string.Equals(mod.Manifest.Id, id, StringComparison.Ordinal))
                    return mod;
            }
            return null;
        }
    }
}
