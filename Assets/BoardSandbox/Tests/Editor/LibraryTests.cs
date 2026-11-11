using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Mods;
using NUnit.Framework;
using static BoardSandbox.Tests.TestMods;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class LibraryTests
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

        private static ModCatalog ThreeGames()
        {
            return new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess", "Classic strategy"), Mod("five", "Five", "Connect five stones")),
                new ListSource(ModOrigin.Local, "l", Mod("checkers", "Checkers", "Jump and capture"))
            });
        }

        private static string[] Ids(GameLibrary library)
        {
            List<string> ids = new List<string>();
            foreach (ModEntry entry in library.Visible)
                ids.Add(entry.Id);
            return ids.ToArray();
        }

        [Test]
        public void TheFirstGameIsSelectedAtTheStart()
        {
            using (GameHost host = new GameHost(ThreeGames()))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual(new[] { "chess", "five", "checkers" }, Ids(library));
                Assert.AreEqual(0, library.SelectedIndex);
                Assert.AreEqual("chess", library.Selected.Id);
                Assert.AreEqual("chess", library.SelectedId);
                Assert.AreEqual("", library.Query);
                Assert.IsNull(library.Origin);
            }
        }

        [Test]
        public void TheGameOnTheTableIsSelectedAtTheStart()
        {
            ModCatalog catalog = ThreeGames();
            using (GameHost host = new GameHost(catalog))
            {
                Assert.IsTrue(host.SelectMod(catalog.Find("checkers")));
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual("checkers", library.SelectedId);
                Assert.AreEqual(2, library.SelectedIndex);
            }
        }

        [Test]
        public void AnEmptyCatalogSelectsNothing()
        {
            using (GameHost host = new GameHost(new ModCatalog(new IModSource[0])))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual(0, library.Visible.Count);
                Assert.AreEqual(-1, library.SelectedIndex);
                Assert.IsNull(library.Selected);
                Assert.IsNull(library.SelectedId);
                library.Move(1);
                library.Move(-1);
                library.Select("chess");
                Assert.IsNull(library.Selected);
                Assert.IsFalse(library.Play());
                Assert.IsFalse(library.IsRunning(null));
            }
        }

        [Test]
        public void SearchingNarrowsTheListAndKeepsASurvivingSelection()
        {
            using (GameHost host = new GameHost(ThreeGames()))
            {
                GameLibrary library = new GameLibrary(host);
                library.Select("checkers");

                library.SetQuery("ch");
                Assert.AreEqual(new[] { "chess", "checkers" }, Ids(library));
                Assert.AreEqual("checkers", library.SelectedId);
                Assert.AreEqual(1, library.SelectedIndex);

                library.SetQuery("five");
                Assert.AreEqual(new[] { "five" }, Ids(library));
                Assert.AreEqual("five", library.SelectedId);
                Assert.AreEqual(0, library.SelectedIndex);

                library.SetQuery("nothing like this");
                Assert.AreEqual(0, library.Visible.Count);
                Assert.IsNull(library.Selected);
                Assert.AreEqual(-1, library.SelectedIndex);

                library.SetQuery(null);
                Assert.AreEqual("", library.Query);
                Assert.AreEqual(3, library.Visible.Count);
                Assert.IsNotNull(library.Selected);
            }
        }

        [Test]
        public void TheOriginFilterListsOneOrigin()
        {
            using (GameHost host = new GameHost(ThreeGames()))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual(2, library.Origins.Count);

                library.SetOrigin(ModOrigin.Local);
                Assert.AreEqual(new[] { "checkers" }, Ids(library));
                Assert.AreEqual("checkers", library.SelectedId);

                library.SetQuery("ch");
                library.SetOrigin(ModOrigin.BuiltIn);
                Assert.AreEqual(new[] { "chess" }, Ids(library));

                library.SetOrigin(null);
                Assert.AreEqual(new[] { "chess", "checkers" }, Ids(library));
            }
        }

        [Test]
        public void AFilterForAnOriginThatIsGoneIsDropped()
        {
            string root = NewDirectory();
            WriteMod(root, "mini", "Mini");
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.BuiltIn, "b", Mod("chess", "Chess")),
                new FolderModSource(root, ModOrigin.Workshop)
            });
            using (GameHost host = new GameHost(catalog))
            {
                GameLibrary library = new GameLibrary(host);
                library.SetOrigin(ModOrigin.Workshop);
                Assert.AreEqual(new[] { "mini" }, Ids(library));

                Directory.Delete(Path.Combine(root, "mini"), true);
                library.Rescan();
                Assert.IsNull(library.Origin);
                Assert.AreEqual(new[] { "chess" }, Ids(library));
                Assert.AreEqual("chess", library.SelectedId);
            }
        }

        [Test]
        public void SelectingAnUnlistedGameChangesNothing()
        {
            using (GameHost host = new GameHost(ThreeGames()))
            {
                GameLibrary library = new GameLibrary(host);
                library.Select("five");
                Assert.AreEqual("five", library.SelectedId);
                library.Select("go");
                library.Select(null);
                Assert.AreEqual("five", library.SelectedId);

                library.SetOrigin(ModOrigin.Local);
                library.Select("chess");
                Assert.AreEqual("checkers", library.SelectedId);
            }
        }

        [Test]
        public void MovingStopsAtBothEnds()
        {
            using (GameHost host = new GameHost(ThreeGames()))
            {
                GameLibrary library = new GameLibrary(host);
                library.Move(-1);
                Assert.AreEqual(0, library.SelectedIndex);
                library.Move(1);
                Assert.AreEqual("five", library.SelectedId);
                library.Move(1);
                library.Move(1);
                Assert.AreEqual(2, library.SelectedIndex);
                Assert.AreEqual("checkers", library.SelectedId);
                library.Move(-5);
                Assert.AreEqual(0, library.SelectedIndex);
                library.Move(0);
                Assert.AreEqual(0, library.SelectedIndex);
                library.Move(100);
                Assert.AreEqual(2, library.SelectedIndex);
            }
        }

        [Test]
        public void RescanFindsNewGamesAndKeepsTheSelection()
        {
            string root = NewDirectory();
            WriteMod(root, "beta", "Beta");
            ModCatalog catalog = new ModCatalog(new[] { root });
            using (GameHost host = new GameHost(catalog))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual(new[] { "beta" }, Ids(library));

                WriteMod(root, "alpha", "Alpha");
                library.Select("beta");
                library.Rescan();
                Assert.AreEqual(new[] { "alpha", "beta" }, Ids(library));
                Assert.AreEqual("beta", library.SelectedId);
                Assert.AreEqual(1, library.SelectedIndex);
                Assert.AreEqual(1, library.Origins.Count);
            }
        }

        [Test]
        public void RescanMovesTheSelectionWhenItsGameIsGone()
        {
            string root = NewDirectory();
            WriteMod(root, "alpha", "Alpha");
            WriteMod(root, "beta", "Beta");
            using (GameHost host = new GameHost(new ModCatalog(new[] { root })))
            {
                GameLibrary library = new GameLibrary(host);
                library.Select("beta");
                Directory.Delete(Path.Combine(root, "beta"), true);
                library.Rescan();
                Assert.AreEqual(new[] { "alpha" }, Ids(library));
                Assert.AreEqual("alpha", library.SelectedId);
            }
        }

        [Test]
        public void PlayStartsTheSelectedGame()
        {
            ModCatalog catalog = new ModCatalog(new[] { TestSupport.ModsRoot });
            using (GameHost host = new GameHost(catalog))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.AreEqual("gomoku", library.SelectedId);
                Assert.IsFalse(library.IsRunning(library.Selected));

                Assert.IsTrue(library.Play());
                Assert.IsTrue(host.Session.IsStarted);
                Assert.AreEqual("gomoku", host.Mod.Manifest.Id);
                Assert.IsTrue(library.IsRunning(library.Selected));

                Assert.IsTrue(library.Play());
                Assert.IsTrue(host.Session.IsStarted);
            }
        }

        [Test]
        public void PlayingAGameThatCannotBootLeavesTheLibraryAsItWas()
        {
            ModCatalog catalog = new ModCatalog(new IModSource[]
            {
                new ListSource(ModOrigin.Local, "l", Mod("broken", "Broken", script: "error('boom')"))
            });
            using (GameHost host = new GameHost(catalog))
            {
                GameLibrary library = new GameLibrary(host);
                Assert.IsFalse(library.Play());
                Assert.IsNull(host.Session);
                Assert.AreEqual("broken", library.SelectedId);
            }
        }

        [Test]
        public void ResetClearsTheSearchAndSelectsTheRunningGame()
        {
            ModCatalog catalog = ThreeGames();
            using (GameHost host = new GameHost(catalog))
            {
                GameLibrary library = new GameLibrary(host);
                library.SetOrigin(ModOrigin.Local);
                library.SetQuery("jump");
                Assert.AreEqual("checkers", library.SelectedId);

                Assert.IsTrue(host.StartGame(catalog.Find("five")));
                library.Reset();
                Assert.AreEqual("", library.Query);
                Assert.IsNull(library.Origin);
                Assert.AreEqual(3, library.Visible.Count);
                Assert.AreEqual("five", library.SelectedId);
                Assert.AreEqual(1, library.SelectedIndex);
                Assert.IsTrue(library.IsRunning(library.Selected));
            }
        }

        [Test]
        public void TheLibraryNeedsAHost()
        {
            Assert.Throws<System.ArgumentNullException>(() => new GameLibrary(null));
        }
    }
}
