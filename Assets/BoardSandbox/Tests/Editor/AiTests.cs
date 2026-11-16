using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BoardSandbox.Core.Ai;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class AiTests
    {
        private const int Size = 15;

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

        private static int At(int x, int y)
        {
            return y * Size + x;
        }

        // A game with these stones, as "x,y" pairs counted from 0 and placed in turn, black first.
        private static GameSession Stones(int[] black, int[] white, string options = null)
        {
            GameSession session = TestSupport.StartGomoku(options == null ? null : TestSupport.Options(options));
            for (int i = 0; i < black.Length || i < white.Length; i += 2)
            {
                if (i < black.Length)
                    TestSupport.PlaceOk(session, 1, black[i] + 1, black[i + 1] + 1);
                if (i < white.Length)
                    TestSupport.PlaceOk(session, 2, white[i] + 1, white[i + 1] + 1);
            }
            return session;
        }

        // The point the computer player of the mod chooses for the seat on turn, counted from 0 like At.
        private static int Think(GameSession session, int effort, int seed = 1)
        {
            JsonValue command = TestSupport.Think(session, session.View.Turn, effort, seed);
            Assert.IsNotNull(command, "the computer found no move");
            Assert.AreEqual("place_stone", command["action"].AsString());
            return At(command["x"].AsInt() - 1, command["y"].AsInt() - 1);
        }

        private static string Name(int point)
        {
            return point % Size + "," + point / Size;
        }

        private GameHost NewHost(Action<GameHostOptions> configure = null)
        {
            GameHostOptions options = new GameHostOptions { Clock = () => _time, AiDelaySeconds = 0.5, AiSeed = 17, AiThreaded = false };
            configure?.Invoke(options);
            GameHost host = new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), options);
            Assert.IsTrue(host.SelectMod("gomoku"));
            Assert.IsTrue(host.StartNewGame());
            return host;
        }

        private static JsonValue Place(int x, int y)
        {
            return JsonValue.NewObject().Set("action", "place_stone").Set("x", x).Set("y", y);
        }

        // Lets the computer make the move that is due.
        private bool ComputerMoves(GameHost host)
        {
            host.Update();
            _time += 1;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20000)
            {
                if (host.Update())
                    return true;
                if (!host.AiThinking)
                    return false;
                Thread.Sleep(1);
            }
            return false;
        }

        // A person on seat 1 loses to the computer on seat 2 as fast as the rules allow, on a small board.
        private void PlayAGameAgainstTheComputer(GameHost host)
        {
            host.SetOption("size", JsonValue.Of(7));
            host.SetOption("win_length", JsonValue.Of(3));
            Assert.IsTrue(host.StartNewGame());
            Assert.IsTrue(host.SetSeatMode(2, SeatMode.Ai));
            int[] corners = { 1, 1, 7, 7, 1, 7, 7, 1, 4, 1, 1, 4 };
            for (int i = 0; i < corners.Length && !host.Session.View.IsOver; i += 2)
            {
                if (!host.Submit(1, Place(corners[i], corners[i + 1])).IsAccepted)
                    continue;
                if (!host.Session.View.IsOver)
                    Assert.IsTrue(ComputerMoves(host), "the computer did not move");
            }
            Assert.IsTrue(host.Session.View.IsOver, "the game did not end");
        }

        [Test]
        public void TheManifestNamesThePlayerAndItsLevels()
        {
            ModManifest gomoku = TestSupport.LoadGomoku().Manifest;
            Assert.AreEqual("luau", gomoku.AiEngine, "levels without an engine ask for the mod's own player");
            Assert.AreEqual(3, gomoku.AiLevels.Count);
            Assert.AreEqual(50, gomoku.AiLevels[0]);
            Assert.AreEqual(500, gomoku.AiLevels[1]);
            Assert.AreEqual(5000, gomoku.AiLevels[2]);
            Assert.IsTrue(ComputerPlayers.Has(gomoku.AiEngine));
            Assert.IsTrue(ComputerPlayers.Create(gomoku) is LuauComputerPlayer);

            const string Head = "{\"id\":\"m\",\"name\":\"M\",\"api\":1,\"entry\":\"main.luau\"";
            ModManifest plain = ModManifest.Parse(Head + "}");
            Assert.AreEqual(string.Empty, plain.AiEngine);
            Assert.AreEqual(0, plain.AiLevels.Count);
            Assert.IsNull(ComputerPlayers.Create(plain));
            Assert.AreEqual(string.Empty, ModManifest.Parse(Head + ",\"ai\":{}}").AiEngine);
            Assert.AreEqual("luau", ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"luau\",\"levels\":[7]}}").AiEngine);
            Assert.IsNull(ComputerPlayers.Create(ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"from_the_future\",\"levels\":[1]}}")),
                "an engine this build does not know leaves the mod with random moves");

            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":3}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"luau\"}}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"levels\":[100,0]}}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"levels\":7}}"));
        }

        // Black wins by fours: (8,7) makes a four across, white must take (9,7), and (8,8) then makes two fours.
        private static readonly int[] FoursBlack = { 5, 7, 6, 7, 7, 7, 8, 5, 8, 6, 6, 6, 9, 9 };
        private static readonly int[] FoursWhite = { 4, 7, 8, 4, 5, 5, 0, 0, 14, 0, 0, 14, 14, 14 };

        [Test]
        public void TheScriptFindsAWinByARowOfFours()
        {
            using (GameSession session = Stones(FoursBlack, FoursWhite))
            {
                int move = Think(session, 5000);
                Assert.IsTrue(move == At(8, 7) || move == At(8, 8), "black played " + Name(move));

                // And it plays the row to its end against the best defence.
                for (int stone = 0; stone < 8 && !session.View.IsOver; stone++)
                {
                    int point = Think(session, 5000, stone + 2);
                    TestSupport.PlaceOk(session, session.View.Turn, point % Size + 1, point / Size + 1);
                }
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
            }
        }

        [Test]
        public void TheScriptKeepsTheOpponentFromAWinByFours()
        {
            int[] white = new int[FoursWhite.Length - 2];
            Array.Copy(FoursWhite, white, white.Length);
            // The points that take the row across or the two rows of the second stone away.
            int[] stops = { At(8, 7), At(8, 8), At(9, 7), At(8, 9), At(10, 10) };
            foreach (int effort in new[] { 500, 5000 })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    using (GameSession session = Stones(FoursBlack, white))
                    {
                        int answer = Think(session, effort, seed);
                        Assert.GreaterOrEqual(Array.IndexOf(stops, answer), 0, "effort " + effort + " played " + Name(answer) + " and black wins by fours");
                    }
                }
            }
        }

        [Test]
        public void TheScriptTakesAWinAndStopsOneAtEveryLevel()
        {
            foreach (int effort in new[] { 1, 50, 500, 5000 })
            {
                using (GameSession win = Stones(new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 3, 8, 4, 8, 5, 8, 6, 8 }))
                {
                    int move = Think(win, effort);
                    Assert.IsTrue(move == At(2, 7) || move == At(7, 7), "black should win, not stop white: " + Name(move));
                }
                using (GameSession stop = Stones(new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 2, 7, 4, 8, 5, 8 }))
                    Assert.AreEqual(At(7, 7), Think(stop, effort));
            }
        }

        [Test]
        public void TheScriptAnswersAnOpenThreeAtEveryLevel()
        {
            foreach (int effort in new[] { 50, 500, 5000 })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    using (GameSession three = Stones(new[] { 6, 7, 7, 7, 8, 7 }, new[] { 7, 8, 6, 9 }))
                    {
                        int move = Think(three, effort, seed);
                        int x = move % Size;
                        Assert.AreEqual(7, move / Size, "effort " + effort + " left the three alone: " + Name(move));
                        Assert.IsTrue(x == 4 || x == 5 || x == 9 || x == 10, "effort " + effort + " left the three alone: " + Name(move));
                    }
                }
            }
        }

        [Test]
        public void TheScriptStartsInTheMiddleAndRepeatsWithTheSameSeed()
        {
            using (GameSession empty = TestSupport.StartGomoku())
                Assert.AreEqual(At(7, 7), Think(empty, 50));
            using (GameSession small = TestSupport.StartGomoku(TestSupport.Options("{\"size\":6,\"win_length\":4}")))
            {
                JsonValue command = TestSupport.Think(small, 1, 50, 1);
                Assert.AreEqual(3, command["x"].AsInt());
                Assert.AreEqual(3, command["y"].AsInt());
            }

            using (GameSession session = Stones(new[] { 7, 7, 8, 8 }, new[] { 6, 6 }))
            {
                string before = session.Query("state")["board"].AsString();
                int history = session.HistoryCount;
                int first = Think(session, 500, 5);
                Assert.AreEqual(first, Think(session, 500, 5));
                Assert.AreEqual('0', before[first]);
                Assert.AreEqual(before, session.Query("state")["board"].AsString(), "thinking must not change the game");
                Assert.AreEqual(history, session.HistoryCount);
                Assert.AreEqual(2, session.View.Turn);
            }
        }

        [Test]
        public void TheScriptPlaysTheRulesOfTheGameItIsGiven()
        {
            // Three in a row win on this board, so two in a row are already a row that is one stone short.
            using (GameSession session = Stones(new[] { 1, 1, 2, 1 }, new[] { 1, 3 }, "{\"size\":7,\"win_length\":3}"))
            {
                int move = Think(session, 50);
                Assert.IsTrue(move == At(0, 1) || move == At(3, 1), "white must stop the row: " + Name(move));
            }
            // With exact_win a row of six does not win, so the stone that would make one is not the winning move.
            using (GameSession session = Stones(new[] { 2, 7, 3, 7, 4, 7, 6, 7, 7, 7 }, new[] { 2, 8, 3, 8, 4, 8, 6, 9, 7, 9 }, "{\"exact_win\":true}"))
            {
                int move = Think(session, 500);
                TestSupport.PlaceOk(session, 1, move % Size + 1, move / Size + 1);
                Assert.IsTrue(move != At(5, 7) || !session.View.IsOver);
            }
        }

        [Test]
        public void MoreEffortPlaysBetter()
        {
            double points = 0;
            const int Games = 6;
            using (LuauComputerPlayer player = new LuauComputerPlayer())
            {
                for (int game = 0; game < Games; game++)
                {
                    int strong = game % 2 == 0 ? 1 : 2;
                    Random random = new Random(100 + game);
                    using (GameSession session = TestSupport.StartGomoku())
                    {
                        TestSupport.PlaceOk(session, 1, 8 + random.Next(-1, 2), 8 + random.Next(-1, 2));
                        while (!session.Submit(TestSupport.Place(2, 8 + random.Next(-2, 3), 8 + random.Next(-2, 3))).IsAccepted)
                        {
                        }
                        while (!session.View.IsOver)
                        {
                            int seat = session.View.Turn;
                            JsonValue command = player.Prepare(session, seat, seat == strong ? 5000 : 50, random.Next(1, int.MaxValue))(CancellationToken.None);
                            Assert.IsTrue(session.Submit(new InputPacket(seat, command, "ai")).IsAccepted);
                        }
                        points += session.View.Over.Winner == 0 ? 0.5 : session.View.Over.Winner == strong ? 1 : 0;
                    }
                }
            }
            Assert.GreaterOrEqual(points, Games * 0.66, "effort 5000 scored " + points + " of " + Games + " against 50");
        }

        [Test]
        public void TheThinkingComputerWaitsForItsDelayThenPlaysNextToTheStone()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.HasAi);
                Assert.IsTrue(host.SetSeatMode(2, SeatMode.Ai));
                Assert.AreEqual(SeatMode.Ai, host.GetSeatMode(2));
                List<string> sources = new List<string>();
                host.Session.CommandProcessed += (packet, result) => sources.Add(packet.Source);
                host.Submit(1, Place(8, 8));
                Assert.IsFalse(host.AcceptsHumanInput);

                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);
                _time = 0.4;
                Assert.IsFalse(host.Update());
                _time = 0.6;
                Assert.IsTrue(host.Update());
                Assert.IsFalse(host.AiThinking);
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(1, host.Session.View.Turn);
                Assert.AreEqual("ai", sources[1]);
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);

                string board = host.Session.Query("state")["board"].AsString();
                int stone = board.IndexOf('2');
                Assert.LessOrEqual(Math.Abs(stone % 15 - 7), 2);
                Assert.LessOrEqual(Math.Abs(stone / 15 - 7), 2);
            }
        }

        [Test]
        public void TheThinkingComputerStopsAFour()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(2, SeatMode.Ai);
                // Black builds a row on y = 8 from x = 5; white must take one end after three and the other after four.
                for (int x = 5; x <= 8 && !host.Session.View.IsOver; x++)
                {
                    SubmitResult placed = host.Submit(1, Place(x, 8));
                    if (!placed.IsAccepted)
                        break;
                    Assert.IsTrue(ComputerMoves(host));
                }
                string board = host.Session.Query("state")["board"].AsString();
                string row = board.Substring(7 * 15, 15);
                Assert.IsFalse(host.Session.View.IsOver);
                Assert.IsFalse(row.Contains("011110"), "white let an open four stand: " + row);
                Assert.IsFalse(row.Contains("11111"));
            }
        }

        [Test]
        public void TheComputerThinksHarderWithEveryGame()
        {
            using (GameHost host = NewHost())
            {
                Assert.AreEqual(3, host.AiLevelCount);
                Assert.AreEqual(1, host.AiLevel);
                Assert.AreEqual(50, host.AiEffort);

                PlayAGameAgainstTheComputer(host);
                Assert.AreEqual(1, host.AiProgress.GamesPlayed("gomoku"));
                Assert.AreEqual(2, host.AiLevel);
                Assert.AreEqual(500, host.AiEffort);
                Assert.AreEqual(ToastLevel.Info, host.Toasts.Active(host.Now)[0].Level);

                // Taking the last move back and ending the game again is still the same game.
                if (host.Undo(2))
                {
                    host.SetSeatMode(2, SeatMode.Human);
                    while (!host.Session.View.IsOver && host.Redo())
                    {
                    }
                    Assert.AreEqual(1, host.AiProgress.GamesPlayed("gomoku"));
                }

                PlayAGameAgainstTheComputer(host);
                Assert.AreEqual(3, host.AiLevel);
                Assert.AreEqual(5000, host.AiEffort);

                PlayAGameAgainstTheComputer(host);
                Assert.AreEqual(3, host.AiProgress.GamesPlayed("gomoku"));
                Assert.AreEqual(3, host.AiLevel, "the last level stays");
                Assert.AreEqual(5000, host.AiEffort);

                host.AiProgress.Reset("gomoku");
                Assert.AreEqual(50, host.AiEffort);
            }
        }

        [Test]
        public void GamesWithoutAPersonOrWithoutTheThinkingComputerDoNotCount()
        {
            using (GameHost host = NewHost())
            {
                host.SetOption("size", JsonValue.Of(5));
                host.SetOption("win_length", JsonValue.Of(3));
                Assert.IsTrue(host.StartNewGame());
                host.SetSeatMode(1, SeatMode.Ai);
                host.SetSeatMode(2, SeatMode.Ai);
                for (int step = 0; step < 60 && !host.Session.View.IsOver; step++)
                    ComputerMoves(host);
                Assert.IsTrue(host.Session.View.IsOver);
                Assert.AreEqual(0, host.AiProgress.GamesPlayed("gomoku"));

                Assert.IsTrue(host.StartNewGame());
                host.SetSeatMode(1, SeatMode.Human);
                host.SetSeatMode(2, SeatMode.Random);
                for (int step = 0; step < 60 && !host.Session.View.IsOver; step++)
                {
                    if (host.Session.View.Turn == 1)
                        host.Submit(1, host.Session.RandomCommand(1, step + 1));
                    else
                        ComputerMoves(host);
                }
                Assert.IsTrue(host.Session.View.IsOver);
                Assert.AreEqual(0, host.AiProgress.GamesPlayed("gomoku"));
                Assert.AreEqual(50, host.AiEffort);
            }
        }

        [Test]
        public void TheGamesPlayedAreKeptInAFile()
        {
            string directory = TestSupport.TempDirectory();
            _temporary.Add(directory);
            string path = Path.Combine(directory, "nested", "ai_progress.json");

            AiProgress progress = new AiProgress(path);
            Assert.IsTrue(progress.Load(), "no file yet is not a problem");
            using (GameHost host = NewHost(o => o.AiProgress = progress))
            {
                Assert.AreSame(progress, host.AiProgress);
                PlayAGameAgainstTheComputer(host);
                PlayAGameAgainstTheComputer(host);
            }
            Assert.IsTrue(File.Exists(path));

            AiProgress loaded = new AiProgress(path);
            Assert.IsTrue(loaded.Load());
            Assert.AreEqual(2, loaded.GamesPlayed("gomoku"));
            Assert.AreEqual(0, loaded.GamesPlayed("chess"));
            Assert.AreEqual(2, loaded.LevelIndex("gomoku", 3));
            Assert.AreEqual(1, loaded.LevelIndex("gomoku", 2));
            using (GameHost host = NewHost(o => o.AiProgress = loaded))
                Assert.AreEqual(5000, host.AiEffort);

            File.WriteAllText(path, "{ not json");
            Assert.IsFalse(loaded.Load());
            Assert.Greater(loaded.Problem.Length, 0);
            Assert.AreEqual(0, loaded.GamesPlayed("gomoku"));
        }

        [Test]
        public void ModsWithoutLevelsRefuseTheThinkingComputer()
        {
            const string Script = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end " +
                                  "function rules.random_command() return { action = 'x' } end return rules";
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(Script)));
                Assert.IsTrue(host.StartNewGame());
                Assert.IsFalse(host.HasAi);
                Assert.AreEqual(0, host.AiLevel);
                Assert.AreEqual(0, host.AiEffort);
                Assert.IsFalse(host.SetSeatMode(1, SeatMode.Ai));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(ToastLevel.Warning, host.Toasts.Active(host.Now)[0].Level);
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Random));
            }
        }

        [Test]
        public void UndoDuringTheSearchStartsItAgainForTheNewPosition()
        {
            using (GameHost host = NewHost())
            {
                host.Submit(1, Place(8, 8));
                host.Submit(2, Place(9, 9));
                host.Submit(1, Place(3, 3));
                host.SetSeatMode(2, SeatMode.Ai);
                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);

                Assert.IsTrue(host.Undo(2));
                Assert.IsFalse(host.AiThinking, "the search was for a position that is gone");
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }
        }

        [Test]
        public void AChangeBehindTheHostsBackDropsTheSearch()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(2, SeatMode.Ai);
                host.Submit(1, Place(8, 8));
                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);

                // The console talks to the session directly.
                Assert.IsTrue(host.Session.Submit(TestSupport.Place(2, 9, 9)).IsAccepted);
                Assert.IsFalse(host.AiThinking);
                _time = 5;
                Assert.IsFalse(host.Update(), "it is the person's turn");
                Assert.AreEqual(2, host.Session.View.ObjectCount);
            }
        }

        [Test]
        public void TheSearchRunsOnAWorkerThreadByDefault()
        {
            using (GameHost host = NewHost(o => o.AiThreaded = true))
            {
                host.SetSeatMode(1, SeatMode.Ai);
                host.SetSeatMode(2, SeatMode.Ai);
                for (int move = 1; move <= 6; move++)
                {
                    Assert.IsTrue(ComputerMoves(host), "move " + move + " did not come");
                    Assert.AreEqual(move, host.Session.View.ObjectCount);
                }
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);

                // Leaving in the middle of a search must not hang or throw.
                host.Update();
                Assert.IsTrue(host.AiThinking);
            }
        }

        [Test]
        public void PausingAndNewGamesDropTheSearch()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(1, SeatMode.Ai);
                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);
                host.AiPaused = true;
                _time = 5;
                Assert.IsFalse(host.Update());
                Assert.IsFalse(host.AiThinking);
                Assert.AreEqual(0, host.Session.View.ObjectCount);

                host.AiPaused = false;
                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);
                Assert.IsTrue(host.StartNewGame());
                Assert.IsFalse(host.AiThinking);
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(1, host.Session.View.ObjectCount);
            }
        }

        // A game of one seat that counts the stones laid; whoever thinks for it is told how in `think`.
        private static ModPackage Thinker(string think, string levels = "[3, 9]", bool snapshots = true)
        {
            string script =
                "local rules = {} local count = 0 local calls = 0 local asked = nil\n" +
                "function rules.new_game() count = 0 bse.turn(1) end\n" +
                "function rules.command(seat, cmd) if cmd.action ~= 'lay' then return bse.reject('UNKNOWN_ACTION', 'no') end count += 1 " +
                "if count >= 5 then bse.over({ winner = 1 }) end end\n" +
                "function rules.random_command() return { action = 'lay' } end\n" +
                "function rules.query(name, args)\n" +
                "  if name == 'calls' then return calls end\n" +
                "  if name == 'count' then return count end\n" +
                "  if name == 'think' then calls += 1 asked = args " + think + " end\n" +
                "  return nil\n" +
                "end\n" +
                (snapshots ? "function rules.snapshot() return { count = count } end function rules.restore(data) count = data.count end\n" : "") +
                "return rules";
            string manifest = "{\"id\":\"thinker\",\"name\":\"Thinker\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":1},\"ai\":{\"levels\":" +
                              levels + "}}";
            return new ModPackage(manifest, new Dictionary<string, string> { { "main.luau", script } });
        }

        private GameHost ThinkerHost(ModPackage mod, Action<GameHostOptions> configure = null)
        {
            GameHostOptions options = new GameHostOptions { Clock = () => _time, AiDelaySeconds = 0.5, AiSeed = 17, AiThreaded = false };
            configure?.Invoke(options);
            GameHost host = new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), options);
            Assert.IsTrue(host.SelectMod(mod));
            Assert.IsTrue(host.StartNewGame());
            return host;
        }

        [Test]
        public void TheModsOwnPlayerIsAskedForTheMoveWithTheSeatTheEffortAndASeed()
        {
            ModPackage mod = Thinker("return { action = 'lay', seat = args.seat, effort = args.effort, seed = args.seed, calls = calls, count = count }");
            using (GameHost host = ThinkerHost(mod))
            {
                Assert.IsTrue(host.HasAi);
                Assert.AreEqual(2, host.AiLevelCount);
                Assert.AreEqual(3, host.AiEffort);
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                List<InputPacket> packets = new List<InputPacket>();
                host.Session.CommandProcessed += (packet, result) => packets.Add(packet);

                Assert.IsTrue(ComputerMoves(host));
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(2, packets.Count);
                Assert.AreEqual("ai", packets[0].Source);
                Assert.AreEqual(1, packets[0].Command["seat"].AsInt());
                Assert.AreEqual(3, packets[0].Command["effort"].AsInt());
                Assert.Greater(packets[0].Command["seed"].AsInt(), 0);
                Assert.AreNotEqual(packets[0].Command["seed"].AsInt(), packets[1].Command["seed"].AsInt());
                Assert.AreEqual(0, packets[0].Command["count"].AsInt(), "the script thinks about the position of the game");
                Assert.AreEqual(1, packets[1].Command["count"].AsInt());

                // The script thinks in a VM of its own, which is kept from one move to the next.
                Assert.AreEqual(1, packets[0].Command["calls"].AsInt());
                Assert.AreEqual(2, packets[1].Command["calls"].AsInt());
                Assert.AreEqual(0, host.Session.Query("calls").AsInt(), "the VM of the game was not asked");
                Assert.AreEqual(2, host.Session.Query("count").AsInt());
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }
        }

        [Test]
        public void TheScriptMayThinkFarLongerThanACommand()
        {
            ModPackage mod = Thinker("local n = 0 for i = 1, args.effort * 100 do n += i % 7 end return { action = 'lay', n = n }", "[300000]");
            using (GameHost host = ThinkerHost(mod, o => o.Session.Vm = new VmOptions { CallTimeoutMs = 20 }))
            {
                Stopwatch watch = Stopwatch.StartNew();
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsTrue(ComputerMoves(host), "the limit of a command must not apply to thinking");
                Assert.Greater(watch.ElapsedMilliseconds, 20);
                Assert.AreEqual(1, host.Session.Query("count").AsInt());
            }
        }

        [Test]
        public void AScriptThatThinksOnAWorkerThreadLeavesTheGameResponsive()
        {
            ModPackage mod = Thinker("local n = 0 for i = 1, 20000000 do n += i % 7 end return { action = 'lay' }");
            using (GameHost host = ThinkerHost(mod, o => o.AiThreaded = true))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsFalse(host.Update());
                Assert.IsTrue(host.AiThinking);
                _time = 5;
                // The game answers while the script is busy, and what a person does drops the move being thought about.
                Assert.AreEqual(0, host.Session.Query("count").AsInt());
                Assert.IsTrue(host.Submit(1, JsonValue.NewObject().Set("action", "lay")).IsAccepted);
                Assert.IsFalse(host.AiThinking);
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(2, host.Session.Query("count").AsInt());
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }
        }

        [Test]
        public void AScriptWithoutAMoveOrWithAnErrorHandsTheSeatBack()
        {
            using (GameHost host = ThinkerHost(Thinker("return nil")))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsFalse(ComputerMoves(host));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(ToastLevel.Warning, host.Toasts.Active(host.Now)[0].Level);
                Assert.AreEqual(0, host.Session.Query("count").AsInt());
            }
            using (GameHost host = ThinkerHost(Thinker("error('lost the thread')")))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsFalse(ComputerMoves(host));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                IReadOnlyList<Toast> toasts = host.Toasts.Active(host.Now);
                Assert.AreEqual(ToastLevel.Error, toasts[0].Level);
                StringAssert.Contains("lost the thread", toasts[0].Text);
                // The game itself is untouched and can go on.
                Assert.IsTrue(host.Submit(1, JsonValue.NewObject().Set("action", "lay")).IsAccepted);
            }
            using (GameHost host = ThinkerHost(Thinker("return { action = 'fly' }")))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsFalse(ComputerMoves(host), "the rules refuse what the player of the same mod asked for");
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
            }
        }

        [Test]
        public void TheLuauPlayerNeedsSnapshotsAndStopsAScriptThatNeverAnswers()
        {
            using (GameHost host = ThinkerHost(Thinker("return { action = 'lay' }", "[1]", false)))
            {
                Assert.IsTrue(host.HasAi);
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsFalse(ComputerMoves(host));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                StringAssert.Contains("NOT_SUPPORTED", host.Toasts.Active(host.Now)[0].Text);
            }

            using (LuauComputerPlayer player = new LuauComputerPlayer { Vm = new VmOptions { CallTimeoutMs = 50 } })
            using (GameSession session = new GameSession(Thinker("while true do end"), new SessionOptions()))
            {
                session.NewGame(null);
                LuauException stopped = Assert.Throws<LuauException>(() => player.Prepare(session, 1, 1, 1)(CancellationToken.None));
                Assert.AreEqual(VmStatus.Timeout, stopped.Status);
                Assert.AreEqual(0, session.Query("count").AsInt());
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "lay")).IsAccepted, "the game goes on");
            }

            using (LuauComputerPlayer player = new LuauComputerPlayer())
            using (GameSession session = new GameSession(Thinker("return { action = 'lay' }"), new SessionOptions()))
            {
                session.NewGame(null);
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    Func<CancellationToken, JsonValue> search = player.Prepare(session, 1, 1, 1);
                    cancel.Cancel();
                    Assert.Throws<OperationCanceledException>(() => search(cancel.Token));
                }
                for (int i = 0; i < 5; i++)
                    Assert.IsTrue(session.Submit(InputPacket.Action(1, "lay")).IsAccepted);
                Assert.AreEqual("GAME_OVER", Assert.Throws<GameSessionException>(() => player.Prepare(session, 1, 1, 1)).Code);
            }
        }

        private sealed class FixedPlayer : IComputerPlayer
        {
            public string Engine => "test_fixed";

            public Func<CancellationToken, JsonValue> Prepare(GameSession session, int seat, int effort, int seed)
            {
                return cancel => JsonValue.NewObject().Set("action", "lay").Set("effort", effort);
            }
        }

        [Test]
        public void AHostCanStillRegisterAnEngineOfItsOwn()
        {
            ComputerPlayers.Register("test_fixed", () => new FixedPlayer());
            ModPackage mod = new ModPackage(
                "{\"id\":\"fixed\",\"name\":\"Fixed\",\"api\":1,\"entry\":\"main.luau\",\"players\":{\"min\":1,\"max\":1},\"ai\":{\"engine\":\"test_fixed\",\"levels\":[4]}}",
                new Dictionary<string, string> { { "main.luau", "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules" } });
            using (GameHost host = ThinkerHost(mod))
            {
                Assert.IsTrue(host.HasAi);
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                InputPacket seen = null;
                host.Session.CommandProcessed += (packet, result) => seen = packet;
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(4, seen.Command["effort"].AsInt());
            }
        }
    }
}
