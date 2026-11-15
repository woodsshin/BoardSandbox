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
    public class ChessAiTests
    {
        private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        private const string Endgame = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
        private const string Promotions = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
        private const string Tricky = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

        private double _time;

        [SetUp]
        public void ResetClock()
        {
            _time = 0;
        }

        private static long Perft(ChessPosition position, int depth, int[][] buffers)
        {
            if (depth == 0)
                return 1;
            int[] moves = buffers[depth];
            int count = position.PseudoMoves(moves, false);
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                ulong before = position.Hash;
                if (!position.TryMake(moves[i]))
                    continue;
                total += Perft(position, depth - 1, buffers);
                position.Unmake();
                Assert.AreEqual(before, position.Hash, "taking a move back must restore the hash");
            }
            return total;
        }

        private static long Perft(string fen, int depth)
        {
            int[][] buffers = new int[depth + 1][];
            for (int i = 0; i <= depth; i++)
                buffers[i] = new int[ChessPosition.MaxMoves];
            return Perft(new ChessPosition(fen), depth, buffers);
        }

        private static ChessPosition After(string fen, params string[] moves)
        {
            ChessPosition position = new ChessPosition(fen);
            int[] legal = new int[ChessPosition.MaxMoves];
            foreach (string uci in moves)
            {
                int count = position.LegalMoves(legal);
                int found = -1;
                for (int i = 0; i < count; i++)
                {
                    if (ChessPosition.Uci(legal[i]) == uci)
                        found = legal[i];
                }
                Assert.GreaterOrEqual(found, 0, uci + " is not legal");
                position.Make(found);
            }
            return position;
        }

        private static string Best(string fen, int simulations, int seed = 1)
        {
            return new ChessMcts().Search(new ChessPosition(fen), simulations, new Random(seed)).Uci;
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
        public void TheMoveGeneratorCountsTheKnownNumbersOfPositions()
        {
            Assert.AreEqual(20, Perft(ChessPosition.StartFen, 1));
            Assert.AreEqual(8902, Perft(ChessPosition.StartFen, 3));
            Assert.AreEqual(2039, Perft(Kiwipete, 2), "castling, pins and en passant");
            Assert.AreEqual(97862, Perft(Kiwipete, 3));
            Assert.AreEqual(43238, Perft(Endgame, 4), "en passant that uncovers a check");
            Assert.AreEqual(9467, Perft(Promotions, 3), "promotions");
            Assert.AreEqual(62379, Perft(Tricky, 3));
        }

        [Test]
        public void TheEngineAndTheModAgreeOnTheLegalMoves()
        {
            ModPackage chess = ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "chess"));
            int[] moves = new int[ChessPosition.MaxMoves];
            foreach (string fen in new[] { ChessPosition.StartFen, Kiwipete, Endgame, Promotions, Tricky })
            {
                using (GameSession session = new GameSession(chess, new SessionOptions()))
                {
                    session.NewGame(JsonValue.NewObject().Set("fen", fen));
                    JsonValue state = session.Query("state");
                    ChessPosition position = ChessMctsPlayer.ReadPosition(state);
                    int count = position.LegalMoves(moves);
                    Assert.AreEqual(state["legal_moves"].AsInt(), count, fen);

                    HashSet<string> mine = new HashSet<string>();
                    for (int i = 0; i < count; i++)
                        mine.Add(ChessPosition.Uci(moves[i]));
                    foreach (JsonValue command in session.LegalCommands(position.Side + 1))
                    {
                        string uci = command["from"].AsString() + command["to"].AsString() + command["promotion"].AsString();
                        Assert.IsTrue(mine.Contains(uci), uci + " is legal for the mod but not for the engine in " + fen);
                    }
                }
            }
        }

        [Test]
        public void PositionsHashTheSameWhateverTheOrderOfMoves()
        {
            ChessPosition first = After(ChessPosition.StartFen, "g1f3", "g8f6", "b1c3", "b8c6");
            ChessPosition second = After(ChessPosition.StartFen, "b1c3", "b8c6", "g1f3", "g8f6");
            Assert.AreEqual(first.Hash, second.Hash);
            Assert.AreNotEqual(new ChessPosition().Hash, first.Hash);
            Assert.AreEqual(first.Hash, new ChessPosition(first.ToFen()).Hash, "a position read back from its FEN is the same position");

            // The same pieces with other castling rights, another side to move or an en passant square differ.
            Assert.AreNotEqual(new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1").Hash, new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R w Kkq - 0 1").Hash);
            Assert.AreNotEqual(new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1").Hash, new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 1").Hash);
            ChessPosition pushed = After(ChessPosition.StartFen, "e2e4");
            Assert.AreEqual("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", pushed.ToFen());
            Assert.AreNotEqual(pushed.Hash, new ChessPosition("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1").Hash);

            ChessPosition copy = new ChessPosition();
            copy.CopyFrom(first);
            Assert.AreEqual(first.Hash, copy.Hash);
            Assert.AreEqual(first.ToFen(), copy.ToFen());
        }

        [Test]
        public void PositionsKnowChecksDrawsAndTheirWorth()
        {
            Assert.IsFalse(new ChessPosition().InCheck());
            Assert.IsTrue(new ChessPosition("4k3/8/8/8/8/8/4R3/4K3 b - - 0 1").InCheck());
            Assert.IsTrue(new ChessPosition("4k3/8/8/8/8/8/8/4K3 w - - 0 1").InsufficientMaterial());
            Assert.IsTrue(new ChessPosition("4k3/8/8/8/8/8/8/4KN2 w - - 0 1").InsufficientMaterial());
            Assert.IsFalse(new ChessPosition("4k3/8/8/8/8/8/8/4KR2 w - - 0 1").InsufficientMaterial());
            Assert.IsFalse(new ChessPosition("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1").InsufficientMaterial());

            Assert.AreEqual(10, new ChessPosition().Evaluate(), "the start is even but for the move");
            Assert.AreEqual(10, After(ChessPosition.StartFen, "e2e4", "e7e5").Evaluate());
            Assert.Greater(new ChessPosition("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1").Evaluate(), 800, "white is a queen up");
            Assert.Less(new ChessPosition("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1").Evaluate(), -800);
            Assert.Greater(new ChessPosition("7k/8/5K2/8/8/8/8/R7 w - - 0 1").Evaluate(), new ChessPosition("8/8/3k4/8/8/8/8/R3K3 w - - 0 1").Evaluate(),
                "a bare king in the corner with the other king near is closer to mate");

            Assert.Throws<FormatException>(() => new ChessPosition("not a position"));
            Assert.Throws<FormatException>(() => new ChessPosition("8/8/8/8/8/8/8/8 w - - 0 1"));
            Assert.Throws<FormatException>(() => new ChessPosition("4k3/8/8/8/8/8/4R3/4K3 w - - 0 1"), "black cannot be in check on white's move");
            Assert.AreEqual("e7e8q", ChessPosition.Uci(FirstPromotion()));
            Assert.AreEqual("a1", ChessPosition.SquareName(0));
            Assert.AreEqual("h8", ChessPosition.SquareName(63));
        }

        private static int FirstPromotion()
        {
            ChessPosition position = new ChessPosition("8/4P2k/8/8/8/8/8/K7 w - - 0 1");
            int[] moves = new int[ChessPosition.MaxMoves];
            int count = position.LegalMoves(moves);
            for (int i = 0; i < count; i++)
            {
                if (ChessPosition.Promotion(moves[i]) == ChessPosition.Queen)
                    return moves[i];
            }
            return 0;
        }

        [Test]
        public void TheSearchSeesSimpleTacticsAtTheFirstLevel()
        {
            Assert.AreEqual("a1a8", Best("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1", 100), "mate in one");
            Assert.AreEqual("g5f7", Best("6rk/6pp/8/6N1/8/8/8/Q5K1 w - - 0 1", 100), "smothered mate");
            Assert.AreEqual("d1g4", Best("rnb1kbnr/pppp1ppp/8/4p3/4P1q1/8/PPPP1PPP/RNBQKBNR w KQkq - 0 1", 100), "a queen for nothing");
            Assert.AreEqual("e3d5", Best("r3k3/8/8/3q4/8/4N3/8/4K3 w - - 0 1", 100));
            Assert.AreEqual("b5c7", Best("r3k3/8/8/1N6/8/8/8/4K3 w - - 0 1", 100), "a fork of king and rook");
            Assert.AreEqual("e4f6", Best("6k1/3q4/8/8/4N3/8/8/6K1 w - - 0 1", 100), "a fork of king and queen");
            Assert.AreEqual("a7a8q", Best("8/P6k/8/8/8/8/8/K7 w - - 0 1", 100), "a pawn becomes a queen");
            string first = Best("6k1/5ppp/8/8/8/8/8/RR4K1 w - - 0 1", 100);
            Assert.IsTrue(first == "a1a8" || first == "b1b8", "mate in two with two rooks, not " + first);
            string saved = Best("rnbqkbnr/ppp1pppp/8/8/3p4/2Q5/PPPP1PPP/RNB1KBNR w KQkq - 0 1", 100);
            Assert.IsTrue(saved.StartsWith("c3", StringComparison.Ordinal) && saved != "c3d4" && saved != "c3c7" && saved != "c3c6",
                "the attacked queen must move to safety, not " + saved);
        }

        [Test]
        public void TheSearchRepeatsWithTheSameSeedAndSharesTranspositions()
        {
            ChessPosition position = After(ChessPosition.StartFen, "e2e4", "e7e5", "g1f3", "b8c6");
            string before = position.ToFen();
            ChessSearchResult first = new ChessMcts().Search(position, 400, new Random(5));
            ChessSearchResult second = new ChessMcts().Search(position, 400, new Random(5));
            Assert.AreEqual(first.Uci, second.Uci);
            Assert.AreEqual(first.Visits, second.Visits);
            Assert.AreEqual(400, first.Simulations);
            Assert.Greater(first.Transpositions, 0);
            Assert.Greater(first.Value, 0.3);
            Assert.Less(first.Value, 0.7);
            Assert.AreEqual(before, position.ToFen(), "the search must not change the position it was given");

            // The tree is kept: the reply is searched from what is already known.
            ChessMcts search = new ChessMcts();
            ChessSearchResult move = search.Search(position, 300, new Random(1));
            position.Make(move.Move);
            int known = search.NodeCount;
            search.Search(position, 1, new Random(1));
            Assert.LessOrEqual(search.NodeCount, known + 1);
        }

        [Test]
        public void TheSearchHandlesForcedMovesTheEndOfTheGameAndCancelling()
        {
            ChessSearchResult only = new ChessMcts().Search(new ChessPosition("7k/8/8/8/8/8/6q1/7K w - - 0 1"), 100, new Random(1));
            Assert.IsTrue(only.Forced);
            Assert.AreEqual("h1g2", only.Uci);
            Assert.AreEqual(0, only.Simulations);

            Assert.Throws<InvalidOperationException>(() => new ChessMcts().Search(new ChessPosition("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1"), 100, new Random(1)),
                "stalemate");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => new ChessMcts().Search(new ChessPosition(), 100, new Random(1), null, cancel.Token));
            }
        }

        [Test]
        public void TheWinningSideDoesNotRepeatAPosition()
        {
            // White is a queen up. Whatever it would play, tell it that this position was on the board before.
            const string Fen = "6k1/5ppp/8/8/8/8/5PPP/3Q2K1 w - - 0 1";
            ChessPosition position = new ChessPosition(Fen);
            ChessSearchResult free = new ChessMcts().Search(position, 300, new Random(2));
            ChessPosition after = new ChessPosition(Fen);
            after.Make(free.Move);

            ChessSearchResult wary = new ChessMcts().Search(position, 300, new Random(2), new[] { after.Hash });
            Assert.AreNotEqual(free.Uci, wary.Uci, "that move would repeat a position and throw the win away");
            Assert.Greater(wary.Value, 0.8);
        }

        [Test]
        public void MoreSimulationsPlayBetter()
        {
            double points = 0;
            const int Games = 4;
            int[] legal = new int[ChessPosition.MaxMoves];
            for (int game = 0; game < Games; game++)
            {
                bool strongIsWhite = game % 2 == 0;
                ChessMcts white = new ChessMcts();
                ChessMcts black = new ChessMcts();
                Random random = new Random(40 + game / 2);
                ChessPosition position = new ChessPosition();
                List<ulong> played = new List<ulong>();
                double result = 0.5;
                for (int ply = 0; ply < 160; ply++)
                {
                    int count = position.LegalMoves(legal);
                    if (count == 0)
                    {
                        result = !position.InCheck() ? 0.5 : (position.Side == 1) == strongIsWhite ? 1 : 0;
                        break;
                    }
                    if (position.InsufficientMaterial() || position.HalfMoves >= 100)
                        break;
                    played.Add(position.Hash);
                    bool strong = (position.Side == 0) == strongIsWhite;
                    int move = ply < 2
                        ? legal[random.Next(count)]
                        : (position.Side == 0 ? white : black).Search(position, strong ? 200 : 15, random, played).Move;
                    position.Make(move);
                    if (ply == 159)
                    {
                        // Not finished: the side that is clearly ahead gets the game.
                        int ahead = position.Evaluate() * (position.Side == 0 ? 1 : -1);
                        result = Math.Abs(ahead) < 300 ? 0.5 : (ahead > 0) == strongIsWhite ? 1 : 0;
                    }
                }
                points += result;
            }
            Assert.GreaterOrEqual(points, Games * 0.75, "200 simulations scored " + points + " of " + Games + " against 15");
        }

        [Test]
        public void TheChessModNamesTheEngine()
        {
            ModManifest chess = ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "chess")).Manifest;
            Assert.AreEqual("chess_mcts", chess.AiEngine);
            Assert.AreEqual(3, chess.AiLevels.Count);
            Assert.AreEqual(100, chess.AiLevels[0]);
            Assert.AreEqual(2000, chess.AiLevels[2]);
            Assert.IsTrue(ComputerPlayers.Create(chess) is ChessMctsPlayer);
        }

        [Test]
        public void TheThinkingComputerAnswersAMoveAndTakesAQueenLeftHanging()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.HasAi);
                Assert.AreEqual(100, host.AiEffort);
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
                Assert.AreEqual(500, host.AiEffort);
            }
        }

        [Test]
        public void TheEngineRefusesWhatItCannotPlay()
        {
            ChessMctsPlayer player = new ChessMctsPlayer();
            ModPackage chess = ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "chess"));
            using (GameSession session = new GameSession(chess, new SessionOptions()))
            {
                session.NewGame(null);
                Assert.AreEqual("NOT_YOUR_TURN", Assert.Throws<GameSessionException>(() => player.Prepare(session, 2, 100, 1)).Code);
                JsonValue command = player.Prepare(session, 1, 50, 1)(CancellationToken.None);
                Assert.AreEqual("move", command["action"].AsString());
                Assert.AreEqual(4, command["uci"].AsString().Length);
                Assert.IsTrue(session.Submit(new InputPacket(1, command, "ai")).IsAccepted);
                Assert.AreEqual(50, player.LastResult.Simulations);
            }
            using (GameSession session = new GameSession(chess, new SessionOptions()))
            {
                session.NewGame(JsonValue.NewObject().Set("fen", "7k/5Q2/6K1/8/8/8/8/8 b - - 0 1"));
                Assert.AreEqual("GAME_OVER", Assert.Throws<GameSessionException>(() => player.Prepare(session, 2, 100, 1)).Code);
            }
            using (GameSession session = TestSupport.StartGomoku())
                Assert.AreEqual("NOT_SUPPORTED", Assert.Throws<GameSessionException>(() => player.Prepare(session, 1, 100, 1)).Code);
        }
    }
}
