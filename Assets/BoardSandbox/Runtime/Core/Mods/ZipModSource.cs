using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoardSandbox.Core.Mods
{
    // The mods in a folder inside a zip archive, laid out the way FolderModSource expects them on disk: a subfolder is a mod
    // when it has a config.json. On Android the files of the app, StreamingAssets among them, are inside the APK, which is a
    // zip archive that System.IO can neither list nor read from, so the games that come with the app are read through this
    // class. The mods are read into memory and nothing is extracted to disk, which is why their Directory is null.
    public sealed class ZipModSource : IModSource
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public ZipModSource(string archivePath, string folder, ModOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("an archive is required", nameof(archivePath));
            ArchivePath = archivePath;
            Folder = (folder ?? string.Empty).Replace('\\', '/').Trim('/');
            Origin = origin;
        }

        public string ArchivePath { get; }

        // The folder inside the archive that holds the mods, with '/' separators and without a leading or trailing '/'.
        // Empty for the top of the archive.
        public string Folder { get; }

        public ModOrigin Origin { get; }

        public string Description => ArchivePath + "!/" + Folder;

        public ModDiscovery Discover()
        {
            ModDiscovery discovery = new ModDiscovery();
            try
            {
                // Sharing is as wide as it gets so that nothing else holding the file open can make this fail.
                using (FileStream stream = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    ReadMods(archive, discovery);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                // A package that cannot be read at all is a problem worth showing, unlike a folder that is not there.
                discovery.Errors.Add(new ModLoadError(Description, "cannot read '" + ArchivePath + "' (" + e.GetType().Name + "): " + e.Message));
            }
            return discovery;
        }

        private void ReadMods(ZipArchive archive, ModDiscovery discovery)
        {
            string prefix = Folder.Length == 0 ? string.Empty : Folder + "/";

            // The files of every folder directly inside Folder, by their path inside that folder. Sorted like
            // ModLoader.Discover sorts the folders of a directory.
            SortedDictionary<string, Dictionary<string, ZipArchiveEntry>> folders =
                new SortedDictionary<string, Dictionary<string, ZipArchiveEntry>>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/", StringComparison.Ordinal) || !name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string inside = name.Substring(prefix.Length);
                int slash = inside.IndexOf('/');
                if (slash <= 0)
                    continue;
                string folder = inside.Substring(0, slash);
                if (!folders.TryGetValue(folder, out Dictionary<string, ZipArchiveEntry> files))
                {
                    files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                    folders.Add(folder, files);
                }
                files[inside.Substring(slash + 1)] = entry;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, ZipArchiveEntry>> folder in folders)
            {
                if (!folder.Value.TryGetValue(ModLoader.ManifestFileName, out ZipArchiveEntry manifest))
                    continue;
                string location = ArchivePath + "!/" + prefix + folder.Key;
                try
                {
                    ModPackage mod = Load(location, manifest, folder.Value);
                    if (!seen.Add(mod.Manifest.Id))
                    {
                        discovery.Errors.Add(new ModLoadError(location, "duplicate mod id '" + mod.Manifest.Id + "'."));
                        continue;
                    }
                    discovery.Mods.Add(mod);
                }
                catch (ModException e)
                {
                    discovery.Errors.Add(new ModLoadError(location, e.Message));
                }
            }
        }

        private static ModPackage Load(string location, ZipArchiveEntry manifest, Dictionary<string, ZipArchiveEntry> files)
        {
            string manifestJson;
            Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                manifestJson = ReadText(manifest);
                List<string> scripts = new List<string>();
                foreach (string relative in files.Keys)
                {
                    if (ModLoader.IsScriptPath(relative))
                        scripts.Add(relative);
                }
                scripts.Sort(StringComparer.Ordinal);
                foreach (string relative in scripts)
                {
                    if (sources.Count >= ModPackage.MaxFiles)
                        throw new ModException("the mod has more than " + ModPackage.MaxFiles + " script files.");
                    sources[relative] = ReadText(files[relative]);
                }
                foreach (KeyValuePair<string, ZipArchiveEntry> file in files)
                {
                    if (!ModPackage.IsAssetPath(file.Key))
                        continue;
                    if (assets.Count >= ModPackage.MaxAssets)
                        throw new ModException("the mod has more than " + ModPackage.MaxAssets + " image files.");
                    assets[file.Key] = ReadBytes(file.Value);
                }
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                throw new ModException("cannot read the mod in '" + location + "' (" + e.GetType().Name + "): " + e.Message, e);
            }
            return new ModPackage(manifestJson, sources, assets, null);
        }

        private static string ReadText(ZipArchiveEntry entry)
        {
            if (entry.Length > ModPackage.MaxSourceBytes)
                throw new ModException("'" + entry.FullName + "' is larger than " + ModPackage.MaxSourceBytes + " bytes.");
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(stream, Utf8, true))
                return reader.ReadToEnd();
        }

        private static byte[] ReadBytes(ZipArchiveEntry entry)
        {
            if (entry.Length > ModPackage.MaxAssetBytes)
                throw new ModException("'" + entry.FullName + "' is larger than " + ModPackage.MaxAssetBytes + " bytes.");
            using (Stream stream = entry.Open())
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        // What reading a file out of an archive can throw: the file cannot be opened or read, or it is not a zip archive.
        private static bool IsReadFailure(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is NotSupportedException;
        }
    }
}
