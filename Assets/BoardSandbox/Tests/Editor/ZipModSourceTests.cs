using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Mods;
using NUnit.Framework;
using static BoardSandbox.Tests.TestMods;

namespace BoardSandbox.Tests
{
    // The games that come with the app are read from the APK on Android, where StreamingAssets is not a folder.
    [TestFixture]
    public class ZipModSourceTests
    {
        // Unity packs StreamingAssets into the "assets" folder of the APK.
        private const string Mods = "assets/Mods";

        private readonly List<string> _temporary = new List<string>();

        [TearDown]
        public void RemoveTemporaryDirectories()
        {
            foreach (string path in _temporary)
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (IOException)
                {
                }
            }
            _temporary.Clear();
        }

        private string NewDirectory()
        {
            string path = TestSupport.TempDirectory();
            _temporary.Add(path);
            return path;
        }

        private string NewArchive(Action<ZipArchive> fill)
        {
            string path = Path.Combine(NewDirectory(), "app.apk");
            WriteArchive(path, fill);
            return path;
        }

        private static void WriteArchive(string path, Action<ZipArchive> fill)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                fill(archive);
        }

        private static void Add(ZipArchive archive, string name, string text, CompressionLevel level = CompressionLevel.Optimal, bool bom = false)
        {
            AddBytes(archive, name, new UTF8Encoding(bom).GetBytes(text), level);
        }

        private static void AddBytes(ZipArchive archive, string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, level);
            using (Stream stream = entry.Open())
                stream.Write(bytes, 0, bytes.Length);
        }

        private static void AddMod(ZipArchive archive, string folder, string id, string name, string script = MinimalScript)
        {
            Add(archive, folder + "/config.json", Manifest(id, name));
            Add(archive, folder + "/main.luau", script);
        }

        [Test]
        public void ListsTheModsInsideAnArchive()
        {
            string apk = NewArchive(zip =>
            {
                AddMod(zip, "assets/Mods/mini", "mini", "Mini");
                AddMod(zip, "assets/Mods/deep", "deep", "Deep");
                Add(zip, "assets/Mods/deep/lib/util.luau", "return {}");
                Add(zip, "assets/Mods/deep/lib/.draft.luau", "this is not read");
                Add(zip, "assets/Mods/deep/.cache/old.luau", "this is not read either");
                Add(zip, "assets/Mods/deep/notes.txt", "not a script");
                Add(zip, "assets/Mods/broken/config.json", "{ nope");
                Add(zip, "assets/Mods/plain/readme.txt", "not a mod");
                Add(zip, "assets/Mods/stray.txt", "not in a mod folder");
                zip.CreateEntry("assets/Mods/emptydir/");
                AddMod(zip, "assets/ModsExtra/near", "near", "Near");
                AddMod(zip, "assets/other/far", "far", "Far");
                AddMod(zip, "Mods/top", "top", "Top");
            });

            ZipModSource source = new ZipModSource(apk, Mods, ModOrigin.BuiltIn);
            Assert.AreEqual(ModOrigin.BuiltIn, source.Origin);
            Assert.AreEqual(apk, source.ArchivePath);
            Assert.AreEqual(Mods, source.Folder);
            Assert.AreEqual(apk + "!/assets/Mods", source.Description);

            ModDiscovery found = source.Discover();
            Assert.AreEqual(2, found.Mods.Count);
            Assert.AreEqual("deep", found.Mods[0].Manifest.Id);
            Assert.AreEqual("mini", found.Mods[1].Manifest.Id);
            Assert.IsNull(found.Mods[0].Directory);

            List<string> scripts = new List<string>(found.Mods[0].Sources.Keys);
            Assert.AreEqual(2, scripts.Count);
            Assert.AreEqual("lib/util.luau", scripts[0]);
            Assert.AreEqual("main.luau", scripts[1]);

            Assert.AreEqual(1, found.Errors.Count);
            Assert.AreEqual("broken", Path.GetFileName(found.Errors[0].Directory));
        }

        [Test]
        public void FolderNamesAreNormalized()
        {
            string apk = NewArchive(zip => AddMod(zip, "assets/Mods/mini", "mini", "Mini"));
            Assert.AreEqual("assets/Mods", new ZipModSource(apk, "\\assets\\Mods\\", ModOrigin.BuiltIn).Folder);
            Assert.AreEqual("assets/Mods", new ZipModSource(apk, "/assets/Mods/", ModOrigin.BuiltIn).Folder);
            Assert.AreEqual("", new ZipModSource(apk, null, ModOrigin.Local).Folder);
            Assert.AreEqual(1, new ZipModSource(apk, "\\assets\\Mods\\", ModOrigin.BuiltIn).Discover().Mods.Count);
        }

        [Test]
        public void TheTopOfAnArchiveCanHoldTheMods()
        {
            string zip = NewArchive(archive =>
            {
                AddMod(archive, "mini", "mini", "Mini");
                AddMod(archive, "other/hidden", "hidden", "Hidden");
            });
            ModDiscovery found = new ZipModSource(zip, "", ModOrigin.Local).Discover();
            Assert.AreEqual(1, found.Mods.Count);
            Assert.AreEqual("mini", found.Mods[0].Manifest.Id);
            Assert.AreEqual(0, found.Errors.Count);
        }

        [Test]
        public void ReportsAnArchiveThatCannotBeRead()
        {
            Assert.Throws<ArgumentException>(() => new ZipModSource("  ", Mods, ModOrigin.BuiltIn));
            Assert.Throws<ArgumentException>(() => new ZipModSource(null, Mods, ModOrigin.BuiltIn));

            string root = NewDirectory();
            ModDiscovery missing = new ZipModSource(Path.Combine(root, "missing.apk"), Mods, ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, missing.Mods.Count);
            Assert.AreEqual(1, missing.Errors.Count);
            StringAssert.Contains("missing.apk", missing.Errors[0].Message);

            string garbage = Path.Combine(root, "garbage.apk");
            File.WriteAllText(garbage, "this is not a zip archive");
            ModDiscovery notZip = new ZipModSource(garbage, Mods, ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, notZip.Mods.Count);
            Assert.AreEqual(1, notZip.Errors.Count);
            StringAssert.Contains("garbage.apk", notZip.Errors[0].Message);

            string empty = Path.Combine(root, "empty.apk");
            File.WriteAllBytes(empty, new byte[0]);
            Assert.AreEqual(1, new ZipModSource(empty, Mods, ModOrigin.BuiltIn).Discover().Errors.Count);
        }

        [Test]
        public void ReportsBrokenAndDuplicateMods()
        {
            string apk = NewArchive(zip =>
            {
                AddMod(zip, "assets/Mods/a", "same", "First");
                AddMod(zip, "assets/Mods/b", "same", "Second");
                Add(zip, "assets/Mods/noscript/config.json", Manifest("noscript", "No script"));
                AddMod(zip, "assets/Mods/fine", "fine", "Fine");
            });

            ModDiscovery found = new ZipModSource(apk, Mods, ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(2, found.Mods.Count);
            Assert.AreEqual("same", found.Mods[0].Manifest.Id);
            Assert.AreEqual("First", found.Mods[0].Manifest.Name);
            Assert.AreEqual("fine", found.Mods[1].Manifest.Id);
            Assert.AreEqual(2, found.Errors.Count);
            Assert.AreEqual("b", Path.GetFileName(found.Errors[0].Directory));
            StringAssert.Contains("duplicate mod id 'same'", found.Errors[0].Message);
            Assert.AreEqual("noscript", Path.GetFileName(found.Errors[1].Directory));
            StringAssert.Contains("main.luau", found.Errors[1].Message);
        }

        [Test]
        public void ReadsStoredAndCompressedFiles()
        {
            // The Android build leaves some streaming assets stored without compression, so both kinds are in an APK.
            string apk = NewArchive(zip =>
            {
                Add(zip, "assets/Mods/stored/config.json", Manifest("stored", "Stored"), CompressionLevel.NoCompression);
                Add(zip, "assets/Mods/stored/main.luau", MinimalScript, CompressionLevel.NoCompression);
                Add(zip, "assets/Mods/packed/config.json", Manifest("packed", "Packed"), CompressionLevel.Optimal);
                Add(zip, "assets/Mods/packed/main.luau", MinimalScript, CompressionLevel.Optimal);
            });
            ModDiscovery found = new ZipModSource(apk, Mods, ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, found.Errors.Count);
            Assert.AreEqual(2, found.Mods.Count);
            Assert.AreEqual("packed", found.Mods[0].Manifest.Id);
            Assert.AreEqual("stored", found.Mods[1].Manifest.Id);
            Assert.AreEqual(found.Mods[0].Sources["main.luau"], found.Mods[1].Sources["main.luau"]);
        }

        [Test]
        public void ReadsAnArchiveWithASigningBlock()
        {
            // A signed APK has its signature in a block between the files and the central directory, which the
            // end-of-central-directory record skips over by naming where the central directory starts.
            string plain = NewArchive(zip =>
            {
                AddMod(zip, "assets/Mods/mini", "mini", "Mini");
                AddMod(zip, "assets/Mods/more", "more", "More");
            });
            byte[] bytes = File.ReadAllBytes(plain);
            int end = bytes.Length - 22;
            Assert.AreEqual(0x06054b50, BitConverter.ToInt32(bytes, end), "the archive should end with an end-of-central-directory record");
            int centralDirectory = BitConverter.ToInt32(bytes, end + 16);

            byte[] block = new byte[4096];
            for (int i = 0; i < block.Length; i++)
                block[i] = (byte)(i * 31 + 7);
            byte[] signed = new byte[bytes.Length + block.Length];
            Array.Copy(bytes, 0, signed, 0, centralDirectory);
            Array.Copy(block, 0, signed, centralDirectory, block.Length);
            Array.Copy(bytes, centralDirectory, signed, centralDirectory + block.Length, bytes.Length - centralDirectory);
            BitConverter.GetBytes(centralDirectory + block.Length).CopyTo(signed, end + block.Length + 16);
            string apk = Path.Combine(NewDirectory(), "signed.apk");
            File.WriteAllBytes(apk, signed);

            ModDiscovery found = BuiltInMods.FromAndroidPackage(apk).Discover();
            Assert.AreEqual(0, found.Errors.Count);
            Assert.AreEqual(2, found.Mods.Count);
            Assert.AreEqual("mini", found.Mods[0].Manifest.Id);
            Assert.AreEqual("more", found.Mods[1].Manifest.Id);
            Assert.AreEqual(MinimalScript, found.Mods[1].Sources["main.luau"]);
        }

        [Test]
        public void ReadsTheSameTextAsAFolderDoes()
        {
            // A byte order mark and non-ASCII text must come out the same from an archive and from a folder, or the same
            // game would have a different ContentHash on Android and on a desktop and the two could not play together.
            string script = "-- 오목\nlocal rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules";
            string manifest = Manifest("hangul", "오목", "한글 설명");

            string folder = NewDirectory();
            Directory.CreateDirectory(Path.Combine(folder, "hangul"));
            File.WriteAllText(Path.Combine(folder, "hangul", "config.json"), manifest, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder, "hangul", "main.luau"), script, new UTF8Encoding(false));

            string apk = NewArchive(zip =>
            {
                Add(zip, "assets/Mods/hangul/config.json", manifest, bom: true);
                Add(zip, "assets/Mods/hangul/main.luau", script, bom: false);
            });

            ModPackage fromFolder = ModLoader.Discover(folder).Mods[0];
            ModPackage fromArchive = new ZipModSource(apk, Mods, ModOrigin.BuiltIn).Discover().Mods[0];
            Assert.AreEqual("오목", fromArchive.Manifest.Name);
            Assert.AreEqual(fromFolder.ManifestJson, fromArchive.ManifestJson);
            Assert.AreEqual(fromFolder.Sources["main.luau"], fromArchive.Sources["main.luau"]);
            Assert.AreEqual(fromFolder.ContentHash, fromArchive.ContentHash);
        }

        [Test]
        public void AScriptOverTheSizeLimitIsAnError()
        {
            string apk = NewArchive(zip =>
            {
                Add(zip, "assets/Mods/big/config.json", Manifest("big", "Big"));
                Add(zip, "assets/Mods/big/main.luau", "-- " + new string('a', ModPackage.MaxSourceBytes + 1));
                AddMod(zip, "assets/Mods/small", "small", "Small");
            });
            ModDiscovery found = new ZipModSource(apk, Mods, ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(1, found.Mods.Count);
            Assert.AreEqual("small", found.Mods[0].Manifest.Id);
            Assert.AreEqual(1, found.Errors.Count);
            StringAssert.Contains("larger than", found.Errors[0].Message);
        }

        [Test]
        public void ReadsTheBuiltInGamesLikeTheFolderDoes()
        {
            string root = TestSupport.ModsRoot;
            string apk = NewArchive(zip =>
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    // Unity leaves its own .meta files out of a build.
                    if (file.EndsWith(".meta", StringComparison.Ordinal))
                        continue;
                    string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    AddBytes(zip, "assets/Mods/" + relative, File.ReadAllBytes(file));
                }
            });

            ModDiscovery fromFolder = ModLoader.Discover(root);
            ModDiscovery fromArchive = BuiltInMods.FromAndroidPackage(apk).Discover();
            Assert.AreEqual(0, fromArchive.Errors.Count);
            Assert.Greater(fromArchive.Mods.Count, 0);
            Assert.AreEqual(fromFolder.Mods.Count, fromArchive.Mods.Count);
            for (int i = 0; i < fromFolder.Mods.Count; i++)
            {
                string id = fromFolder.Mods[i].Manifest.Id;
                Assert.AreEqual(id, fromArchive.Mods[i].Manifest.Id);
                Assert.AreEqual(fromFolder.Mods[i].Sources.Count, fromArchive.Mods[i].Sources.Count, id);
                Assert.AreEqual(fromFolder.Mods[i].ContentHash, fromArchive.Mods[i].ContentHash, id);
                Assert.IsNull(fromArchive.Mods[i].Directory, id);
            }
        }

        [Test]
        public void GamesReadFromAnArchiveCanBePlayed()
        {
            // The games of the APK have no folder; they still have to start, scripts that require each other included.
            string root = TestSupport.ModsRoot;
            string apk = NewArchive(zip =>
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".meta", StringComparison.Ordinal))
                    {
                        string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        AddBytes(zip, "assets/Mods/" + relative, File.ReadAllBytes(file));
                    }
                }
            });

            ModDiscovery found = BuiltInMods.FromAndroidPackage(apk).Discover();
            Assert.AreEqual(0, found.Errors.Count);
            Assert.Greater(found.Mods.Count, 0);
            foreach (ModPackage mod in found.Mods)
            {
                using (GameSession session = new GameSession(mod))
                {
                    session.NewGame();
                    Assert.IsTrue(session.IsStarted, mod.Manifest.Id);
                }
            }
        }

        [Test]
        public void BuiltInGamesAreInTheFolderOrInTheAndroidPackage()
        {
            string streaming = Path.Combine("data", "streaming");
            FolderModSource folder = (FolderModSource)BuiltInMods.FromFolder(streaming);
            Assert.AreEqual(Path.Combine(streaming, "Mods"), folder.Folder);
            Assert.AreEqual(ModOrigin.BuiltIn, folder.Origin);

            ZipModSource package = (ZipModSource)BuiltInMods.FromAndroidPackage("/data/app/pkg/base.apk");
            Assert.AreEqual("/data/app/pkg/base.apk", package.ArchivePath);
            Assert.AreEqual("assets/Mods", package.Folder);
            Assert.AreEqual(ModOrigin.BuiltIn, package.Origin);
            Assert.AreEqual("/data/app/pkg/base.apk!/assets/Mods", package.Description);

            Assert.Throws<ArgumentException>(() => BuiltInMods.FromAndroidPackage(null));
        }

        [Test]
        public void CatalogKeepsTheIdsOfTheGamesInTheArchive()
        {
            string apk = NewArchive(zip => AddMod(zip, "assets/Mods/mini", "mini", "Mini"));
            string local = NewDirectory();
            WriteMod(local, "mini", "Impostor");
            WriteMod(local, "mine", "Mine");

            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                BuiltInMods.FromAndroidPackage(apk),
                new FolderModSource(local, ModOrigin.Local)
            });
            Assert.AreEqual(2, catalog.Entries.Count);
            Assert.AreEqual(ModOrigin.BuiltIn, catalog.FindEntry("mini").Origin);
            Assert.AreEqual("Mini", catalog.FindEntry("mini").Name);
            Assert.AreEqual(ModOrigin.Local, catalog.FindEntry("mine").Origin);
            Assert.AreEqual(1, catalog.Errors.Count);
            StringAssert.Contains("already provided", catalog.Errors[0].Message);
        }

        [Test]
        public void RescanReadsTheArchiveAgain()
        {
            string apk = NewArchive(zip => AddMod(zip, "assets/Mods/mini", "mini", "Mini"));
            ModCatalog catalog = new ModCatalog(new IModSource[] { BuiltInMods.FromAndroidPackage(apk) });
            Assert.AreEqual(1, catalog.Entries.Count);

            WriteArchive(apk, zip =>
            {
                AddMod(zip, "assets/Mods/mini", "mini", "Mini");
                AddMod(zip, "assets/Mods/more", "more", "More");
            });
            catalog.Refresh();
            Assert.AreEqual(2, catalog.Entries.Count);
            Assert.IsNotNull(catalog.FindEntry("more"));
        }

        [Test]
        public void CatalogShowsAnUnreadablePackageAsAProblem()
        {
            string root = NewDirectory();
            ModCatalog catalog = new ModCatalog(new IModSource[] { BuiltInMods.FromAndroidPackage(Path.Combine(root, "gone.apk")) });
            Assert.AreEqual(0, catalog.Entries.Count);
            Assert.AreEqual(1, catalog.Errors.Count);
            StringAssert.Contains("gone.apk", catalog.Errors[0].Message);
        }
    }
}
