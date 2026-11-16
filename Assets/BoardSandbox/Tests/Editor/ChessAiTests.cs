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
    // The computer player of the chess mod, which is Luau like the rest of the game (Mods/chess/ai.luau).
    [TestFixture]
    public class ChessAiTests
    {
        private double _time;

        [SetUp]
        public void ResetClock()
        {
            _time = 0;
        }

        private static GameSession Game(string fen = null)
        {
            GameSession session = new GameSession(TestSupport.LoadMod("chess"), new SessionOptions());
            session.NewGame(fen == null ? null : JsonValue.NewObject().Set("fen", fen));
            return session;
        }

        private static string Uci(JsonValue command)
        {
            Assert.IsNotNull(command, "the computer found no move");
            Assert.AreEqual("move", command["action"].AsString());
            return command["from"].AsString() + command["to"].AsString() + command["promotion"].AsString();
        }

        // The move the script chooses for the side to move, as e2e4 or a7a8q.
        private static string Best(string fen, int effort, int seed = 1)
        {
            using (GameSession session = Game(fen))
                return Uci(TestSupport.Think(session, session.View.Turn, effort, seed));
        }

        // The pieces and the side to move of the position on the board.
        private static string Placement(GameSession session)
        {
            string[] fields = session.Query("fen").AsString().Split(' ');
            return fields[0] + " " + fields[1];
        }

        private static void Play(GameSession session, string uci)
        {
            SubmitResult result = session.Submit(InputPacket.Action(session.View.Turn, "move").With("uci", uci));
            Assert.IsTrue(result.IsAccepted, uci + " was refused: " + result);
        }

        private GameHost NewHost(string fen = null)
        {
            GameHostOptions options = new GameHostOptions { Clock = () => _time, AiDelaySeconds = 0.5, AiSeed = 17, AiThreaded = false };
            GameHost host = new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), options);
            Assert.IsTrue(host.SelectMod("chess"));
            if (fen != null)
                host.SetOption("fen", JsonValue.Of(fen));
            Assert.IsTrue(host.StartNewGame());
            return host;
        }

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

        [Test]
        public void TheChessModBringsItsOwnPlayer()
        {
            ModPackage chess = ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "chess"));
            Assert.AreEqual("luau", chess.Manifest.AiEngine);
            Assert.AreEqual(3, chess.Manifest.AiLevels.Count);
            Assert.AreEqual(1000, chess.Manifest.AiLevels[0]);
            Assert.AreEqual(30000, chess.Manifest.AiLevels[2]);
            Assert.IsTrue(ComputerPlayers.Create(chess.Manifest) is LuauComputerPlayer);
            Assert.IsTrue(chess.Sources.ContainsKey("ai.luau"), "the player is a script of the mod");
        }

        [Test]
        public void TheScriptSeesSimpleTacticsAtTheFirstLevel()
        {
            const int Effort = 1000;
            Assert.AreEqual("a1a8", Best("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1", Effort), "mate in one");
            Assert.AreEqual("g5f7", Best("6rk/6pp/8/6N1/8/8/8/Q5K1 w - - 0 1", Effort), "smothered mate");
            Assert.AreEqual("d1g4", Best("rnb1kbnr/pppp1ppp/8/4p3/4P1q1/8/PPPP1PPP/RNBQKBNR w KQkq - 0 1", Effort), "a queen for nothing");
            Assert.AreEqual("e3d5", Best("r3k3/8/8/3q4/8/4N3/8/4K3 w - - 0 1", Effort));
            Assert.AreEqual("b5c7", Best("r3k3/8/8/1N6/8/8/8/4K3 w - - 0 1", Effort), "a fork of king and rook");
            Assert.AreEqual("e4f6", Best("6k1/3q4/8/8/4N3/8/8/6K1 w - - 0 1", Effort), "a fork of king and queen");
            Assert.AreEqual("a7a8q", Best("8/P1k5/8/8/8/8/8/K7 w - - 0 1", Effort), "a pawn becomes a queen before it is taken");
            string first = Best("6k1/5ppp/8/8/8/8/8/RR4K1 w - - 0 1", Effort);
            Assert.IsTrue(first == "a1a8" || first == "b1b8", "mate in two with two rooks, not " + first);
            for (int seed = 1; seed <= 3; seed++)
            {
                string saved = Best("rnbqkbnr/ppp1pppp/8/8/3p4/2Q5/PPPP1PPP/RNB1KBNR w KQkq - 0 1", Effort, seed);
                Assert.IsTrue(saved.StartsWith("c3", StringComparison.Ordinal) && saved != "c3d4" && saved != "c3c7" && saved != "c3c6",
                    "the attacked queen must move to safety, not " + saved);
            }
        }

        [Test]
        public void TheScriptWinsAWonEndingAtTheFirstLevel()
        {
            // A pawn that queens, and then king and queen against the king: the search is far too shallow to see the
            // mate from the start, so it has to know where it is going.
            using (LuauComputerPlayer player = new LuauComputerPlayer())
            using (GameSession session = Game("8/P6k/8/8/8/8/8/K7 w - - 0 1"))
            {
                for (int ply = 0; ply < 80 && !session.View.IsOver; ply++)
                {
                    int seat = session.View.Turn;
                    JsonValue command = player.Prepare(session, seat, seat == 1 ? 1000 : 1, ply + 1)(CancellationToken.None);
                    Assert.IsTrue(session.Submit(new InputPacket(seat, command, "ai")).IsAccepted);
                }
                Assert.IsTrue(session.View.IsOver, "white did not finish the game");
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual("checkmate", session.View.Over.Reason);
            }
        }

        [Test]
        public void TheScriptRepeatsWithTheSameSeedAndLeavesTheGameAlone()
        {
            using (GameSession session = Game())
            {
                foreach (string move in new[] { "e2e4", "e7e5", "g1f3", "b8c6" })
                    Play(session, move);
                string before = session.Query("fen").AsString();
                int history = session.HistoryCount;
                string first = Uci(TestSupport.Think(session, 1, 3000, 5));
                Assert.AreEqual(first, Uci(TestSupport.Think(session, 1, 3000, 5)));
                Assert.AreEqual(before, session.Query("fen").AsString(), "thinking must not change the game");
                Assert.AreEqual(history, session.HistoryCount);
                Play(session, first);
            }
        }

        [Test]
        public void TheScriptHandlesTheOnlyMoveTheWrongSeatAndTheEndOfTheGame()
        {
            Assert.AreEqual("h1g2", Best("7k/8/8/8/8/8/6q1/7K w - - 0 1", 1000));
            using (GameSession session = Game())
            {
                Assert.IsNull(TestSupport.Think(session, 2, 100), "black is not to move");
                Assert.IsTrue(session.Query("think").IsNull, "a question without a seat has no answer");
            }
            using (GameSession stalemate = Game("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1"))
            {
                Assert.IsTrue(stalemate.View.IsOver);
                Assert.AreEqual("GAME_OVER", Assert.Throws<GameSessionException>(() => TestSupport.Think(stalemate, 2, 100)).Code);
            }
        }

        [Test]
        public void TheWinningSideDoesNotGoBackToAPositionOfTheGame()
        {
            // White is a queen up and has shuffled back and forth once; every position so far is one to stay away from.
            using (GameSession session = Game("6k1/5ppp/8/8/8/8/5PPP/3Q2K1 w - - 0 1"))
            {
                HashSet<string> seen = new HashSet<string> { Placement(session) };
                foreach (string move in new[] { "d1d2", "g8h8", "d2d1", "h8g8" })
                {
                    Play(session, move);
                    seen.Add(Placement(session));
                }
                for (int seed = 1; seed <= 4; seed++)
                {
                    string before = session.CaptureSnapshot();
                    string move = Uci(TestSupport.Think(session, 1, 1000, seed));
                    Play(session, move);
                    Assert.IsFalse(seen.Contains(Placement(session)), move + " goes back to a position of the game");
                    session.LoadSnapshot(before);
                }
            }
        }

        [Test]
        public void EveryMoveOfTheScriptIsAcceptedThroughPromotionsAndToTheEnd()
        {
            using (LuauComputerPlayer player = new LuauComputerPlayer())
            using (GameSession session = Game("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1"))
            {
                for (int ply = 0; ply < 120 && !session.View.IsOver; ply++)
                {
                    int seat = session.View.Turn;
                    JsonValue command = player.Prepare(session, seat, 300, ply + 1)(CancellationToken.None);
                    SubmitResult result = session.Submit(new InputPacket(seat, command, "ai"));
                    Assert.IsTrue(result.IsAccepted, "ply " + ply + ": " + (command == null ? "no move" : command.ToJson()) + " was refused: " + result);
                }
            }
        }

        [Test]
        public void MoreEffortPlaysBetter()
        {
            double points = 0;
            const int Games = 4;
            using (LuauComputerPlayer player = new LuauComputerPlayer())
            {
                for (int game = 0; game < Games; game++)
                {
                    int strong = game % 2 == 0 ? 1 : 2;
                    Random random = new Random(40 + game / 2);
                    using (GameSession session = Game())
                    {
                        for (int ply = 0; ply < 2; ply++)
                            session.Submit(new InputPacket(session.View.Turn, session.RandomCommand(session.View.Turn, random.Next(1, 1000)), "test"));
                        for (int ply = 0; ply < 240 && !session.View.IsOver; ply++)
                        {
                            int seat = session.View.Turn;
                            JsonValue command = player.Prepare(session, seat, seat == strong ? 4000 : 100, random.Next(1, int.MaxValue))(CancellationToken.None);
                            Assert.IsTrue(session.Submit(new InputPacket(seat, command, "ai")).IsAccepted);
                        }
                        points += !session.View.IsOver || session.View.Over.Winner == 0 ? 0.5 : session.View.Over.Winner == strong ? 1 : 0;
                    }
                }
            }
            Assert.GreaterOrEqual(points, Games * 0.75, "effort 4000 scored " + points + " of " + Games + " against 100");
        }

        [Test]
        public void TheThinkingComputerAnswersAMoveAndTakesAQueenLeftHanging()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.HasAi);
                Assert.AreEqual(1000, host.AiEffort);
                Assert.IsTrue(host.SetSeatMode(2, SeatMode.Ai));
                Assert.IsTrue(host.Submit(1, InputPacket.Action(1, "move").With("uci", "e2e4").Command).IsAccepted);
                Assert.IsTrue(ComputerMoves(host), "black did not answer");
                Assert.AreEqual(1, host.Session.View.Turn);
                Assert.AreEqual(2, host.Session.Query("state")["plies"].AsInt());

                Assert.IsTrue(host.Submit(1, InputPacket.Action(1, "move").With("uci", "d1h5").Command).IsAccepted);
                Assert.IsTrue(ComputerMoves(host));
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
                Assert.AreEqual(4, host.Session.Query("state")["plies"].AsInt());
            }
            using (GameHost host = NewHost("rnb1kbnr/pppp1ppp/8/4p3/4P1q1/8/PPPP1PPP/RNBQKBNR w KQkq - 0 1"))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsTrue(ComputerMoves(host));
                StringAssert.StartsWith("rnb1kbnr/pppp1ppp/8/4p3/4P1Q1/", host.Session.Query("fen").AsString());
            }
        }

        [Test]
        public void TheThinkingComputerMatesAndTheGameCountsTowardsItsLevel()
        {
            using (GameHost host = NewHost("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1"))
            {
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Ai));
                Assert.IsTrue(ComputerMoves(host));
                Assert.IsTrue(host.Session.View.IsOver);
                JsonValue state = host.Session.Query("state");
                Assert.AreEqual("1-0", state["result"].AsString());
                Assert.AreEqual(1, host.AiProgress.GamesPlayed("chess"));
                Assert.AreEqual(0, host.AiProgress.GamesPlayed("gomoku"), "every game has its own level");
                Assert.AreEqual(2, host.AiLevel);
                Assert.AreEqual(6000, host.AiEffort);
            }
        }
    }
}
