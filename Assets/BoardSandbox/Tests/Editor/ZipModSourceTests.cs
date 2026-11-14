using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class ZipModSourceTests
    {
        private const string Manifest = "{\"id\":\"zipped\",\"name\":\"Zipped\",\"api\":1,\"entry\":\"main.luau\"}";
        private const string Script = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules";

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

        private string NewArchivePath()
        {
            string directory = TestSupport.TempDirectory();
            _temporary.Add(directory);
            return Path.Combine(directory, "base.apk");
        }

        private static void Add(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            using (Stream stream = zip.CreateEntry(name, level).Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        // An archive laid out like an APK: the games that come with the app under assets/Mods, among other things.
        private string PackBuiltInMods()
        {
            string path = NewArchivePath();
            using (ZipArchive zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                Add(zip, "AndroidManifest.xml", "<manifest/>");
                Add(zip, "assets/bin/Data/boot.config", "x");
                foreach (string file in Directory.EnumerateFiles(TestSupport.ModsRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(TestSupport.ModsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    // An APK stores assets without compression.
                    Add(zip, "assets/Mods/" + relative.Replace('\\', '/'), File.ReadAllText(file), CompressionLevel.NoCompression);
                }
                Add(zip, "lib/arm64-v8a/libbse_luau.so", "not a mod");
            }
            return path;
        }

        [Test]
        public void TheGamesInsideAnApkAreTheSameAsThoseInTheFolder()
        {
            string apk = PackBuiltInMods();
            ModDiscovery zipped = new ZipModSource(apk, "assets/Mods", ModOrigin.BuiltIn).Discover();
            ModDiscovery folder = new FolderModSource(TestSupport.ModsRoot, ModOrigin.BuiltIn).Discover();

            Assert.AreEqual(0, zipped.Errors.Count);
            Assert.Greater(folder.Mods.Count, 0);
            Assert.AreEqual(folder.Mods.Count, zipped.Mods.Count);
            for (int i = 0; i < folder.Mods.Count; i++)
            {
                Assert.AreEqual(folder.Mods[i].Manifest.Id, zipped.Mods[i].Manifest.Id);
                Assert.AreEqual(folder.Mods[i].ContentHash, zipped.Mods[i].ContentHash, "a player on a phone must be able to join one on a PC");
                Assert.AreEqual(folder.Mods[i].Sources.Count, zipped.Mods[i].Sources.Count);
                Assert.IsNull(zipped.Mods[i].Directory);
            }
        }

        [Test]
        public void AGameFromAnApkCanBePlayed()
        {
            string apk = PackBuiltInMods();
            IModSource source = ZipModSource.FromJarUrl("jar:file://" + apk + "!/assets", "Mods", ModOrigin.BuiltIn);
            using (GameHost host = new GameHost(new ModCatalog(new[] { source })))
            {
                Assert.AreEqual(0, host.Catalog.Errors.Count);
                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual(1, host.Session.View.Turn);
            }
        }

        [Test]
        public void JarAddressesNameTheArchiveAndTheFolder()
        {
            ZipModSource source = ZipModSource.FromJarUrl("jar:file:///data/app/~~ab==/com.x.y-cd==/base.apk!/assets", "Mods", ModOrigin.BuiltIn);
            Assert.AreEqual("/data/app/~~ab==/com.x.y-cd==/base.apk", source.Archive);
            Assert.AreEqual("assets/Mods", source.Folder);
            Assert.AreEqual(ModOrigin.BuiltIn, source.Origin);
            Assert.AreEqual("/data/app/~~ab==/com.x.y-cd==/base.apk!/assets/Mods", source.Description);

            Assert.AreEqual("assets/Mods", ZipModSource.FromJarUrl("jar:file:///a.apk!/assets/", "/Mods/", ModOrigin.BuiltIn).Folder);
            Assert.AreEqual("Mods", ZipModSource.FromJarUrl("jar:file:///a.obb!/", "Mods", ModOrigin.BuiltIn).Folder);
            Assert.AreEqual("assets", ZipModSource.FromJarUrl("jar:file:///a.apk!/assets", null, ModOrigin.BuiltIn).Folder);

            // Every other platform gives a plain folder.
            Assert.IsNull(ZipModSource.FromJarUrl("/Applications/Game.app/Data/Raw", "Mods", ModOrigin.BuiltIn));
            Assert.IsNull(ZipModSource.FromJarUrl("C:\\Game\\Game_Data\\StreamingAssets", "Mods", ModOrigin.BuiltIn));
            Assert.IsNull(ZipModSource.FromJarUrl("jar:file:///a.apk", "Mods", ModOrigin.BuiltIn));
            Assert.IsNull(ZipModSource.FromJarUrl(null, "Mods", ModOrigin.BuiltIn));
            Assert.Throws<ArgumentException>(() => new ZipModSource(" ", "Mods", ModOrigin.BuiltIn));
        }

        [Test]
        public void OnlyModFoldersBelowTheFolderAreRead()
        {
            string path = NewArchivePath();
            using (ZipArchive zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                Add(zip, "assets/Mods/good/config.json", Manifest);
                Add(zip, "assets/Mods/good/main.luau", Script);
                Add(zip, "assets/Mods/good/lib/util.luau", "return {}");
                Add(zip, "assets/Mods/good/.hidden/secret.luau", "return {}");
                Add(zip, "assets/Mods/good/notes.txt", "not a script");
                Add(zip, "assets/Mods/good/main.luau.meta", "fileFormatVersion: 2");
                Add(zip, "assets/Mods/readme.luau", "a file next to the mod folders");
                Add(zip, "assets/Mods/empty/main.luau", Script);
                Add(zip, "assets/Other/elsewhere/config.json", Manifest.Replace("zipped", "elsewhere"));
                Add(zip, "assets/Other/elsewhere/main.luau", Script);
                Add(zip, "assets/ModsToo/near/config.json", Manifest.Replace("zipped", "near"));
                Add(zip, "assets/ModsToo/near/main.luau", Script);
            }
            ModDiscovery found = new ZipModSource(path, "assets/Mods", ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, found.Errors.Count);
            Assert.AreEqual(1, found.Mods.Count);
            Assert.AreEqual("zipped", found.Mods[0].Manifest.Id);
            Assert.AreEqual(2, found.Mods[0].Sources.Count);
            Assert.IsTrue(found.Mods[0].Sources.ContainsKey("lib/util.luau"));

            Assert.AreEqual(2, new ZipModSource(path, "assets/Other", ModOrigin.BuiltIn).Discover().Mods.Count
                               + new ZipModSource(path, "assets/ModsToo/", ModOrigin.BuiltIn).Discover().Mods.Count);
        }

        [Test]
        public void BrokenModsAndArchivesAreReportedNotThrown()
        {
            string path = NewArchivePath();
            using (ZipArchive zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                Add(zip, "Mods/a/config.json", Manifest);
                Add(zip, "Mods/a/main.luau", Script);
                Add(zip, "Mods/b/config.json", Manifest);
                Add(zip, "Mods/b/main.luau", Script);
                Add(zip, "Mods/c/config.json", "{ not json");
                Add(zip, "Mods/d/config.json", Manifest.Replace("zipped", "no-entry"));
            }
            ModDiscovery found = new ZipModSource(path, "Mods", ModOrigin.Local).Discover();
            Assert.AreEqual(1, found.Mods.Count);
            Assert.AreEqual(3, found.Errors.Count);
            Assert.IsTrue(found.Errors[0].Message.Contains("duplicate mod id"), found.Errors[0].Message);
            Assert.AreEqual("b", Path.GetFileName(found.Errors[0].Directory));
            Assert.AreEqual("c", Path.GetFileName(found.Errors[1].Directory));
            Assert.AreEqual("d", Path.GetFileName(found.Errors[2].Directory));

            string missing = Path.Combine(Path.GetDirectoryName(path), "missing.apk");
            ModDiscovery none = new ZipModSource(missing, "Mods", ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, none.Mods.Count);
            Assert.AreEqual(0, none.Errors.Count);

            string garbage = Path.Combine(Path.GetDirectoryName(path), "garbage.apk");
            File.WriteAllText(garbage, "this is not a zip file");
            ModDiscovery broken = new ZipModSource(garbage, "Mods", ModOrigin.BuiltIn).Discover();
            Assert.AreEqual(0, broken.Mods.Count);
            Assert.AreEqual(1, broken.Errors.Count);
            Assert.IsTrue(broken.Errors[0].Message.Contains("cannot read the archive"), broken.Errors[0].Message);
        }
    }
}
