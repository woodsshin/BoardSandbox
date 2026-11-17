using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class ModTests
    {
        private const string MinimalManifest = "{\"id\":\"mini\",\"name\":\"Mini\",\"api\":1,\"entry\":\"main.luau\"}";
        private const string MinimalScript = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules";

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

        private static string Manifest(string id, string extra = "")
        {
            return "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"api\":1,\"entry\":\"main.luau\"" + extra + "}";
        }

        [Test]
        public void MinimalManifestGetsDefaults()
        {
            ModManifest manifest = ModManifest.Parse(MinimalManifest);
            Assert.AreEqual("mini", manifest.Id);
            Assert.AreEqual("Mini", manifest.Name);
            Assert.AreEqual("0.0.0", manifest.Version);
            Assert.AreEqual("", manifest.Description);
            Assert.AreEqual(1, manifest.Api);
            Assert.AreEqual("main.luau", manifest.Entry);
            Assert.AreEqual(2, manifest.MinPlayers);
            Assert.AreEqual(2, manifest.MaxPlayers);
            Assert.AreEqual(2, manifest.Seats.Count);
            Assert.AreEqual("Player 1", manifest.Seats[0].Name);
            Assert.AreEqual("Player 2", manifest.Seats[1].Name);
            Assert.IsTrue(manifest.DefaultOptions.IsObject);
            Assert.AreEqual(0, manifest.DefaultOptions.Count);
        }

        [Test]
        public void SeatsKeepTheirDeclaredNamesAndColors()
        {
            ModManifest manifest = ModManifest.Parse(Manifest("seats",
                ",\"players\":{\"min\":2,\"max\":3},\"seats\":[{\"name\":\"Red\",\"color\":\"#FF0000\"},{\"name\":\"Blue\"}]"));
            Assert.AreEqual(3, manifest.Seats.Count);
            Assert.AreEqual("Red", manifest.GetSeat(1).Name);
            Assert.AreEqual("#FF0000", manifest.GetSeat(1).Color);
            Assert.AreEqual("Blue", manifest.GetSeat(2).Name);
            Assert.AreEqual(7, manifest.GetSeat(2).Color.Length);
            Assert.AreEqual("Player 3", manifest.GetSeat(3).Name);
            Assert.IsNull(manifest.GetSeat(0));
            Assert.IsNull(manifest.GetSeat(4));
        }

        [Test]
        public void OptionsAndPlayerCountsAreRead()
        {
            ModManifest manifest = ModManifest.Parse(Manifest("opts", ",\"version\":\"2.1.0\",\"description\":\"d\",\"players\":{\"min\":1,\"max\":4},\"options\":{\"size\":9}"));
            Assert.AreEqual("2.1.0", manifest.Version);
            Assert.AreEqual("d", manifest.Description);
            Assert.AreEqual(1, manifest.MinPlayers);
            Assert.AreEqual(4, manifest.MaxPlayers);
            Assert.AreEqual(9, manifest.DefaultOptions["size"].AsInt());
        }

        [TestCase("a")]
        [TestCase("0abc")]
        [TestCase("my-mod_2")]
        public void AcceptsValidIds(string id)
        {
            Assert.IsTrue(ModManifest.IsValidId(id));
        }

        [TestCase("")]
        [TestCase("Upper")]
        [TestCase("_lead")]
        [TestCase("-lead")]
        [TestCase("has space")]
        [TestCase("dot.dot")]
        [TestCase("slash/slash")]
        public void RejectsInvalidIds(string id)
        {
            Assert.IsFalse(ModManifest.IsValidId(id));
        }

        [Test]
        public void RejectsOverlongIds()
        {
            Assert.IsTrue(ModManifest.IsValidId(new string('a', 64)));
            Assert.IsFalse(ModManifest.IsValidId(new string('a', 65)));
        }

        [TestCase("main.luau")]
        [TestCase("lib/util.luau")]
        [TestCase("a/b/c.luau")]
        public void AcceptsValidModulePaths(string path)
        {
            Assert.IsTrue(ModManifest.IsValidModulePath(path));
        }

        [TestCase("")]
        [TestCase("main.lua")]
        [TestCase("/main.luau")]
        [TestCase("../main.luau")]
        [TestCase("a/../b.luau")]
        [TestCase("./main.luau")]
        [TestCase("a//b.luau")]
        [TestCase("a\\b.luau")]
        [TestCase("main.luau/")]
        public void RejectsInvalidModulePaths(string path)
        {
            Assert.IsFalse(ModManifest.IsValidModulePath(path));
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("{\"id\":\"x\",\"api\":1,\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"\",\"api\":1,\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"Bad Id\",\"name\":\"x\",\"api\":1,\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":2,\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":\"1\",\"entry\":\"main.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"../x.luau\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.txt\"}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":0,\"max\":2}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":3,\"max\":2}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":2,\"max\":17}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"seats\":[{\"color\":\"red\"},{}]}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"seats\":[{\"color\":\"#12345\"},{}]}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"seats\":[{\"color\":\"#12345g\"},{}]}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"options\":5}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"options\":[1]}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"client\":5}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"client\":[]}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":2,\"max\":3,\"option\":5},\"options\":{\"players\":0}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":2,\"max\":3,\"option\":\"\"},\"options\":{\"players\":0}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":2,\"max\":3,\"option\":\"players\"}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"min\":2,\"max\":3,\"option\":\"count\"},\"options\":{\"players\":0}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"computer\":\"no\"}}")]
        [TestCase("{\"id\":\"x\",\"name\":\"x\",\"api\":1,\"entry\":\"x.luau\",\"players\":{\"computer\":0}}")]
        public void RejectsInvalidManifests(string json)
        {
            Assert.Throws<ModException>(() => ModManifest.Parse(json));
        }

        [Test]
        public void ClientHintsAreKeptAsGiven()
        {
            ModManifest none = ModManifest.Parse(MinimalManifest);
            Assert.IsTrue(none.Client.IsObject);
            Assert.AreEqual(0, none.Client.Count);

            ModManifest hinted = ModManifest.Parse(Manifest("hinted", ",\"client\":{\"board\":{\"style\":\"checker\"},\"click\":{\"action\":\"play\"}}"));
            Assert.AreEqual("checker", hinted.Client["board"]["style"].AsString());
            Assert.AreEqual("play", hinted.Client["click"]["action"].AsString());
        }

        [Test]
        public void EmptyArrayCountsAsEmptyOptions()
        {
            ModManifest manifest = ModManifest.Parse(Manifest("empty", ",\"options\":[]"));
            Assert.IsTrue(manifest.DefaultOptions.IsObject);
            Assert.AreEqual(0, manifest.DefaultOptions.Count);
        }

        [Test]
        public void AGameCanNameTheOptionThatSetsHowManyPlayAndTurnTheComputerOff()
        {
            ModManifest plain = ModManifest.Parse(MinimalManifest);
            Assert.AreEqual(string.Empty, plain.PlayersOption);
            Assert.IsTrue(plain.AllowComputer);
            Assert.IsFalse(plain.ChoosesPlayerCount);
            Assert.AreEqual(2, plain.SeatCount(plain.DefaultOptions));
            Assert.AreEqual(2, plain.SeatCount(null));

            ModManifest manifest = ModManifest.Parse(Manifest("three",
                ",\"players\":{\"min\":2,\"max\":3,\"option\":\"players\",\"computer\":false},\"options\":{\"players\":0}"));
            Assert.AreEqual("players", manifest.PlayersOption);
            Assert.IsFalse(manifest.AllowComputer);
            Assert.IsTrue(manifest.ChoosesPlayerCount);
            Assert.AreEqual(3, manifest.SeatCount(manifest.DefaultOptions), "0 is no choice: the game plays with every seat");
            Assert.AreEqual(2, manifest.SeatCount(JsonValue.Parse("{\"players\":2}")));
            Assert.AreEqual(3, manifest.SeatCount(JsonValue.Parse("{\"players\":3}")));

            // The option only counts when it is a whole number of players that the game takes.
            foreach (string odd in new[] { "{\"players\":1}", "{\"players\":4}", "{\"players\":2.5}", "{\"players\":\"2\"}", "{\"players\":null}", "{}", "[]", "5" })
                Assert.AreEqual(3, manifest.SeatCount(JsonValue.Parse(odd)), odd);
            Assert.AreEqual(3, manifest.SeatCount(null));
        }

        [Test]
        public void TheNumberOfPlayersIsOnlyAChoiceWhereItCanBeOne()
        {
            const string options = ",\"options\":{\"players\":0}";
            Assert.IsTrue(ModManifest.Parse(Manifest("a", ",\"players\":{\"min\":2,\"max\":4,\"option\":\"players\"}" + options)).ChoosesPlayerCount);

            // One number of players, players who sit at the same grid or at tables of their own, or an option that is not
            // named have nothing to choose.
            Assert.IsFalse(ModManifest.Parse(Manifest("b", ",\"players\":{\"min\":2,\"max\":2,\"option\":\"players\"}" + options)).ChoosesPlayerCount);
            Assert.IsFalse(ModManifest.Parse(Manifest("c",
                ",\"players\":{\"min\":1,\"max\":2,\"option\":\"players\"},\"multiplayer\":{\"cooperative\":true}" + options)).ChoosesPlayerCount);
            Assert.IsFalse(ModManifest.Parse(Manifest("d",
                ",\"players\":{\"min\":1,\"max\":4,\"option\":\"players\"},\"multiplayer\":{\"mode\":\"split\",\"players\":4}" + options)).ChoosesPlayerCount);
            Assert.IsFalse(ModManifest.Parse(Manifest("e", ",\"players\":{\"min\":2,\"max\":4}" + options)).ChoosesPlayerCount);
        }

        [Test]
        public void AGameHasAComputerPlayerWhenItBringsOneAndAllowsIt()
        {
            const string ai = ",\"ai\":{\"levels\":[10,100]}";
            Assert.IsFalse(ModManifest.Parse(MinimalManifest).HasComputerPlayer, "no player to play with");
            Assert.IsTrue(ModManifest.Parse(Manifest("ai", ai)).HasComputerPlayer);
            Assert.IsTrue(ModManifest.Parse(Manifest("yes", ai + ",\"players\":{\"computer\":true}")).HasComputerPlayer);
            ModManifest off = ModManifest.Parse(Manifest("off", ai + ",\"players\":{\"computer\":false}"));
            Assert.IsFalse(off.AllowComputer);
            Assert.IsFalse(off.HasComputerPlayer, "the game does not want one");
        }

        [Test]
        public void TheGamesThatShipSayWhoPlaysThem()
        {
            // Go-Stop is played by two or three, which the host chooses; until it does every seat plays.
            ModManifest gostop = TestSupport.LoadMod("gostop").Manifest;
            Assert.AreEqual("players", gostop.PlayersOption);
            Assert.IsTrue(gostop.ChoosesPlayerCount);
            Assert.IsTrue(gostop.HasComputerPlayer);
            Assert.AreEqual(3, gostop.SeatCount(gostop.DefaultOptions));
            Assert.AreEqual(2, gostop.SeatCount(JsonValue.Parse("{\"players\":2}")));

            foreach (string id in new[] { "chess", "gomoku" })
            {
                ModManifest manifest = TestSupport.LoadMod(id).Manifest;
                Assert.AreEqual(string.Empty, manifest.PlayersOption, id);
                Assert.IsFalse(manifest.ChoosesPlayerCount, id);
                Assert.IsTrue(manifest.AllowComputer, id);
                Assert.IsTrue(manifest.HasComputerPlayer, id);
                Assert.AreEqual(2, manifest.SeatCount(manifest.DefaultOptions), id);
            }

            // Sudoku has three hints in place of a computer to play with.
            ModManifest sudoku = TestSupport.LoadMod("sudoku").Manifest;
            Assert.IsFalse(sudoku.AllowComputer);
            Assert.IsFalse(sudoku.HasComputerPlayer);
            Assert.IsFalse(sudoku.ChoosesPlayerCount);
            Assert.AreEqual(2, sudoku.SeatCount(sudoku.DefaultOptions));

            ModManifest cubic3 = TestSupport.LoadMod("cubic3").Manifest;
            Assert.IsFalse(cubic3.HasComputerPlayer);
            Assert.IsFalse(cubic3.ChoosesPlayerCount);
        }

        [Test]
        public void PackageRequiresItsEntryScript()
        {
            Assert.Throws<ModException>(() => new ModPackage(MinimalManifest, new Dictionary<string, string> { { "other.luau", MinimalScript } }));
        }

        [Test]
        public void PackageRejectsBadScriptPaths()
        {
            Dictionary<string, string> sources = new Dictionary<string, string> { { "main.luau", MinimalScript }, { "../escape.luau", "return {}" } };
            Assert.Throws<ModException>(() => new ModPackage(MinimalManifest, sources));
        }

        [Test]
        public void PackageRejectsOversizedScripts()
        {
            Dictionary<string, string> sources = new Dictionary<string, string> { { "main.luau", new string('x', ModPackage.MaxSourceBytes + 1) } };
            Assert.Throws<ModException>(() => new ModPackage(MinimalManifest, sources));
        }

        [Test]
        public void PackageExposesSortedSources()
        {
            ModPackage mod = new ModPackage(MinimalManifest, new Dictionary<string, string>
            {
                { "main.luau", MinimalScript }, { "z.luau", "return 1" }, { "a/b.luau", "return 2" }
            });
            List<string> keys = new List<string>(mod.Sources.Keys);
            Assert.AreEqual(3, keys.Count);
            Assert.AreEqual("a/b.luau", keys[0]);
            Assert.AreEqual("main.luau", keys[1]);
            Assert.AreEqual("z.luau", keys[2]);
            Assert.IsNull(mod.Directory);
        }

        [Test]
        public void ContentHashTracksTheContent()
        {
            ModPackage a = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", MinimalScript } });
            ModPackage same = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", MinimalScript } });
            ModPackage script = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", MinimalScript + " " } });
            ModPackage manifest = new ModPackage(MinimalManifest.Replace("Mini", "Mini2"), new Dictionary<string, string> { { "main.luau", MinimalScript } });
            ModPackage extra = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", MinimalScript }, { "x.luau", "return 1" } });

            Assert.AreEqual(64, a.ContentHash.Length);
            Assert.AreEqual(a.ContentHash, same.ContentHash);
            Assert.AreNotEqual(a.ContentHash, script.ContentHash);
            Assert.AreNotEqual(a.ContentHash, manifest.ContentHash);
            Assert.AreNotEqual(a.ContentHash, extra.ContentHash);
        }

        [Test]
        public void ContentHashIgnoresInsertionOrder()
        {
            Dictionary<string, string> first = new Dictionary<string, string> { { "main.luau", MinimalScript }, { "b.luau", "return 2" }, { "a.luau", "return 1" } };
            Dictionary<string, string> second = new Dictionary<string, string> { { "a.luau", "return 1" }, { "b.luau", "return 2" }, { "main.luau", MinimalScript } };
            Assert.AreEqual(new ModPackage(MinimalManifest, first).ContentHash, new ModPackage(MinimalManifest, second).ContentHash);
        }

        [Test]
        public void CreateVmBootsTheEntryScript()
        {
            ModPackage mod = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", MinimalScript } });
            using (LuauVm vm = mod.CreateVm())
                Assert.IsTrue(vm.CallJson("ping")["ok"].AsBool());
        }

        [Test]
        public void CreateVmReportsScriptErrors()
        {
            ModPackage mod = new ModPackage(MinimalManifest, new Dictionary<string, string> { { "main.luau", "local x = = 1" } });
            LuauException e = Assert.Throws<LuauException>(() => mod.CreateVm());
            Assert.AreEqual(VmStatus.Compile, e.Status);
        }

        [Test]
        public void LoadsAModFromADirectory()
        {
            string root = NewDirectory();
            TestSupport.WriteFile(root, "config.json", MinimalManifest);
            TestSupport.WriteFile(root, "main.luau", MinimalScript);
            TestSupport.WriteFile(root, "lib/util.luau", "return {}");
            TestSupport.WriteFile(root, "lib/deep/more.luau", "return {}");

            ModPackage mod = ModLoader.LoadDirectory(root);
            Assert.AreEqual("mini", mod.Manifest.Id);
            Assert.AreEqual(Path.GetFullPath(root), mod.Directory);
            Assert.AreEqual(3, mod.Sources.Count);
            Assert.IsTrue(mod.Sources.ContainsKey("main.luau"));
            Assert.IsTrue(mod.Sources.ContainsKey("lib/util.luau"));
            Assert.IsTrue(mod.Sources.ContainsKey("lib/deep/more.luau"));
        }

        [Test]
        public void LoaderIgnoresHiddenAndNonScriptFiles()
        {
            string root = NewDirectory();
            TestSupport.WriteFile(root, "config.json", MinimalManifest);
            TestSupport.WriteFile(root, "main.luau", MinimalScript);
            TestSupport.WriteFile(root, "main.luau.meta", "guid: x");
            TestSupport.WriteFile(root, "notes.txt", "not a script");
            TestSupport.WriteFile(root, ".hidden.luau", "return 1");
            TestSupport.WriteFile(root, ".git/hooks.luau", "return 1");
            TestSupport.WriteFile(root, "lib/.cache/x.luau", "return 1");

            ModPackage mod = ModLoader.LoadDirectory(root);
            Assert.AreEqual(1, mod.Sources.Count);
            Assert.IsTrue(mod.Sources.ContainsKey("main.luau"));
        }

        [Test]
        public void LoaderReadsUtf8WithoutABom()
        {
            string root = NewDirectory();
            TestSupport.WriteFile(root, "config.json", MinimalManifest);
            TestSupport.WriteFile(root, "main.luau", "-- 한글 주석\n" + MinimalScript);
            ModPackage mod = ModLoader.LoadDirectory(root);
            StringAssert.Contains("한글", mod.Sources["main.luau"]);
        }

        [Test]
        public void LoaderRejectsMissingPieces()
        {
            string empty = NewDirectory();
            Assert.Throws<ModException>(() => ModLoader.LoadDirectory(Path.Combine(empty, "nope")));
            Assert.Throws<ModException>(() => ModLoader.LoadDirectory(empty));

            string noEntry = NewDirectory();
            TestSupport.WriteFile(noEntry, "config.json", MinimalManifest);
            ModException e = Assert.Throws<ModException>(() => ModLoader.LoadDirectory(noEntry));
            StringAssert.Contains("main.luau", e.Message);
        }

        [Test]
        public void DiscoverFindsModsAndReportsProblems()
        {
            string root = NewDirectory();
            TestSupport.WriteFile(root, "b_mod/config.json", Manifest("bmod"));
            TestSupport.WriteFile(root, "b_mod/main.luau", MinimalScript);
            TestSupport.WriteFile(root, "a_mod/config.json", Manifest("amod"));
            TestSupport.WriteFile(root, "a_mod/main.luau", MinimalScript);
            TestSupport.WriteFile(root, "broken/config.json", "{ not json");
            TestSupport.WriteFile(root, "dup/config.json", Manifest("amod"));
            TestSupport.WriteFile(root, "dup/main.luau", MinimalScript);
            TestSupport.WriteFile(root, "plain_folder/readme.txt", "no manifest here");

            ModDiscovery discovery = ModLoader.Discover(root);
            Assert.AreEqual(2, discovery.Mods.Count);
            Assert.AreEqual("amod", discovery.Mods[0].Manifest.Id);
            Assert.AreEqual("bmod", discovery.Mods[1].Manifest.Id);
            Assert.AreEqual(2, discovery.Errors.Count);

            bool sawBroken = false;
            bool sawDuplicate = false;
            foreach (ModLoadError error in discovery.Errors)
            {
                if (error.Directory.EndsWith("broken", StringComparison.Ordinal))
                    sawBroken = true;
                if (error.Directory.EndsWith("dup", StringComparison.Ordinal))
                {
                    sawDuplicate = true;
                    StringAssert.Contains("duplicate", error.Message);
                }
            }
            Assert.IsTrue(sawBroken);
            Assert.IsTrue(sawDuplicate);
        }

        [Test]
        public void DiscoverToleratesMissingRoots()
        {
            Assert.AreEqual(0, ModLoader.Discover(null).Mods.Count);
            Assert.AreEqual(0, ModLoader.Discover(Path.Combine(Path.GetTempPath(), "bse-no-such-directory-" + Guid.NewGuid().ToString("N"))).Errors.Count);
        }

        [Test]
        public void ShippedModsLoadWithoutErrors()
        {
            ModDiscovery discovery = ModLoader.Discover(TestSupport.ModsRoot);
            foreach (ModLoadError error in discovery.Errors)
                Assert.Fail(error.Directory + ": " + error.Message);
            Assert.GreaterOrEqual(discovery.Mods.Count, 1);
            foreach (ModPackage mod in discovery.Mods)
            {
                using (LuauVm vm = mod.CreateVm())
                    Assert.IsTrue(vm.CallJson("ping")["ok"].AsBool());
            }
        }

        [Test]
        public void ImagesOfAModAreReadAndArePartOfItsHash()
        {
            string plain = NewDirectory();
            TestSupport.WriteFile(plain, "config.json", Manifest("pictures"));
            TestSupport.WriteFile(plain, "main.luau", "return {}");
            ModPackage without = ModLoader.LoadDirectory(plain);
            Assert.AreEqual(0, without.Assets.Count);
            Assert.AreEqual(new ModPackage(Manifest("pictures"), new Dictionary<string, string> { { "main.luau", "return {}" } }).ContentHash,
                without.ContentHash, "a mod without images keeps the hash it had");

            string root = NewDirectory();
            TestSupport.WriteFile(root, "config.json", Manifest("pictures"));
            TestSupport.WriteFile(root, "main.luau", "return {}");
            File.WriteAllBytes(Path.Combine(root, "cards.png"), new byte[] { 1, 2, 3 });
            Directory.CreateDirectory(Path.Combine(root, "art"));
            File.WriteAllBytes(Path.Combine(root, "art", "Back.JPG"), new byte[] { 4 });
            File.WriteAllBytes(Path.Combine(root, "notes.txt"), new byte[] { 5 });
            Directory.CreateDirectory(Path.Combine(root, ".hidden"));
            File.WriteAllBytes(Path.Combine(root, ".hidden", "x.png"), new byte[] { 6 });

            ModPackage mod = ModLoader.LoadDirectory(root);
            Assert.AreEqual(2, mod.Assets.Count);
            Assert.AreEqual(3, mod.Assets["cards.png"].Length);
            Assert.AreEqual(3, mod.Assets["cards.png"][2]);
            Assert.AreEqual(4, mod.Assets["art/Back.JPG"][0]);
            Assert.AreNotEqual(without.ContentHash, mod.ContentHash);

            File.WriteAllBytes(Path.Combine(root, "cards.png"), new byte[] { 1, 2, 4 });
            Assert.AreNotEqual(mod.ContentHash, ModLoader.LoadDirectory(root).ContentHash, "another picture is another mod");
        }

        [Test]
        public void ImagePathsAreCheckedAndLimited()
        {
            Assert.IsTrue(ModPackage.IsAssetPath("cards.png"));
            Assert.IsTrue(ModPackage.IsAssetPath("art/back.jpeg"));
            Assert.IsFalse(ModPackage.IsAssetPath("cards.svg"));
            Assert.IsFalse(ModPackage.IsAssetPath("../cards.png"));
            Assert.IsFalse(ModPackage.IsAssetPath("/cards.png"));
            Assert.IsFalse(ModPackage.IsAssetPath("art\\cards.png"));
            Assert.IsFalse(ModPackage.IsAssetPath("c:/cards.png"));
            Assert.IsFalse(ModPackage.IsAssetPath(".git/cards.png"));
            Assert.IsFalse(ModPackage.IsAssetPath(""));

            Dictionary<string, string> sources = new Dictionary<string, string> { { "main.luau", "return {}" } };
            Assert.Throws<ModException>(() => new ModPackage(Manifest("pictures"), sources,
                new Dictionary<string, byte[]> { { "../x.png", new byte[1] } }, null));
            Assert.Throws<ModException>(() => new ModPackage(Manifest("pictures"), sources,
                new Dictionary<string, byte[]> { { "big.png", new byte[ModPackage.MaxAssetBytes + 1] } }, null));
        }
    }
}
