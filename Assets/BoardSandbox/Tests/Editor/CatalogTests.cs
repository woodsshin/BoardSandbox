using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Mods;
using NUnit.Framework;
using static BoardSandbox.Tests.TestMods;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class CatalogTests
    {
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

        private static List<string> Ids(IEnumerable<ModEntry> entries)
        {
            List<string> ids = new List<string>();
            foreach (ModEntry entry in entries)
                ids.Add(entry.Id);
            return ids;
        }

        [Test]
        public void OriginsHaveLabels()
        {
            Assert.AreEqual("Built-in", ModOrigin.BuiltIn.Label());
            Assert.AreEqual("Local", ModOrigin.Local.Label());
            Assert.AreEqual("Workshop", ModOrigin.Workshop.Label());
            Assert.IsTrue(ModOrigin.BuiltIn < ModOrigin.Local && ModOrigin.Local < ModOrigin.Workshop);
        }

        [Test]
        public void FolderSourceListsTheModsOfAFolder()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            TestSupport.WriteFile(root, "broken/config.json", "{ nope");
            TestSupport.WriteFile(root, "plain/readme.txt", "not a mod");

            FolderModSource source = new FolderModSource(root, ModOrigin.Workshop);
            Assert.AreEqual(ModOrigin.Workshop, source.Origin);
            Assert.AreEqual(root, source.Description);
            ModDiscovery found = source.Discover();
            Assert.AreEqual(1, found.Mods.Count);
            Assert.AreEqual("mini", found.Mods[0].Manifest.Id);
            Assert.AreEqual(1, found.Errors.Count);

            Assert.AreEqual(0, new FolderModSource(Path.Combine(root, "missing"), ModOrigin.Local).Discover().Mods.Count);
            Assert.Throws<ArgumentException>(() => new FolderModSource("  ", ModOrigin.Local));
            Assert.Throws<ArgumentException>(() => new FolderModSource(null, ModOrigin.Local));
        }

        [Test]
        public void CatalogRecordsTheOriginOfEachMod()
        {
            string local = NewDirectory();
            WriteMod(local, "mini", "Mini");

            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new FolderModSource(local, ModOrigin.Local),
                new FolderModSource(TestSupport.ModsRoot, ModOrigin.BuiltIn)
            });
            Assert.AreEqual(2, catalog.Sources.Count);
            Assert.AreEqual(ModOrigin.BuiltIn, catalog.FindEntry("gomoku").Origin);
            Assert.AreEqual(ModOrigin.Local, catalog.FindEntry("mini").Origin);
            Assert.AreSame(catalog.FindEntry("mini").Mod, catalog.Find("mini"));
            Assert.IsNull(catalog.FindEntry("nope"));
            Assert.IsNull(catalog.FindEntry(null));
            Assert.AreEqual(catalog.Entries.Count, catalog.Mods.Count);

            Assert.AreEqual(2, catalog.Origins.Count);
            Assert.AreEqual(ModOrigin.BuiltIn, catalog.Origins[0]);
            Assert.AreEqual(ModOrigin.Local, catalog.Origins[1]);
        }

        [Test]
        public void StringFoldersAreLocalSources()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            ModCatalog catalog = new ModCatalog(new[] { root, "", "  ", null });
            Assert.AreEqual(1, catalog.Sources.Count);
            Assert.AreEqual(ModOrigin.Local, catalog.Sources[0].Origin);
            Assert.AreEqual(ModOrigin.Local, catalog.FindEntry("mini").Origin);
        }

        [Test]
        public void EarlierSourcesWinForDuplicateIds()
        {
            string builtIn = NewDirectory();
            string workshop = NewDirectory();
            WriteMod(builtIn, "mini", "Mini");
            WriteMod(workshop, "mini", "Impostor");

            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new FolderModSource(builtIn, ModOrigin.BuiltIn),
                new FolderModSource(workshop, ModOrigin.Workshop)
            });
            Assert.AreEqual(1, catalog.Entries.Count);
            Assert.AreEqual("Mini", catalog.FindEntry("mini").Name);
            Assert.AreEqual(ModOrigin.BuiltIn, catalog.FindEntry("mini").Origin);
            Assert.AreEqual(1, catalog.Errors.Count);
            StringAssert.Contains("already provided", catalog.Errors[0].Message);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(workshop, "mini")), catalog.Errors[0].Directory);
        }

        [Test]
        public void ListingTheSameFolderTwiceIsNotAnError()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new FolderModSource(root, ModOrigin.Local),
                new FolderModSource(root, ModOrigin.Workshop)
            });
            Assert.AreEqual(1, catalog.Entries.Count);
            Assert.AreEqual(ModOrigin.Local, catalog.Entries[0].Origin);
            Assert.AreEqual(0, catalog.Errors.Count);
        }

        [Test]
        public void ASourceThatThrowsIsReportedAndTheOthersStillLoad()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new FailingSource(),
                new FolderModSource(root, ModOrigin.Local)
            });
            Assert.AreEqual(1, catalog.Entries.Count);
            Assert.AreEqual(1, catalog.Errors.Count);
            Assert.AreEqual("the subscribed items", catalog.Errors[0].Directory);
            StringAssert.Contains("the service is not running", catalog.Errors[0].Message);
        }

        [Test]
        public void ModsWithoutAFolderAreReportedByTheirSource()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.Workshop, "first list", Mod("alpha", "Alpha")),
                new ListSource(ModOrigin.Workshop, "second list", Mod("alpha", "Alpha again"))
            });
            Assert.AreEqual(1, catalog.Entries.Count);
            Assert.IsNull(catalog.Entries[0].Mod.Directory);
            Assert.AreEqual(1, catalog.Errors.Count);
            Assert.AreEqual("second list", catalog.Errors[0].Directory);
            StringAssert.Contains("another source", catalog.Errors[0].Message);
        }

        [Test]
        public void RefreshFollowsTheSources()
        {
            string root = NewDirectory();
            ModCatalog catalog = new ModCatalog(new IModSource[] { new FolderModSource(root, ModOrigin.Local) });
            Assert.AreEqual(0, catalog.Entries.Count);
            Assert.AreEqual(0, catalog.Origins.Count);

            WriteMod(root, "mini", "Mini");
            catalog.Refresh();
            Assert.AreEqual(1, catalog.Entries.Count);
            Assert.AreEqual(1, catalog.Mods.Count);

            Directory.Delete(Path.Combine(root, "mini"), true);
            catalog.Refresh();
            Assert.AreEqual(0, catalog.Entries.Count);
            Assert.AreEqual(0, catalog.Mods.Count);
        }

        [Test]
        public void SearchOrdersByOriginThenName()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.Workshop, "w", Mod("omok", "오목", "다섯 줄 연결")),
                new ListSource(ModOrigin.Local, "l", Mod("checkers", "Checkers", "Jump and capture")),
                new ListSource(ModOrigin.BuiltIn, "b", Mod("five", "five in a row", "Connect five stones"), Mod("chess", "Chess", "Classic strategy"),
                    Mod("tie-b", "Twin"), Mod("tie-a", "twin"))
            });
            Assert.AreEqual(new[] { "chess", "five", "tie-a", "tie-b", "checkers", "omok" }, Ids(catalog.Search(null)).ToArray());
            Assert.AreEqual(6, catalog.Search("").Count);
            Assert.AreEqual(6, catalog.Search("   ").Count);
        }

        [Test]
        public void SearchMatchesNameIdAndDescription()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.Workshop, "w", Mod("omok", "오목", "다섯 줄 연결")),
                new ListSource(ModOrigin.Local, "l", Mod("checkers", "Checkers", "Jump and capture")),
                new ListSource(ModOrigin.BuiltIn, "b", Mod("five", "Five in a Row", "Connect five stones"), Mod("chess", "Chess", "Classic strategy"))
            });

            Assert.AreEqual(new[] { "chess", "checkers" }, Ids(catalog.Search("ch")).ToArray());
            Assert.AreEqual(new[] { "chess", "checkers" }, Ids(catalog.Search("CH")).ToArray());
            Assert.AreEqual(new[] { "five" }, Ids(catalog.Search("CONNECT five")).ToArray());
            Assert.AreEqual(0, catalog.Search("five 오목").Count);
            Assert.AreEqual(new[] { "omok" }, Ids(catalog.Search("오목")).ToArray());
            Assert.AreEqual(new[] { "omok" }, Ids(catalog.Search("연결")).ToArray());
            Assert.AreEqual(new[] { "omok" }, Ids(catalog.Search("omok")).ToArray());
            Assert.AreEqual(new[] { "five" }, Ids(catalog.Search("stones")).ToArray());
            Assert.AreEqual(0, catalog.Search("zzz").Count);
        }

        [Test]
        public void SearchCanBeLimitedToAnOrigin()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.Local, "l", Mod("checkers", "Checkers")),
                new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess"), Mod("five", "Five"))
            });
            Assert.AreEqual(new[] { "checkers" }, Ids(catalog.Search("", ModOrigin.Local)).ToArray());
            Assert.AreEqual(new[] { "chess" }, Ids(catalog.Search("ch", ModOrigin.BuiltIn)).ToArray());
            Assert.AreEqual(0, catalog.Search("", ModOrigin.Workshop).Count);
            Assert.AreEqual(3, catalog.Search("", null).Count);
        }

        [Test]
        public void EntriesCountThePlayers()
        {
            Assert.AreEqual("2 players", new ModEntry(Mod("a", "A"), ModOrigin.Local).PlayersText);
            Assert.AreEqual("1 player", new ModEntry(Mod("a", "A", extra: ",\"players\":{\"min\":1}"), ModOrigin.Local).PlayersText);
            Assert.AreEqual("2-4 players", new ModEntry(Mod("a", "A", extra: ",\"players\":{\"min\":2,\"max\":4}"), ModOrigin.Local).PlayersText);
            Assert.AreEqual("4 players", new ModEntry(Mod("a", "A", extra: ",\"players\":{\"min\":4}"), ModOrigin.Local).PlayersText);
            Assert.Throws<ArgumentNullException>(() => new ModEntry(null, ModOrigin.Local));
        }

        [Test]
        public void EntriesSummarizeThePlayersAndTheOrigin()
        {
            Assert.AreEqual("2 players · Built-in", new ModEntry(Mod("a", "A"), ModOrigin.BuiltIn).Summary);
            Assert.AreEqual("2-4 players · Workshop", new ModEntry(Mod("a", "A", extra: ",\"players\":{\"min\":2,\"max\":4}"), ModOrigin.Workshop).Summary);
            Assert.AreEqual("1 player · Local", new ModEntry(Mod("a", "A", extra: ",\"players\":{\"min\":1}"), ModOrigin.Local).Summary);
        }

        [Test]
        public void EntriesHaveAnInitialAndAHue()
        {
            Assert.AreEqual("G", new ModEntry(Mod("a", "Gomoku"), ModOrigin.Local).Initial);
            Assert.AreEqual("C", new ModEntry(Mod("a", "  chess"), ModOrigin.Local).Initial);
            Assert.AreEqual("오", new ModEntry(Mod("a", "오목"), ModOrigin.Local).Initial);
            Assert.AreEqual("\U0001F600", new ModEntry(Mod("a", "\U0001F600 party"), ModOrigin.Local).Initial);

            ModEntry first = new ModEntry(Mod("gomoku", "Gomoku"), ModOrigin.BuiltIn);
            ModEntry again = new ModEntry(Mod("gomoku", "Another name"), ModOrigin.Local);
            ModEntry other = new ModEntry(Mod("race", "Race"), ModOrigin.BuiltIn);
            Assert.IsTrue(first.TileHue >= 0f && first.TileHue < 1f);
            Assert.AreEqual(first.TileHue, again.TileHue);
            Assert.AreNotEqual(first.TileHue, other.TileHue);
        }

        [Test]
        public void ClipShortensLongTexts()
        {
            Assert.AreEqual("abc", DisplayText.Clip("abc", 5));
            Assert.AreEqual("abc…", DisplayText.Clip("abcdef", 4));
            Assert.AreEqual("abcde", DisplayText.Clip("abcde", 5));
            Assert.AreEqual("a b", DisplayText.Clip("a\nb", 10));
            Assert.AreEqual("a  b", DisplayText.Clip("a\r\nb", 10));
            Assert.AreEqual("a\nb", DisplayText.Clip("a\nb", 10, false));
            Assert.AreEqual("", DisplayText.Clip(null, 5));
            Assert.AreEqual("", DisplayText.Clip("", 5));
            Assert.AreEqual("", DisplayText.Clip("abc", 0));
            Assert.AreEqual("…", DisplayText.Clip("abcdef", 1));

            string cut = DisplayText.Clip("ab\U0001F600cd", 4);
            Assert.AreEqual("ab…", cut);
            Assert.AreEqual("ab\U0001F600…", DisplayText.Clip("ab\U0001F600cd", 5));
        }

        [Test]
        public void HostStartsAGameFromACatalogEntry()
        {
            ModCatalog catalog = new ModCatalog(new[] { TestSupport.ModsRoot });
            using (GameHost host = new GameHost(catalog))
            {
                ModPackage gomoku = catalog.Find("gomoku");
                Assert.IsFalse(host.IsRunning(gomoku));
                Assert.IsFalse(host.IsRunning(null));

                int replaced = 0;
                host.SessionReplaced += () => replaced++;
                Assert.IsTrue(host.StartGame(gomoku));
                Assert.AreEqual(1, replaced);
                Assert.IsTrue(host.Session.IsStarted);
                Assert.IsTrue(host.IsRunning(gomoku));
                Assert.IsFalse(host.IsRunning(Mod("other", "Other")));

                Assert.IsTrue(host.SelectMod(gomoku));
                Assert.IsFalse(host.Session.IsStarted);
                Assert.IsFalse(host.IsRunning(gomoku));
            }
        }

        [Test]
        public void AnEditedModIsAnotherVersionOfTheRunningGame()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            ModCatalog catalog = new ModCatalog(new[] { root });
            using (GameHost host = new GameHost(catalog))
            {
                Assert.IsTrue(host.StartGame(catalog.Find("mini")));
                Assert.IsTrue(host.IsRunning(catalog.Find("mini")));

                catalog.Refresh();
                Assert.IsTrue(host.IsRunning(catalog.Find("mini")));

                TestSupport.WriteFile(root, "mini/main.luau", MinimalScript + "\n-- edited");
                catalog.Refresh();
                Assert.IsFalse(host.IsRunning(catalog.Find("mini")));
                Assert.IsTrue(host.StartGame(catalog.Find("mini")));
                Assert.IsTrue(host.IsRunning(catalog.Find("mini")));
            }
        }

        [Test]
        public void StartingAModThatCannotBootKeepsTheRunningGame()
        {
            ModCatalog catalog = new ModCatalog(new[] { TestSupport.ModsRoot });
            using (GameHost host = new GameHost(catalog))
            {
                Assert.IsTrue(host.StartGame(catalog.Find("gomoku")));
                GameSession before = host.Session;

                ModPackage broken = Mod("broken", "Broken", script: "error('boom')");
                Assert.IsFalse(host.StartGame(broken));
                Assert.AreSame(before, host.Session);
                Assert.IsTrue(host.Session.IsStarted);

                IReadOnlyList<Toast> toasts = host.Toasts.Active(host.Now);
                Assert.AreEqual(1, toasts.Count);
                Assert.AreEqual(ToastLevel.Error, toasts[0].Level);
                StringAssert.Contains("Broken", toasts[0].Text);
                Assert.Throws<ArgumentNullException>(() => host.StartGame(null));
            }
        }

        [Test]
        public void ARequestedGameIsOpenedWhateverElseThereIs()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess"), Mod("five", "Five"))
            });
            Assert.AreEqual("five", catalog.FindStartup("five").Id);
            Assert.AreEqual("five", catalog.FindStartup("  five ").Id);
            Assert.IsNull(catalog.FindStartup("go"));
            Assert.IsNull(catalog.FindStartup("FIVE"));
        }

        [Test]
        public void TheOnlyGameIsOpenedWithoutARequest()
        {
            ModCatalog one = new ModCatalog(new IModSource[] { new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess")) });
            Assert.AreEqual("chess", one.FindStartup(null).Id);
            Assert.AreEqual("chess", one.FindStartup("").Id);
            Assert.AreEqual("chess", one.FindStartup("   ").Id);

            ModCatalog two = new ModCatalog(new IModSource[] { new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess"), Mod("five", "Five")) });
            Assert.IsNull(two.FindStartup(null));
            Assert.IsNull(two.FindStartup(""));

            ModCatalog none = new ModCatalog(new IModSource[0]);
            Assert.IsNull(none.FindStartup(null));
            Assert.IsNull(none.FindStartup("chess"));
        }
    }
}
