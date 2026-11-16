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

        // Images a mod brings along for its objects ("image" in the properties of an object).
        public const int MaxAssets = 256;
        public const int MaxAssetBytes = 8 * 1024 * 1024;
        public const int MaxTotalAssetBytes = 32 * 1024 * 1024;

        private static readonly string[] AssetExtensions = { ".png", ".jpg", ".jpeg" };

        private readonly SortedDictionary<string, string> _sources;
        private readonly SortedDictionary<string, byte[]> _assets = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        public ModPackage(string manifestJson, IDictionary<string, string> sources, string directory = null)
            : this(manifestJson, sources, null, directory)
        {
        }

        // assets: the image files of the mod by their path inside its folder ('/' separators); null for none.
        public ModPackage(string manifestJson, IDictionary<string, string> sources, IDictionary<string, byte[]> assets, string directory)
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
            if (assets != null)
            {
                if (assets.Count > MaxAssets)
                    throw new ModException("the mod has more than " + MaxAssets + " image files.");
                long assetBytes = 0;
                foreach (KeyValuePair<string, byte[]> pair in assets)
                {
                    if (!IsAssetPath(pair.Key))
                        throw new ModException("'" + pair.Key + "' is not a valid image path.");
                    byte[] data = pair.Value ?? new byte[0];
                    if (data.Length > MaxAssetBytes)
                        throw new ModException("'" + pair.Key + "' is larger than " + MaxAssetBytes + " bytes.");
                    assetBytes += data.Length;
                    _assets[pair.Key] = data;
                }
                if (assetBytes > MaxTotalAssetBytes)
                    throw new ModException("the mod images are larger than " + MaxTotalAssetBytes + " bytes in total.");
            }
            ContentHash = ComputeHash();
        }

        public ModManifest Manifest { get; }
        public string ManifestJson { get; }

        // null for packages built from memory
        public string Directory { get; }

        public IReadOnlyDictionary<string, string> Sources => _sources;

        // The image files of the mod by path; empty for most mods. The bytes are those of the files.
        public IReadOnlyDictionary<string, byte[]> Assets => _assets;

        // SHA-256 over the manifest text, every script and every image, for comparing mods between peers. A mod
        // without images has the hash it had before images existed.
        public string ContentHash { get; }

        // Whether a file of a mod is one of its images: a .png or .jpg under a relative path with '/' separators, no
        // part of which starts with a dot.
        public static bool IsAssetPath(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Length > 200 || relative.StartsWith("/", StringComparison.Ordinal)
                || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0)
                return false;
            foreach (string part in relative.Split('/'))
            {
                if (part.Length == 0 || part.StartsWith(".", StringComparison.Ordinal))
                    return false;
            }
            foreach (string extension in AssetExtensions)
            {
                if (relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

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
                foreach (KeyValuePair<string, byte[]> pair in _assets)
                {
                    canonical.Append("asset\n").Append(pair.Key).Append('\n').Append(pair.Value.Length).Append('\n');
                    foreach (byte b in sha.ComputeHash(pair.Value))
                        canonical.Append(b.ToString("x2"));
                    canonical.Append('\n');
                }
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                StringBuilder hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
