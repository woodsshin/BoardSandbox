using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoardSandbox.Core.Mods
{
    // The mods in the immediate subfolders of a folder inside a zip archive. On Android the files that ship with
    // the app (StreamingAssets) are not on the file system but inside the APK, which is a zip, so Directory and File
    // do not see them; this reads them from there.
    public sealed class ZipModSource : IModSource
    {
        private const string JarScheme = "jar:file://";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public ZipModSource(string archive, string folder, ModOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(archive))
                throw new ArgumentException("an archive is required", nameof(archive));
            Archive = archive;
            Folder = (folder ?? string.Empty).Replace('\\', '/').Trim('/');
            Origin = origin;
        }

        // The path of the zip file.
        public string Archive { get; }

        // The folder inside the archive that holds the mod folders, with '/' separators; empty for the top.
        public string Folder { get; }

        public ModOrigin Origin { get; }
        public string Description => Archive + "!/" + Folder;

        // A source for a folder below an address like "jar:file:///data/app/x/base.apk!/assets", which is what Unity
        // gives as the StreamingAssets path on Android. Null when the address is not of that form.
        public static ZipModSource FromJarUrl(string url, string folder, ModOrigin origin)
        {
            if (url == null || !url.StartsWith(JarScheme, StringComparison.OrdinalIgnoreCase))
                return null;
            int mark = url.IndexOf("!/", StringComparison.Ordinal);
            if (mark <= JarScheme.Length)
                return null;
            string archive = url.Substring(JarScheme.Length, mark - JarScheme.Length);
            string inner = url.Substring(mark + 2).Trim('/');
            string below = (folder ?? string.Empty).Replace('\\', '/').Trim('/');
            return new ZipModSource(archive, inner.Length > 0 && below.Length > 0 ? inner + "/" + below : inner + below, origin);
        }

        public ModDiscovery Discover()
        {
            ModDiscovery discovery = new ModDiscovery();
            if (!File.Exists(Archive))
                return discovery;

            // Mod folder -> its files, by their path inside that folder.
            SortedDictionary<string, Dictionary<string, string>> folders =
                new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            try
            {
                using (FileStream stream = new FileStream(Archive, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    string prefix = Folder.Length > 0 ? Folder + "/" : string.Empty;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                            continue;
                        string inside = name.Substring(prefix.Length);
                        int slash = inside.IndexOf('/');
                        if (slash <= 0)
                            continue;
                        string folder = inside.Substring(0, slash);
                        string relative = inside.Substring(slash + 1);
                        bool manifest = relative == ModLoader.ManifestFileName;
                        if (!manifest && (!relative.EndsWith(ModLoader.ScriptExtension, StringComparison.Ordinal) || IsHidden(relative)))
                            continue;
                        if (!folders.TryGetValue(folder, out Dictionary<string, string> files))
                        {
                            files = new Dictionary<string, string>(StringComparer.Ordinal);
                            folders[folder] = files;
                        }
                        // More files than a mod may have fail in ModPackage; there is no need to read them all first.
                        if (files.Count > ModPackage.MaxFiles + 1)
                            continue;
                        files[relative] = entry.Length > ModPackage.MaxSourceBytes ? TooLarge(entry.Length) : Read(entry);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                discovery.Errors.Add(new ModLoadError(Description, "cannot read the archive: " + e.Message));
                return discovery;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, string>> pair in folders)
            {
                Dictionary<string, string> files = pair.Value;
                if (!files.TryGetValue(ModLoader.ManifestFileName, out string manifestJson))
                    continue;
                files.Remove(ModLoader.ManifestFileName);
                // The name of the folder, so that messages can show it; the mod has no directory to edit.
                string where = Description.TrimEnd('/') + "/" + pair.Key;
                try
                {
                    ModPackage mod = new ModPackage(manifestJson, files);
                    if (!seen.Add(mod.Manifest.Id))
                    {
                        discovery.Errors.Add(new ModLoadError(where, "duplicate mod id '" + mod.Manifest.Id + "'."));
                        continue;
                    }
                    discovery.Mods.Add(mod);
                }
                catch (ModException e)
                {
                    discovery.Errors.Add(new ModLoadError(where, e.Message));
                }
            }
            return discovery;
        }

        private static string Read(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(stream, Utf8))
                return reader.ReadToEnd();
        }

        // Stands in for a file that is too large to be a script, so that ModPackage refuses the mod with its message.
        private static string TooLarge(long length)
        {
            return new string(' ', ModPackage.MaxSourceBytes + 1);
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
