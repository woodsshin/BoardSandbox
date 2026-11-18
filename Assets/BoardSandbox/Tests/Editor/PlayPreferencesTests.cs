using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Json;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // How the player's last games are set up again for the next ones: who plays which seat, and how a room was opened.
    [TestFixture]
    public class PlayPreferencesTests
    {
        private readonly List<string> _temporary = new List<string>();
        private double _time;

        [SetUp]
        public void ResetClock()
        {
            _time = 0;
        }

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

        // A host whose computer player thinks inside Update, on a clock the test moves.
        private GameHost NewHost(PlayPreferences preferences)
        {
            GameHostOptions options = new GameHostOptions
            {
                Clock = () => _time,
                AiDelaySeconds = 0.5,
                AiSeed = 17,
                AiThreaded = false,
                Preferences = preferences
            };
            return new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), options);
        }

        private static JsonValue Place(int x, int y)
        {
            return JsonValue.NewObject().Set("action", "place_stone").Set("x", x).Set("y", y);
        }

        // ---- What is kept -------------------------------------------------------------------------------------------------

        [Test]
        public void NothingIsKeptForAGameNobodyPlayed()
        {
            PlayPreferences preferences = new PlayPreferences();
            Assert.IsFalse(preferences.TryGetRoom("chess", out bool computer, out int players));
            Assert.IsFalse(computer);
            Assert.AreEqual(0, players);
            Assert.IsFalse(preferences.TryGetRoom(null, out _, out _));
            Assert.IsTrue(preferences.ComputerPlays("chess", 2, true), "where nobody chose, the fallback says");
            Assert.IsFalse(preferences.ComputerPlays("chess", 2, false));
            Assert.IsTrue(preferences.ComputerPlays(null, 3, true));
            Assert.AreEqual(string.Empty, preferences.Problem);
        }

        [Test]
        public void TheRoomOfAGameIsKeptWithItsNumberOfPlayers()
        {
            PlayPreferences preferences = new PlayPreferences();
            preferences.RememberRoom("gostop", false, 2);
            Assert.IsTrue(preferences.TryGetRoom("gostop", out bool computer, out int players));
            Assert.IsFalse(computer);
            Assert.AreEqual(2, players);

            preferences.RememberRoom("gostop", true, 3);
            Assert.IsTrue(preferences.TryGetRoom("gostop", out computer, out players));
            Assert.IsTrue(computer, "the latest room is the one that counts");
            Assert.AreEqual(3, players);

            preferences.RememberRoom("chess", true, -5);
            Assert.IsTrue(preferences.TryGetRoom("chess", out computer, out players));
            Assert.IsTrue(computer);
            Assert.AreEqual(0, players, "a game that does not ask has no number of players");

            Assert.IsFalse(preferences.TryGetRoom("gomoku", out _, out _), "every game has its own room");
            preferences.RememberRoom(null, true, 2);
            preferences.RememberRoom(string.Empty, true, 2);
            Assert.IsFalse(preferences.TryGetRoom(string.Empty, out _, out _));
        }

        [Test]
        public void TheSeatsOfAGameAreKeptButNeverTheFirst()
        {
            PlayPreferences preferences = new PlayPreferences();
            // The person plays seat 1, the computer seat 2 and the person seat 3 as well.
            preferences.RememberSeats("gostop", new[] { true, true, false });
            Assert.IsFalse(preferences.ComputerPlays("gostop", 1, true), "seat 1 is the person's whatever was kept");
            Assert.IsFalse(preferences.ComputerPlays("gostop", 0, true));
            Assert.IsTrue(preferences.ComputerPlays("gostop", 2, false));
            Assert.IsFalse(preferences.ComputerPlays("gostop", 3, true));
            Assert.IsTrue(preferences.ComputerPlays("gostop", 4, true), "a seat nobody chose gets the fallback");
            Assert.IsFalse(preferences.ComputerPlays("gostop", 4, false));
            Assert.IsTrue(preferences.ComputerPlays("chess", 3, true), "every game has its own seats");

            preferences.RememberSeats("gostop", new[] { false, false, true });
            Assert.IsFalse(preferences.ComputerPlays("gostop", 2, true));
            Assert.IsTrue(preferences.ComputerPlays("gostop", 3, false));

            // One seat has no other seat to choose.
            preferences.RememberSeats("cubic3", new[] { false });
            Assert.IsTrue(preferences.ComputerPlays("cubic3", 2, true));
            preferences.RememberSeats(null, new[] { false, false });
            preferences.RememberSeats("chess", null);
            Assert.IsTrue(preferences.ComputerPlays("chess", 2, true));
        }

        [Test]
        public void TheChoicesAreKeptInAFileAndComeBack()
        {
            string path = Path.Combine(NewDirectory(), "nested", "preferences.json");
            PlayPreferences preferences = new PlayPreferences(path);
            Assert.AreEqual(path, preferences.Path);
            Assert.IsTrue(preferences.Load(), "no file yet is not a problem");
            Assert.IsFalse(File.Exists(path));

            preferences.RememberRoom("gostop", false, 2);
            preferences.RememberSeats("gostop", new[] { false, false, true });
            preferences.RememberSeats("chess", new[] { false, false });
            preferences.RememberRoom("chess", true, 0);
            Assert.AreEqual(string.Empty, preferences.Problem);
            Assert.IsTrue(File.Exists(path));

            PlayPreferences loaded = new PlayPreferences(path);
            Assert.IsTrue(loaded.Load());
            Assert.IsTrue(loaded.TryGetRoom("gostop", out bool computer, out int players));
            Assert.IsFalse(computer);
            Assert.AreEqual(2, players);
            Assert.IsFalse(loaded.ComputerPlays("gostop", 2, true));
            Assert.IsTrue(loaded.ComputerPlays("gostop", 3, false));
            Assert.IsFalse(loaded.ComputerPlays("chess", 2, true));
            Assert.IsTrue(loaded.TryGetRoom("chess", out computer, out players));
            Assert.IsTrue(computer);
            Assert.AreEqual(0, players);
            Assert.IsFalse(loaded.TryGetRoom("gomoku", out _, out _));

            // The same choices again change nothing, and the games are listed in the same order whichever was chosen first.
            string text = File.ReadAllText(path);
            loaded.RememberRoom("gostop", false, 2);
            loaded.RememberSeats("chess", new[] { false, false });
            Assert.AreEqual(text, File.ReadAllText(path));
            Assert.IsTrue(text.IndexOf("\"chess\"", StringComparison.Ordinal) < text.IndexOf("\"gostop\"", StringComparison.Ordinal));

            File.WriteAllText(path, "{ not json");
            Assert.IsFalse(loaded.Load());
            StringAssert.Contains("could not be read", loaded.Problem);
            Assert.IsFalse(loaded.TryGetRoom("gostop", out _, out _), "what was read before is gone");
            Assert.IsTrue(loaded.ComputerPlays("chess", 2, true));
        }

        [Test]
        public void AFileThatIsNotWhatItShouldBeIsReadAsFarAsItCanBe()
        {
            string path = Path.Combine(NewDirectory(), "preferences.json");
            string many = string.Join(",", new string[20]).Replace(",", "true,") + "true";
            File.WriteAllText(path,
                "{\"games\":{" +
                "\"chess\":{\"room\":{\"computer\":\"yes\",\"players\":2},\"seats\":[true,\"x\",false]}," +
                "\"gostop\":{\"room\":{\"computer\":false,\"players\":-4},\"seats\":[true,false]}," +
                "\"gomoku\":{\"room\":[],\"seats\":{}}," +
                "\"sudoku\":5," +
                "\"many\":{\"seats\":[" + many + "]}}}");
            PlayPreferences preferences = new PlayPreferences(path);
            Assert.IsTrue(preferences.Load());
            Assert.AreEqual(string.Empty, preferences.Problem);

            Assert.IsFalse(preferences.TryGetRoom("chess", out _, out _), "a room whose answer is not yes or no was not chosen");
            Assert.IsFalse(preferences.ComputerPlays("chess", 2, false), "nor was a list with something else in it");
            Assert.IsTrue(preferences.ComputerPlays("chess", 2, true));

            Assert.IsTrue(preferences.TryGetRoom("gostop", out bool computer, out int players));
            Assert.IsFalse(computer);
            Assert.AreEqual(0, players, "less than none is none");
            Assert.IsFalse(preferences.ComputerPlays("gostop", 3, true));

            Assert.IsFalse(preferences.TryGetRoom("gomoku", out _, out _));
            Assert.IsTrue(preferences.ComputerPlays("gomoku", 2, true));
            Assert.IsFalse(preferences.TryGetRoom("sudoku", out _, out _));

            Assert.IsTrue(preferences.ComputerPlays("many", 16, false), "the seats of a game are at most sixteen");
            Assert.IsFalse(preferences.ComputerPlays("many", 17, false));

            File.WriteAllText(path, "[1, 2]");
            Assert.IsTrue(preferences.Load(), "a file of another shape holds nothing, which is not a problem");
            Assert.IsTrue(preferences.ComputerPlays("many", 2, true));
        }

        [Test]
        public void AFileThatCannotBeWrittenIsAProblemAndTheChoicesStayInMemory()
        {
            string directory = NewDirectory();
            string blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "this is a file, not a folder");
            PlayPreferences preferences = new PlayPreferences(Path.Combine(blocker, "preferences.json"));

            preferences.RememberRoom("chess", false, 0);
            StringAssert.Contains("could not be saved", preferences.Problem);
            Assert.IsTrue(preferences.TryGetRoom("chess", out bool computer, out _));
            Assert.IsFalse(computer, "the choice holds for as long as the game runs");

            preferences.RememberSeats("chess", new[] { false, false });
            StringAssert.Contains("could not be saved", preferences.Problem);
            Assert.IsFalse(preferences.ComputerPlays("chess", 2, true));

            // The same choice again writes nothing, so there is nothing to complain about.
            preferences.RememberSeats("chess", new[] { false, false });
            Assert.AreEqual(string.Empty, preferences.Problem);
        }

        // ---- The seats of a game ------------------------------------------------------------------------------------------

        [Test]
        public void WithoutPreferencesEverySeatIsAPersons()
        {
            using (GameHost host = NewHost(null))
            {
                Assert.IsNull(host.Preferences);
                foreach (string id in new[] { "chess", "gomoku", "gostop" })
                {
                    Assert.IsTrue(host.SelectMod(id), id);
                    for (int seat = 1; seat <= 3; seat++)
                        Assert.AreEqual(SeatMode.Human, host.GetSeatMode(seat), id + " seat " + seat);
                }
                Assert.IsTrue(host.SetSeatMode(2, SeatMode.Ai));
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Human), "there is nothing to keep it in, and no harm in that");
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
            }
        }

        [Test]
        public void ThePersonPlaysSeatOneAndTheComputerTheOtherSeatsOfAGameItCanPlayAgainst()
        {
            PlayPreferences preferences = new PlayPreferences();
            using (GameHost host = NewHost(preferences))
            {
                Assert.AreSame(preferences, host.Preferences);
                foreach (string id in new[] { "chess", "gomoku" })
                {
                    Assert.IsTrue(host.SelectMod(id), id);
                    Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1), id);
                    Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2), id);
                }

                // Go-Stop: the other two seats.
                Assert.IsTrue(host.SelectMod("gostop"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));
                Assert.AreEqual(3, host.SeatCount);
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }
        }

        [Test]
        public void TheComputerPlaysTheSeatItWasGivenWithoutAnyoneSwitchingIt()
        {
            using (GameHost host = NewHost(new PlayPreferences()))
            {
                Assert.IsTrue(host.StartGame(host.Catalog.Find("gomoku")));
                Assert.IsTrue(host.Submit(1, Place(8, 8)).IsAccepted);
                host.Update();
                _time += 1;
                Assert.IsTrue(host.Update(), "the computer did not answer");
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(1, host.Session.View.Turn);
            }
        }

        [Test]
        public void AGameThatCannotBePlayedAgainstTheComputerStaysWithPeople()
        {
            using (GameHost host = NewHost(new PlayPreferences()))
            {
                // A puzzle that hands out hints, and the cube that nobody plays against.
                foreach (string id in new[] { "sudoku", "cubic3" })
                {
                    Assert.IsTrue(host.SelectMod(id), id);
                    Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1), id);
                    Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2), id);
                }

                // A game whose computer only plays random moves is not played against it unless the player says so.
                const string Script = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end " +
                                      "function rules.random_command() return { action = 'x' } end return rules";
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(Script)));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Random));
                Assert.AreEqual(SeatMode.Random, host.GetSeatMode(2));
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(Script)));
                Assert.AreEqual(SeatMode.Random, host.GetSeatMode(2), "but it is then, the next time");
            }
        }

        [Test]
        public void TheSeatsTheyChoseComeBackWithTheNextGameAndAfterARestart()
        {
            string path = Path.Combine(NewDirectory(), "preferences.json");
            PlayPreferences preferences = new PlayPreferences(path);
            using (GameHost host = NewHost(preferences))
            {
                // Two people at one screen.
                Assert.IsTrue(host.StartGame(host.Catalog.Find("chess")));
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Human));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));

                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2), "every game has its own seats");

                Assert.IsTrue(host.SelectMod("gostop"));
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Human));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));

                Assert.IsTrue(host.SelectMod("chess"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2), "the next game starts the way the last was left");
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2), "and so does a new game on the table");
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }

            PlayPreferences loaded = new PlayPreferences(path);
            Assert.IsTrue(loaded.Load());
            using (GameHost host = NewHost(loaded))
            {
                Assert.IsTrue(host.SelectMod("chess"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));

                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));

                Assert.IsTrue(host.SelectMod("gostop"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));

                // Giving the seat back to the computer is kept as well.
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Ai));
            }
            PlayPreferences again = new PlayPreferences(path);
            Assert.IsTrue(again.Load());
            using (GameHost host = NewHost(again))
            {
                Assert.IsTrue(host.SelectMod("gostop"));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(3));
            }
        }

        [Test]
        public void OnlyWhatThePlayerChoseIsKept()
        {
            PlayPreferences preferences = new PlayPreferences();
            using (GameHost host = NewHost(preferences))
            {
                Assert.IsTrue(host.SelectMod("chess"));

                // A room hands the seats around by itself, and the host's own settings are not what it leaves behind.
                Assert.IsTrue(host.SetSeatMode(2, SeatMode.Human));
                Assert.IsTrue(host.SelectMod("chess"));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));

                // Seat 1 may be played by the computer for a game, but the next game gives it back to the person.
                Assert.IsTrue(host.ChooseSeatMode(1, SeatMode.Ai));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(1));
                Assert.IsTrue(host.SelectMod("chess"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
            }
        }

        [Test]
        public void AChoiceThatIsRefusedIsNotKept()
        {
            PlayPreferences preferences = new PlayPreferences();
            using (GameHost host = NewHost(preferences))
            {
                Assert.IsTrue(host.SelectMod("sudoku"));
                Assert.IsFalse(host.ChooseSeatMode(2, SeatMode.Ai), "sudoku is not played against the computer");
                Assert.IsFalse(host.ChooseSeatMode(3, SeatMode.Human), "there is no third seat");
                Assert.IsTrue(preferences.ComputerPlays("sudoku", 2, true), "nothing was kept, the fallback answers");
                Assert.AreEqual(ToastLevel.Warning, host.Toasts.Active(host.Now)[0].Level);
            }
        }

        [Test]
        public void AProblemWithTheFileIsToastedAndTheGameGoesOn()
        {
            string blocker = Path.Combine(NewDirectory(), "blocker");
            File.WriteAllText(blocker, "this is a file, not a folder");
            PlayPreferences preferences = new PlayPreferences(Path.Combine(blocker, "preferences.json"));
            using (GameHost host = NewHost(preferences))
            {
                Assert.IsTrue(host.SelectMod("chess"));
                Assert.IsTrue(host.ChooseSeatMode(2, SeatMode.Human));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
                IReadOnlyList<Toast> toasts = host.Toasts.Active(host.Now);
                Assert.AreEqual(1, toasts.Count);
                Assert.AreEqual(ToastLevel.Warning, toasts[0].Level);
                StringAssert.Contains("could not be saved", toasts[0].Text);

                Assert.IsTrue(host.SelectMod("chess"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2), "it is kept for as long as the game runs");
            }
        }
    }
}
