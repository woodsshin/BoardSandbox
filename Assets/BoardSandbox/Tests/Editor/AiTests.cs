using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BoardSandbox.Core.Ai;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
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

        // Stones as "x,y" pairs, counted from 0.
        private static GomokuPosition Position(int current, int[] black, int[] white, int winLength = 5, bool exactWin = false)
        {
            char[] board = new string('0', Size * Size).ToCharArray();
            for (int i = 0; i < black.Length; i += 2)
                board[At(black[i], black[i + 1])] = '1';
            for (int i = 0; i < white.Length; i += 2)
                board[At(white[i], white[i + 1])] = '2';
            return GomokuPosition.Parse(Size, winLength, exactWin, new string(board), current);
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
        public void ZobristKeysAreFixedAndDistinct()
        {
            ZobristKeys first = new ZobristKeys(225);
            ZobristKeys second = new ZobristKeys(225);
            HashSet<ulong> seen = new HashSet<ulong> { first.SideToMove };
            for (int cell = 0; cell < 225; cell++)
            {
                for (int seat = 1; seat <= 2; seat++)
                {
                    Assert.AreEqual(first.Stone(cell, seat), second.Stone(cell, seat));
                    Assert.IsTrue(seen.Add(first.Stone(cell, seat)), "two keys are equal");
                }
            }
            Assert.AreSame(ZobristKeys.For(225), ZobristKeys.For(225));
            Assert.AreNotEqual(ZobristKeys.For(225).Stone(0, 1), ZobristKeys.For(81).Stone(0, 1));
        }

        [Test]
        public void TheSamePositionHashesTheSameWhateverTheOrderOfMoves()
        {
            GomokuPosition first = new GomokuPosition(Size, 5, false);
            GomokuPosition second = new GomokuPosition(Size, 5, false);
            ulong empty = first.Hash;
            foreach (int move in new[] { At(7, 7), At(8, 8), At(6, 7), At(3, 3) })
                first.Play(move);
            foreach (int move in new[] { At(6, 7), At(3, 3), At(7, 7), At(8, 8) })
                second.Play(move);
            Assert.AreEqual(first.Hash, second.Hash);
            Assert.AreNotEqual(empty, first.Hash);

            GomokuPosition parsed = Position(1, new[] { 7, 7, 6, 7 }, new[] { 8, 8, 3, 3 });
            Assert.AreEqual(first.Hash, parsed.Hash);
            Assert.AreNotEqual(first.Hash, Position(2, new[] { 7, 7, 6, 7 }, new[] { 8, 8, 3, 3 }).Hash, "the side to move is part of the hash");
            Assert.AreNotEqual(first.Hash, Position(1, new[] { 7, 7, 6, 7 }, new[] { 8, 8, 3, 4 }).Hash);

            GomokuPosition copy = new GomokuPosition(Size, 5, false);
            copy.CopyFrom(first);
            Assert.AreEqual(first.Hash, copy.Hash);
            Assert.AreEqual(first.CandidateCount, copy.CandidateCount);
            copy.Play(At(0, 0));
            Assert.AreNotEqual(first.Hash, copy.Hash);
            Assert.AreEqual(0, first.Cell(At(0, 0)));
        }

        [Test]
        public void PositionsKnowWinsAndTheMovesNearStones()
        {
            GomokuPosition position = new GomokuPosition(Size, 5, false);
            Assert.AreEqual(0, position.CandidateCount);
            position.Play(At(0, 0));
            Assert.AreEqual(8, position.CandidateCount, "a corner has eight points within two steps");
            Assert.AreEqual(2, position.Current);

            GomokuPosition four = Position(1, new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 3, 8, 4, 8, 5, 8 });
            Assert.IsTrue(four.Wins(At(7, 7), 1));
            Assert.IsTrue(four.Wins(At(2, 7), 1));
            Assert.IsFalse(four.Wins(At(7, 7), 2));
            Assert.IsFalse(four.Wins(At(6, 8), 2));
            Assert.IsTrue(four.Play(At(7, 7)));
            Assert.AreEqual(1, four.Winner);

            GomokuPosition six = Position(1, new[] { 2, 7, 3, 7, 4, 7, 6, 7, 7, 7 }, new int[0], 5, true);
            Assert.IsFalse(six.Wins(At(5, 7), 1), "six in a row do not win when exactly five are needed");
            Assert.IsTrue(Position(1, new[] { 2, 7, 3, 7, 4, 7, 6, 7, 7, 7 }, new int[0]).Wins(At(5, 7), 1));

            Assert.Throws<FormatException>(() => GomokuPosition.Parse(Size, 5, false, "012", 1));
            Assert.Throws<FormatException>(() => GomokuPosition.Parse(3, 3, false, "000000003", 1));
            Assert.Throws<FormatException>(() => GomokuPosition.Parse(3, 3, false, "000000000", 3));
        }

        [Test]
        public void PositionsKnowTheShapesAStoneWouldMake()
        {
            const int Across = 0;
            const int Down = 1;

            // . X X . X . : the gap makes an open four, as does either end of an unbroken three.
            GomokuPosition broken = Position(1, new[] { 5, 7, 6, 7, 8, 7 }, new[] { 0, 0, 14, 0, 0, 14 });
            Assert.AreEqual(GomokuShape.OpenFour, broken.Shape(At(7, 7), 1, Across));
            Assert.AreEqual(GomokuShape.Four, broken.Shape(At(9, 7), 1, Across), "X X . X X has one point left");
            Assert.AreEqual(GomokuShape.None, broken.Shape(At(7, 7), 1, Down));
            Assert.AreEqual(GomokuThreat.Winning, broken.Rate(At(7, 7), 1, out int value));
            Assert.Greater(value, 1000);
            Assert.AreEqual(GomokuThreat.None, broken.Rate(At(7, 7), 2, out value), "white only stops black there");
            Assert.AreEqual(GomokuThreat.None, broken.Rate(At(12, 12), 1, out value));
            Assert.AreEqual(0, value);

            // A three closed on one side becomes a four the opponent can stop.
            GomokuPosition closed = Position(1, new[] { 5, 7, 6, 7, 7, 7 }, new[] { 4, 7, 0, 0, 14, 0 });
            Assert.AreEqual(GomokuShape.Four, closed.Shape(At(8, 7), 1, Across));
            Assert.AreEqual(GomokuThreat.Four, closed.Rate(At(8, 7), 1, out value));

            // Two stones with a gap, and two in a row: a third makes an open three.
            GomokuPosition two = Position(1, new[] { 6, 7, 8, 7 }, new[] { 0, 0, 14, 0 });
            Assert.AreEqual(GomokuShape.OpenThree, two.Shape(At(7, 7), 1, Across));
            Assert.AreEqual(GomokuShape.OpenThree, two.Shape(At(9, 7), 1, Across), "X . X X is an open three too");
            Assert.AreEqual(GomokuShape.Two, Position(2, new[] { 7, 7 }, new int[0]).Shape(At(8, 7), 1, Across));
            Assert.AreEqual(GomokuShape.Five, Position(1, new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 0, 0, 1, 0, 2, 0, 4, 0 }).Shape(At(7, 7), 1, Across));

            // A four and an open three through one point, and two open threes.
            GomokuPosition fourThree = Position(1, new[] { 5, 7, 6, 7, 7, 7, 8, 5, 8, 6 }, new[] { 4, 7, 0, 0, 14, 0, 0, 14, 14, 14 });
            Assert.AreEqual(GomokuShape.OpenThree, fourThree.Shape(At(8, 7), 1, Down));
            Assert.AreEqual(GomokuThreat.Winning, fourThree.Rate(At(8, 7), 1, out value));
            GomokuPosition threeThree = Position(1, new[] { 6, 7, 7, 7, 8, 5, 8, 6 }, new[] { 0, 0, 14, 0, 0, 14, 14, 14 });
            Assert.AreEqual(GomokuThreat.DoubleThree, threeThree.Rate(At(8, 7), 1, out value));

            // The shapes follow the stones.
            Assert.AreEqual(GomokuShape.OpenThree, threeThree.Shape(At(8, 7), 1, Across));
            threeThree.Play(At(5, 7));
            threeThree.Play(At(9, 7));
            Assert.AreEqual(GomokuShape.Four, threeThree.Shape(At(8, 7), 1, Across), "black lengthened the line and white closed its far end");
            GomokuPosition copy = new GomokuPosition(Size, 5, false);
            copy.CopyFrom(threeThree);
            Assert.AreEqual(GomokuShape.Four, copy.Shape(At(8, 7), 1, Across));
        }

        // Black wins by fours: (8,7) makes a four across, white must take (9,7), and (8,8) then makes two fours.
        private static readonly int[] FoursBlack = { 5, 7, 6, 7, 7, 7, 8, 5, 8, 6, 6, 6, 9, 9 };
        private static readonly int[] FoursWhite = { 4, 7, 8, 4, 5, 5, 0, 0, 14, 0, 0, 14, 14, 14 };

        [Test]
        public void TheSearchFindsAWinByARowOfFours()
        {
            GomokuPosition position = Position(1, FoursBlack, FoursWhite);
            for (int i = 0; i < position.CandidateCount; i++)
                Assert.AreNotEqual(GomokuThreat.Winning, position.Rate(position.Candidate(i), 1, out _), "no single move wins yet");

            GomokuSearchResult result = new GomokuMcts().Search(position, 100, new Random(1));
            Assert.IsTrue(result.Solved);
            Assert.AreEqual(0, result.Simulations);
            Assert.IsTrue(result.Move == At(8, 7) || result.Move == At(8, 8));

            GomokuSearchResult blind = new GomokuMcts { ReadForcedWins = false }.Search(position, 100, new Random(1));
            Assert.IsFalse(blind.Solved);
            Assert.AreEqual(100, blind.Simulations);
        }

        [Test]
        public void TheSearchKeepsTheOpponentFromAWinByFours()
        {
            int[] white = new int[FoursWhite.Length - 2];
            Array.Copy(FoursWhite, white, white.Length);
            GomokuPosition position = Position(2, FoursBlack, white);
            for (int seed = 1; seed <= 3; seed++)
            {
                GomokuSearchResult result = new GomokuMcts().Search(position, 100, new Random(seed));
                GomokuPosition after = new GomokuPosition(Size, 5, false);
                after.CopyFrom(position);
                after.Play(result.Move);
                Assert.IsFalse(new GomokuMcts().Search(after, 100, new Random(1)).Solved,
                    "white played " + result.Move % Size + "," + result.Move / Size + " and black still wins by fours");
            }
        }

        [Test]
        public void TheSearchTakesAWinAndStopsOne()
        {
            GomokuMcts search = new GomokuMcts();
            GomokuPosition win = Position(1, new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 3, 8, 4, 8, 5, 8, 6, 8 });
            GomokuSearchResult result = search.Search(win, 100, new Random(1));
            Assert.IsTrue(result.Forced);
            Assert.IsTrue(result.Move == At(2, 7) || result.Move == At(7, 7), "black should win, not stop white");

            GomokuPosition stop = Position(2, new[] { 3, 7, 4, 7, 5, 7, 6, 7 }, new[] { 2, 7, 4, 8, 5, 8 });
            result = search.Search(stop, 100, new Random(1));
            Assert.IsTrue(result.Forced);
            Assert.AreEqual(At(7, 7), result.Move);
        }

        [Test]
        public void TheSearchAnswersAnOpenThreeAtEveryLevel()
        {
            GomokuPosition three = Position(2, new[] { 6, 7, 7, 7, 8, 7 }, new[] { 7, 8, 6, 9 });
            foreach (int simulations in new[] { 500, 2000 })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    GomokuSearchResult result = new GomokuMcts().Search(three, simulations, new Random(seed));
                    int x = result.Move % Size;
                    Assert.AreEqual(7, result.Move / Size, simulations + " simulations left the three alone");
                    Assert.IsTrue(x == 4 || x == 5 || x == 9 || x == 10, simulations + " simulations left the three alone");
                    Assert.AreEqual(simulations, result.Simulations);
                }
            }
        }

        [Test]
        public void TheSearchStartsInTheMiddleAndRepeatsWithTheSameSeed()
        {
            GomokuPosition empty = new GomokuPosition(Size, 5, false);
            Assert.AreEqual(At(7, 7), new GomokuMcts().Search(empty, 100, new Random(1)).Move);

            GomokuPosition position = Position(2, new[] { 7, 7, 8, 8 }, new[] { 6, 6 });
            GomokuSearchResult first = new GomokuMcts().Search(position, 300, new Random(5));
            GomokuSearchResult second = new GomokuMcts().Search(position, 300, new Random(5));
            Assert.AreEqual(first.Move, second.Move);
            Assert.AreEqual(first.Visits, second.Visits);
            Assert.AreEqual(first.Nodes, second.Nodes);
            Assert.AreEqual(0, position.Cell(first.Move));
            Assert.AreEqual(3, position.Moves, "the search must not change the position it was given");
        }

        [Test]
        public void TranspositionsShareOnePositionInTheTable()
        {
            GomokuMcts search = new GomokuMcts();
            GomokuPosition position = Position(1, new[] { 7, 7, 8, 8 }, new[] { 6, 6, 8, 6 });
            GomokuSearchResult result = search.Search(position, 2000, new Random(3));
            Assert.Greater(result.Transpositions, 0);
            Assert.AreEqual(result.Nodes, search.NodeCount);
        }

        [Test]
        public void TheTreeIsKeptForTheNextMoveAndDroppedForAnotherGame()
        {
            GomokuMcts search = new GomokuMcts();
            GomokuPosition position = Position(1, new[] { 7, 7, 8, 8 }, new[] { 6, 6, 8, 6 });
            GomokuSearchResult first = search.Search(position, 400, new Random(3));
            position.Play(first.Move);
            int before = search.NodeCount;
            GomokuSearchResult second = search.Search(position, 400, new Random(4));
            Assert.Less(search.NodeCount, before + 401, "the position after the chosen move was already known");
            Assert.Greater(second.Visits, 0);

            search.Search(new GomokuPosition(9, 5, false), 50, new Random(1));
            Assert.LessOrEqual(search.NodeCount, 51, "positions of a 15 x 15 board mean nothing on a 9 x 9 one");
        }

        [Test]
        public void ASearchCanBeCancelled()
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                GomokuPosition position = Position(2, new[] { 7, 7 }, new int[0]);
                Assert.Throws<OperationCanceledException>(() => new GomokuMcts().Search(position, 1000, new Random(1), cancel.Token));
            }
        }

        [Test]
        public void MoreSimulationsPlayBetter()
        {
            double points = 0;
            const int Games = 6;
            for (int game = 0; game < Games; game++)
            {
                bool strongIsBlack = game % 2 == 0;
                GomokuMcts black = new GomokuMcts();
                GomokuMcts white = new GomokuMcts();
                Random random = new Random(100 + game);
                GomokuPosition position = new GomokuPosition(Size, 5, false);
                int winner = 0;
                while (!position.IsFull && winner == 0)
                {
                    bool blackToMove = position.Current == 1;
                    int simulations = blackToMove == strongIsBlack ? 400 : 30;
                    int mover = position.Current;
                    if (position.Play((blackToMove ? black : white).Search(position, simulations, random).Move))
                        winner = mover;
                }
                points += winner == 0 ? 0.5 : (winner == 1) == strongIsBlack ? 1 : 0;
            }
            Assert.GreaterOrEqual(points, Games * 0.75, "400 simulations scored " + points + " of " + Games + " against 30");
        }

        [Test]
        public void TheManifestNamesTheEngineAndItsLevels()
        {
            ModManifest gomoku = TestSupport.LoadGomoku().Manifest;
            Assert.AreEqual("gomoku_mcts", gomoku.AiEngine);
            Assert.AreEqual(3, gomoku.AiLevels.Count);
            Assert.AreEqual(100, gomoku.AiLevels[0]);
            Assert.AreEqual(500, gomoku.AiLevels[1]);
            Assert.AreEqual(2000, gomoku.AiLevels[2]);
            Assert.IsTrue(ComputerPlayers.Has(gomoku.AiEngine));
            Assert.IsNotNull(ComputerPlayers.Create(gomoku));

            const string Head = "{\"id\":\"m\",\"name\":\"M\",\"api\":1,\"entry\":\"main.luau\"";
            ModManifest plain = ModManifest.Parse(Head + "}");
            Assert.AreEqual(string.Empty, plain.AiEngine);
            Assert.AreEqual(0, plain.AiLevels.Count);
            Assert.IsNull(ComputerPlayers.Create(plain));
            Assert.IsNull(ComputerPlayers.Create(ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"from_the_future\",\"levels\":[1]}}")),
                "an engine this build does not know leaves the mod with random moves");

            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":3}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"gomoku_mcts\"}}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"gomoku_mcts\",\"levels\":[100,0]}}"));
            Assert.Throws<ModException>(() => ModManifest.Parse(Head + ",\"ai\":{\"engine\":\"gomoku_mcts\",\"levels\":7}}"));
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
                Assert.AreEqual(100, host.AiEffort);

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
                Assert.AreEqual(2000, host.AiEffort);

                PlayAGameAgainstTheComputer(host);
                Assert.AreEqual(3, host.AiProgress.GamesPlayed("gomoku"));
                Assert.AreEqual(3, host.AiLevel, "the last level stays");
                Assert.AreEqual(2000, host.AiEffort);

                host.AiProgress.Reset("gomoku");
                Assert.AreEqual(100, host.AiEffort);
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
                Assert.AreEqual(100, host.AiEffort);
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
                Assert.AreEqual(2000, host.AiEffort);

            File.WriteAllText(path, "{ not json");
            Assert.IsFalse(loaded.Load());
            Assert.Greater(loaded.Problem.Length, 0);
            Assert.AreEqual(0, loaded.GamesPlayed("gomoku"));
        }

        [Test]
        public void ModsWithoutAnEngineRefuseTheThinkingComputer()
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

        [Test]
        public void TheEngineRefusesGamesItCannotRead()
        {
            const string Script = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end " +
                                  "function rules.query() return { size = 99 } end return rules";
            GomokuMctsPlayer player = new GomokuMctsPlayer();
            using (GameSession session = new GameSession(TestSupport.InlineMod(Script), new SessionOptions()))
            {
                session.NewGame(null);
                Assert.AreEqual("NOT_SUPPORTED", Assert.Throws<GameSessionException>(() => player.Prepare(session, 1, 100, 1)).Code);
            }
            using (GameSession session = TestSupport.StartGomoku())
            {
                Assert.AreEqual("NOT_YOUR_TURN", Assert.Throws<GameSessionException>(() => player.Prepare(session, 2, 100, 1)).Code);
                JsonValue command = player.Prepare(session, 1, 100, 1)(CancellationToken.None);
                Assert.AreEqual("place_stone", command["action"].AsString());
                Assert.AreEqual(8, command["x"].AsInt());
                Assert.AreEqual(8, command["y"].AsInt());
                Assert.IsTrue(player.LastResult.Forced);
            }
        }
    }
}
