using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoardSandbox.Core.Mods
{
    public sealed class ModLoadError
    {
        public ModLoadError(string directory, string message)
        {
            Directory = directory;
            Message = message;
        }

        public string Directory { get; }
        public string Message { get; }
    }

    public sealed class ModDiscovery
    {
        public List<ModPackage> Mods { get; } = new List<ModPackage>();
        public List<ModLoadError> Errors { get; } = new List<ModLoadError>();
    }

    public static class ModLoader
    {
        public const string ManifestFileName = "config.json";
        public const string ScriptExtension = ".luau";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static ModPackage LoadDirectory(string directory)
        {
            if (directory == null)
                throw new ArgumentNullException(nameof(directory));
            if (!System.IO.Directory.Exists(directory))
                throw new ModException("the mod directory '" + directory + "' does not exist.");

            string root = Path.GetFullPath(directory);
            string manifestPath = Path.Combine(root, ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new ModException("'" + root + "' has no " + ManifestFileName + ".");

            string manifestJson;
            Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                manifestJson = File.ReadAllText(manifestPath, Utf8);
                foreach (string file in System.IO.Directory.EnumerateFiles(root, "*" + ScriptExtension, SearchOption.AllDirectories))
                {
                    string relative = RelativePath(root, Path.GetFullPath(file));
                    if (!relative.EndsWith(ScriptExtension, StringComparison.Ordinal) || IsHidden(relative))
                        continue;
                    if (sources.Count >= ModPackage.MaxFiles)
                        throw new ModException("the mod has more than " + ModPackage.MaxFiles + " script files.");
                    sources[relative] = File.ReadAllText(file, Utf8);
                }
            }
            catch (IOException e)
            {
                throw new ModException("cannot read the mod in '" + root + "': " + e.Message, e);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new ModException("cannot read the mod in '" + root + "': " + e.Message, e);
            }
            return new ModPackage(manifestJson, sources, root);
        }

        public static ModDiscovery Discover(string modsRoot)
        {
            ModDiscovery discovery = new ModDiscovery();
            if (modsRoot == null || !System.IO.Directory.Exists(modsRoot))
                return discovery;

            List<string> directories = new List<string>(System.IO.Directory.GetDirectories(modsRoot));
            directories.Sort(StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string directory in directories)
            {
                if (!File.Exists(Path.Combine(directory, ManifestFileName)))
                    continue;
                try
                {
                    ModPackage mod = LoadDirectory(directory);
                    if (!seen.Add(mod.Manifest.Id))
                    {
                        discovery.Errors.Add(new ModLoadError(directory, "duplicate mod id '" + mod.Manifest.Id + "'."));
                        continue;
                    }
                    discovery.Mods.Add(mod);
                }
                catch (ModException e)
                {
                    discovery.Errors.Add(new ModLoadError(directory, e.Message));
                }
            }
            return discovery;
        }

        private static string RelativePath(string root, string file)
        {
            string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace('\\', '/');
        }

        private static bool IsHidden(string relative)
        {
            foreach (string part in relative.Split('/'))
            {
                if (part.StartsWith(".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
