using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BoardSandbox.Core.Luau;

namespace BoardSandbox.Core.Mods
{
    public sealed class ModPackage
    {
        public const int MaxFiles = 4096;
        public const int MaxSourceBytes = 4 * 1024 * 1024;
        public const int MaxTotalBytes = 32 * 1024 * 1024;

        private readonly SortedDictionary<string, string> _sources;

        public ModPackage(string manifestJson, IDictionary<string, string> sources, string directory = null)
        {
            if (manifestJson == null)
                throw new ArgumentNullException(nameof(manifestJson));
            if (sources == null)
                throw new ArgumentNullException(nameof(sources));

            Manifest = ModManifest.Parse(manifestJson);
            ManifestJson = manifestJson;
            Directory = directory;
            _sources = new SortedDictionary<string, string>(StringComparer.Ordinal);

            if (sources.Count > MaxFiles)
                throw new ModException("the mod has more than " + MaxFiles + " script files.");
            long total = 0;
            foreach (KeyValuePair<string, string> pair in sources)
            {
                if (!ModManifest.IsValidModulePath(pair.Key))
                    throw new ModException("'" + pair.Key + "' is not a valid script path.");
                int bytes = Encoding.UTF8.GetByteCount(pair.Value ?? string.Empty);
                if (bytes > MaxSourceBytes)
                    throw new ModException("'" + pair.Key + "' is larger than " + MaxSourceBytes + " bytes.");
                total += bytes;
                _sources[pair.Key] = pair.Value ?? string.Empty;
            }
            if (total > MaxTotalBytes)
                throw new ModException("the mod scripts are larger than " + MaxTotalBytes + " bytes in total.");
            if (!_sources.ContainsKey(Manifest.Entry))
                throw new ModException("the entry script '" + Manifest.Entry + "' was not found.");
            ContentHash = ComputeHash();
        }

        public ModManifest Manifest { get; }
        public string ManifestJson { get; }

        // null for packages built from memory
        public string Directory { get; }

        public IReadOnlyDictionary<string, string> Sources => _sources;

        // SHA-256 over the manifest text and every script, for comparing mods between peers.
        public string ContentHash { get; }

        public LuauVm CreateVm(VmOptions options = null)
        {
            LuauVm vm = LuauVm.Create(options);
            try
            {
                foreach (KeyValuePair<string, string> pair in _sources)
                    vm.AddModule(pair.Key, pair.Value);
                vm.Boot(Manifest.Entry);
                return vm;
            }
            catch
            {
                vm.Dispose();
                throw;
            }
        }

        private string ComputeHash()
        {
            using (SHA256 sha = SHA256.Create())
            {
                StringBuilder canonical = new StringBuilder();
                canonical.Append(ManifestJson.Length).Append('\n').Append(ManifestJson).Append('\n');
                foreach (KeyValuePair<string, string> pair in _sources)
                    canonical.Append(pair.Key).Append('\n').Append(pair.Value.Length).Append('\n').Append(pair.Value).Append('\n');
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                StringBuilder hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
