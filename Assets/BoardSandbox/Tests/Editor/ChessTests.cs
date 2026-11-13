using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class ChessTests
    {
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        private const string Castling = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";

        private const string White = "#f3efe6";
        private const string Black = "#161616";
        private const string Selected = "#f2c23a";
        private const string WhiteTarget = "#e8928a";
        private const string BlackCheck = "#c0281c";

        private static ModPackage LoadChess()
        {
            return ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "chess"));
        }

        private static GameSession Start(string fen = null, string optionsJson = "{}", VmOptions vm = null)
        {
            SessionOptions sessionOptions = new SessionOptions();
            if (vm != null)
                sessionOptions.Vm = vm;
            GameSession session = new GameSession(LoadChess(), sessionOptions);
            JsonValue options = JsonValue.Parse(optionsJson);
            if (fen != null)
                options.Set("fen", fen);
            session.NewGame(options);
            return session;
        }

        private static InputPacket MoveCommand(int seat, string uci)
        {
            InputPacket packet = InputPacket.Action(seat, "move").With("from", uci.Substring(0, 2)).With("to", uci.Substring(2, 2));
            if (uci.Length == 5)
                packet.With("promotion", uci.Substring(4, 1));
            return packet;
        }

        // Plays a move for the seat on turn, written as "e2e4" or "e7e8q".
        private static SubmitResult Move(GameSession session, string uci)
        {
            return session.Submit(MoveCommand(session.View.Turn, uci));
        }

        private static void MoveAll(GameSession session, params string[] moves)
        {
            foreach (string uci in moves)
            {
                SubmitResult result = Move(session, uci);
                Assert.IsTrue(result.IsAccepted, uci + " was refused: " + result);
            }
        }

        private static SubmitResult Click(GameSession session, string square)
        {
            return session.Submit(InputPacket.Action(session.View.Turn, "click").With("x", square[0] - 'a' + 1).With("y", 9 - (square[1] - '0')));
        }

        private static ViewObject PieceAt(GameSession session, string square)
        {
            int x = square[0] - 'a' + 1;
            int y = 9 - (square[1] - '0');
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind != "hint" && (int)o.X == x && (int)o.Y == y)
                    return o;
            }
            return null;
        }

        private static string ColorAt(GameSession session, string square)
        {
            return PieceAt(session, square).Props["color"].AsString();
        }

        private static int Hints(GameSession session)
        {
            int count = 0;
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind == "hint")
                    count++;
            }
            return count;
        }

        private static string Fen(GameSession session)
        {
            return session.Query("fen").AsString();
        }

        private static string Moves(GameSession session)
        {
            List<string> moves = new List<string>();
            foreach (JsonValue move in session.Query("moves").Items)
                moves.Add(move.AsString());
            return string.Join(" ", moves);
        }

        private static List<string> LegalMoves(GameSession session)
        {
            List<string> moves = new List<string>();
            foreach (JsonValue move in session.Query("legal_moves").Items)
                moves.Add(move.AsString());
            return moves;
        }

        private static string Describe(ViewModel view)
        {
            JsonValue array = JsonValue.NewArray();
            foreach (ViewEvent e in view.Synthesize())
                array.Add(e.Raw);
            return array.ToJson();
        }

        private static int CountEvents(IEnumerable<ViewEvent> events, string type)
        {
            int count = 0;
            foreach (ViewEvent e in events)
            {
                if (e.Type == type)
                    count++;
            }
            return count;
        }

        [Test]
        public void NewGameAnnouncesTheBoardTheThirtyTwoPiecesAndTheFirstTurn()
        {
            using (GameSession session = new GameSession(LoadChess()))
            {
                IReadOnlyList<ViewEvent> events = session.NewGame();
                Assert.AreEqual("reset", events[0].Type);
                Assert.AreEqual(32, CountEvents(events, "spawn"));
                Assert.AreEqual(ViewEventKind.Turn, events[events.Count - 1].Kind);
                Assert.AreEqual(1, events[events.Count - 1].Seat);
                ViewEvent board = session.View.GetCustom("board");
                Assert.AreEqual(8, board.Raw["columns"].AsInt());
                Assert.AreEqual(8, board.Raw["rows"].AsInt());

                Assert.AreEqual(32, session.View.ObjectCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(StartFen, Fen(session));
                Assert.IsTrue(session.Capabilities.Snapshot);
                Assert.IsTrue(session.Capabilities.Legal);
                Assert.IsTrue(session.Capabilities.Random);
                Assert.IsTrue(session.Capabilities.Query);
                Assert.AreEqual(20, session.LegalCommands(1).Count);
                Assert.AreEqual(0, session.LegalCommands(2).Count);
            }
        }

        [Test]
        public void ThePiecesStartOnTheirSquaresWhiteOnTheNearSide()
        {
            using (GameSession session = Start())
            {
                Assert.AreEqual("king", PieceAt(session, "e1").Kind);
                Assert.AreEqual(1, PieceAt(session, "e1").Owner);
                Assert.AreEqual("queen", PieceAt(session, "d8").Kind);
                Assert.AreEqual(2, PieceAt(session, "d8").Owner);
                Assert.AreEqual("knight", PieceAt(session, "b1").Kind);
                Assert.AreEqual("bishop", PieceAt(session, "f8").Kind);
                Assert.AreEqual("rook", PieceAt(session, "a1").Kind);
                Assert.AreEqual("pawn", PieceAt(session, "h7").Kind);
                Assert.IsNull(PieceAt(session, "e4"));
                // white sits at the high rows, rank 1 is row 8
                Assert.AreEqual(8.0, PieceAt(session, "e1").Y, 1e-9);
                Assert.AreEqual(5.0, PieceAt(session, "e1").X, 1e-9);
                Assert.AreEqual(White, ColorAt(session, "e1"));
                Assert.AreEqual(Black, ColorAt(session, "e8"));
            }
        }

        [TestCase(StartFen, 1, 20)]
        [TestCase(StartFen, 2, 400)]
        [TestCase(StartFen, 3, 8902)]
        [TestCase(StartFen, 4, 197281)]
        [TestCase(Kiwipete, 1, 48)]
        [TestCase(Kiwipete, 2, 2039)]
        [TestCase(Kiwipete, 3, 97862)]
        [TestCase("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 1, 14)]
        [TestCase("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 3, 2812)]
        [TestCase("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 4, 43238)]
        [TestCase("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 3, 9467)]
        [TestCase("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 3, 62379)]
        [TestCase("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", 3, 89890)]
        [TestCase("8/8/1k6/2b5/2pP4/8/5K2/8 b - d3 0 1", 3, 1928)]
        [TestCase("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1", 3, 9483)]
        [TestCase("3k4/3p4/8/K1P4r/8/8/8/8 b - - 0 1", 3, 1670)]
        public void MoveGenerationMatchesTheKnownNodeCounts(string fen, int depth, int nodes)
        {
            using (GameSession session = Start(null, "{}", new VmOptions { CallTimeoutMs = 120000 }))
            {
                JsonValue args = JsonValue.NewObject().Set("depth", depth).Set("fen", fen);
                Assert.AreEqual(nodes, session.Query("perft", args)["nodes"].AsInt());
            }
        }

        [Test]
        public void OpeningMovesAreRecordedInSanAndPgn()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4", "e7e5", "g1f3", "b8c6", "f1b5", "a7a6");
                Assert.AreEqual("e4 e5 Nf3 Nc6 Bb5 a6", Moves(session));
                Assert.AreEqual("1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 *", session.Query("pgn").AsString());
                Assert.AreEqual("r1bqkbnr/1ppp1ppp/p1n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4", Fen(session));
                JsonValue state = session.Query("state");
                Assert.AreEqual(1, state["turn"].AsInt());
                Assert.AreEqual(6, state["plies"].AsInt());
                Assert.IsFalse(state["finished"].AsBool());
                ViewEvent last = session.View.GetCustom("last_move");
                Assert.AreEqual(1, last.Raw["x"].AsInt());
                Assert.AreEqual(3, last.Raw["y"].AsInt());
                Assert.AreEqual("a6", last.Raw["san"].AsString());
                Assert.AreEqual("a7a6", last.Raw["uci"].AsString());
            }
        }

        [Test]
        public void MovesCanBeNamedByFromAndToByUciAndBySan()
        {
            using (GameSession session = Start())
            {
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("uci", "e2e4")).IsAccepted);
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "move").With("san", "Nf6!?")).IsAccepted);
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("from", "B1").With("to", "c3")).IsAccepted);
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "move").With("san", "e6")).IsAccepted);
                Assert.AreEqual("e4 Nf6 Nc3 e6", Moves(session));
            }
        }

        [Test]
        public void IllegalAndOffTurnCommandsAreRefusedWithoutChangingTheGame()
        {
            using (GameSession session = Start())
            {
                string before = Describe(session.View);
                int history = session.HistoryCount;
                (InputPacket, string)[] cases =
                {
                    (InputPacket.Action(1, "dance"), "UNKNOWN_ACTION"),
                    (new InputPacket(1, JsonValue.NewObject()), "UNKNOWN_ACTION"),
                    (InputPacket.Action(2, "move").With("from", "e7").With("to", "e5"), "NOT_YOUR_TURN"),
                    (InputPacket.Action(2, "click").With("x", 3).With("y", 3), "NOT_YOUR_TURN"),
                    (InputPacket.Action(1, "move"), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "move").With("from", "e2"), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "move").With("from", "e9").With("to", "e4"), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "move").With("uci", "e2"), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "move").With("from", "e2").With("to", "e4").With("promotion", "x"), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "move").With("from", "e2").With("to", "e5"), "ILLEGAL_MOVE"),
                    (InputPacket.Action(1, "move").With("from", "e7").With("to", "e5"), "ILLEGAL_MOVE"),
                    (InputPacket.Action(1, "move").With("from", "e4").With("to", "e5"), "ILLEGAL_MOVE"),
                    (InputPacket.Action(1, "move").With("san", "Qh5"), "ILLEGAL_MOVE"),
                    (InputPacket.Action(1, "click").With("x", 0).With("y", 3), "OUT_OF_BOUNDS"),
                    (InputPacket.Action(1, "click").With("x", 3).With("y", 9), "OUT_OF_BOUNDS"),
                    (InputPacket.Action(1, "click").With("x", 3.5).With("y", 3), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "click").With("x", "3").With("y", 3), "INVALID_ARGUMENT"),
                    (InputPacket.Action(1, "click").With("x", 5).With("y", 5), "EMPTY_SQUARE"),
                    (InputPacket.Action(1, "click").With("x", 5).With("y", 2), "NOT_YOUR_PIECE"),
                    (InputPacket.Action(1, "click").With("x", 1).With("y", 8), "NO_MOVES"),
                };
                foreach ((InputPacket packet, string code) in cases)
                {
                    SubmitResult result = session.Submit(packet);
                    Assert.AreEqual(SubmitStatus.Rejected, result.Status, packet.ToString());
                    Assert.AreEqual(code, result.Code, packet.ToString());
                    Assert.AreEqual(0, result.Events.Count);
                }
                Assert.AreEqual(before, Describe(session.View));
                Assert.AreEqual(history, session.HistoryCount);
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        [Test]
        public void APinnedPieceCannotMoveAndACheckMustBeAnswered()
        {
            using (GameSession session = Start("4k3/4r3/8/8/8/8/4B3/4K3 w - - 0 1"))
            {
                Assert.AreEqual("ILLEGAL_MOVE", Move(session, "e2d3").Code);
                Assert.IsFalse(LegalMoves(session).Exists(m => m.StartsWith("B")));
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/8/r3K3 w - - 0 1"))
            {
                SubmitResult refused = Move(session, "e1d1");
                Assert.AreEqual("ILLEGAL_MOVE", refused.Code);
                StringAssert.Contains("check", refused.Message);
                Assert.IsTrue(Move(session, "e1e2").IsAccepted);
            }
        }

        [Test]
        public void ChecksAreAnnouncedAndColourTheKing()
        {
            using (GameSession session = Start())
            {
                List<ViewEvent> statuses = new List<ViewEvent>();
                session.EventApplied += e =>
                {
                    if (e.Kind == ViewEventKind.Status)
                        statuses.Add(e);
                };
                MoveAll(session, "e2e4", "f7f5", "d1h5");
                Assert.AreEqual("Qh5+", Moves(session).Split(' ')[2]);
                Assert.AreEqual(1, statuses.Count);
                Assert.AreEqual("Black is in check", statuses[0].Text);
                Assert.AreEqual("warn", statuses[0].Level);
                Assert.AreEqual(BlackCheck, ColorAt(session, "e8"));
                Assert.AreEqual("ILLEGAL_MOVE", Move(session, "a7a6").Code);
                MoveAll(session, "g7g6");
                Assert.AreEqual(Black, ColorAt(session, "e8"));
            }
        }

        [Test]
        public void CastlingMovesTheRookAndTakesTheRightsAway()
        {
            using (GameSession session = Start(Castling))
            {
                Assert.IsTrue(LegalMoves(session).Contains("O-O"));
                Assert.IsTrue(LegalMoves(session).Contains("O-O-O"));
                MoveAll(session, "e1g1");
                Assert.AreEqual("king", PieceAt(session, "g1").Kind);
                Assert.AreEqual("rook", PieceAt(session, "f1").Kind);
                Assert.IsNull(PieceAt(session, "h1"));
                Assert.IsNull(PieceAt(session, "e1"));
                Assert.AreEqual("r3k2r/8/8/8/8/8/8/R4RK1 b kq - 1 1", Fen(session));
                MoveAll(session, "e8c8");
                Assert.AreEqual("king", PieceAt(session, "c8").Kind);
                Assert.AreEqual("rook", PieceAt(session, "d8").Kind);
                Assert.IsNull(PieceAt(session, "a8"));
                Assert.AreEqual("O-O O-O-O", Moves(session));
            }
        }

        [Test]
        public void CastlingIsNotAllowedThroughOutOfOrAfterLosingTheRights()
        {
            using (GameSession session = Start("r3k2r/8/8/8/8/8/5r2/R3K2R w KQkq - 0 1"))
                AssertOnlyCastling(LegalMoves(session), "O-O-O");
            using (GameSession session = Start("r3k2r/8/8/4r3/8/8/8/R3K2R w KQkq - 0 1"))
                Assert.AreEqual(0, LegalMoves(session).FindAll(m => m.StartsWith("O")).Count);
            using (GameSession session = Start("r3k2r/8/8/8/8/8/3r4/R3K2R w KQkq - 0 1"))
                AssertOnlyCastling(LegalMoves(session), "O-O");
            using (GameSession session = Start("1r2k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1"))
                Assert.AreEqual(2, LegalMoves(session).FindAll(m => m.StartsWith("O")).Count);

            using (GameSession session = Start(Castling))
            {
                MoveAll(session, "h1h2", "h8h7", "h2h1", "h7h8");
                Assert.AreEqual("r3k2r/8/8/8/8/8/8/R3K2R w Qq - 4 3", Fen(session));
                Assert.AreEqual("ILLEGAL_MOVE", Move(session, "e1g1").Code);
                MoveAll(session, "e1c1");
            }
            using (GameSession session = Start("r3k2r/8/8/8/8/8/6b1/R3K2R b KQkq - 0 1"))
            {
                MoveAll(session, "g2h1");
                Assert.AreEqual("Qkq", Fen(session).Split(' ')[2]);
            }
        }

        // The only castling move among the legal moves.
        private static void AssertOnlyCastling(List<string> legal, string expected)
        {
            List<string> castles = legal.FindAll(m => m.StartsWith("O"));
            Assert.AreEqual(1, castles.Count, string.Join(",", castles));
            Assert.AreEqual(expected, castles[0]);
        }

        [Test]
        public void EnPassantRemovesTheBypassedPawnAndOnlyOnTheNextMove()
        {
            using (GameSession session = Start("4k3/2p5/8/3P4/8/8/8/4K3 b - - 0 1"))
            {
                MoveAll(session, "c7c5");
                Assert.AreEqual("4k3/8/8/2pP4/8/8/8/4K3 w - c6 0 2", Fen(session));
                Assert.IsTrue(LegalMoves(session).Contains("dxc6"));
                int objects = session.View.ObjectCount;
                MoveAll(session, "d5c6");
                Assert.AreEqual(objects - 1, session.View.ObjectCount);
                Assert.IsNull(PieceAt(session, "c5"));
                Assert.AreEqual(1, PieceAt(session, "c6").Owner);
                Assert.AreEqual("c5 dxc6", Moves(session));
                Assert.AreEqual("4k3/8/2P5/8/8/8/8/4K3 b - - 0 2", Fen(session));
            }
            using (GameSession session = Start("4k3/2p5/8/3P4/8/8/7P/4K3 b - - 0 1"))
            {
                MoveAll(session, "c7c5", "h2h3", "e8e7");
                Assert.AreEqual("ILLEGAL_MOVE", Move(session, "d5c6").Code);
            }
        }

        [Test]
        public void PromotionTakesTheChosenPieceAndTheOptionsQueenByDefault()
        {
            using (GameSession session = Start("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1"))
            {
                int promotions = 0;
                foreach (JsonValue command in session.LegalCommands(1))
                {
                    if (command.Has("promotion"))
                        promotions++;
                }
                Assert.AreEqual(8, promotions);
                MoveAll(session, "a7b8");
                Assert.AreEqual("queen", PieceAt(session, "b8").Kind);
                Assert.AreEqual(1, PieceAt(session, "b8").Owner);
                Assert.AreEqual("axb8=Q+", Moves(session));
            }
            using (GameSession session = Start("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1"))
            {
                MoveAll(session, "a7a8r");
                Assert.AreEqual("rook", PieceAt(session, "a8").Kind);
                Assert.AreEqual("a8=R", Moves(session));
            }
            using (GameSession session = Start("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1"))
            {
                Assert.AreEqual("INVALID_ARGUMENT", Move(session, "a7a8x").Code);
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("san", "a8=N")).IsAccepted);
                Assert.AreEqual("knight", PieceAt(session, "a8").Kind);
            }
            using (GameSession session = Start("1n2k3/P7/8/8/8/8/8/4K3 w - - 0 1", "{\"promotion\":\"bishop\"}"))
            {
                MoveAll(session, "a7a8");
                Assert.AreEqual("bishop", PieceAt(session, "a8").Kind);
            }
        }

        [Test]
        public void FoolsMateIsCheckmate()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "f2f3", "e7e5", "g2g4");
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "move").With("san", "Qh4#")).IsAccepted);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(2, session.View.Over.Winner);
                Assert.AreEqual("checkmate", session.View.Over.Reason);
                // the mated king and the piece that attacks it are marked
                Assert.AreEqual(2, session.View.Over.Cells.Count);
                Assert.AreEqual(5.0, session.View.Over.Cells[0].X, 1e-9);
                Assert.AreEqual(8.0, session.View.Over.Cells[0].Y, 1e-9);
                Assert.AreEqual(8.0, session.View.Over.Cells[1].X, 1e-9);
                Assert.AreEqual(5.0, session.View.Over.Cells[1].Y, 1e-9);
                Assert.AreEqual("1. f3 e5 2. g4 Qh4# 0-1", session.Query("pgn").AsString());
                Assert.AreEqual("0-1", session.Query("state")["result"].AsString());
                foreach (string action in new[] { "move", "click", "resign", "draw" })
                    Assert.AreEqual("GAME_OVER", session.Submit(InputPacket.Action(1, action).With("uci", "a2a3")).Code);
                Assert.AreEqual(0, session.LegalCommands(1).Count);
                Assert.AreEqual(0, session.LegalCommands(2).Count);
                Assert.IsTrue(session.RandomCommand(1, 1).IsNull);
            }
        }

        [Test]
        public void StalemateAndEveryOtherDrawEndTheGame()
        {
            using (GameSession session = Start("7k/5K2/8/8/8/8/8/4Q3 w - - 0 1"))
            {
                MoveAll(session, "e1e4");
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(0, session.View.Over.Winner);
                Assert.AreEqual("stalemate", session.View.Over.Reason);
                Assert.AreEqual("1/2-1/2", session.Query("state")["result"].AsString());
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/nb6/K7 w - - 0 1"))
            {
                Assert.IsFalse(session.View.IsOver);
                MoveAll(session, "a1a2");
                Assert.AreEqual("insufficient_material", session.View.Over.Reason);
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/p7/4K3 b - - 99 80"))
            {
                MoveAll(session, "a2a1q");
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(0, session.Query("state")["halfmove"].AsInt());
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/8/R3K3 w Q - 99 80"))
            {
                MoveAll(session, "a1a2");
                Assert.AreEqual("fifty_move_rule", session.View.Over.Reason);
            }
            using (GameSession session = Start())
            {
                string[] shuffle = { "g1f3", "g8f6", "f3g1", "f6g8" };
                MoveAll(session, shuffle);
                Assert.IsFalse(session.View.IsOver);
                MoveAll(session, shuffle[0], shuffle[1], shuffle[2]);
                Assert.IsFalse(session.View.IsOver);
                MoveAll(session, shuffle[3]);
                Assert.AreEqual("repetition", session.View.Over.Reason);
                Assert.AreEqual(0, session.View.Over.Winner);
            }
        }

        [Test]
        public void PositionsThatAreOverAtTheStartEndTheGameAtOnce()
        {
            using (GameSession session = Start("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1"))
                Assert.AreEqual("stalemate", session.View.Over.Reason);
            using (GameSession session = Start("R5k1/5ppp/8/8/8/8/8/6K1 b - - 0 1"))
            {
                Assert.AreEqual("checkmate", session.View.Over.Reason);
                Assert.AreEqual(1, session.View.Over.Winner);
            }
            using (GameSession session = Start("4kb2/8/8/8/8/8/8/2B1K3 w - - 0 1"))
                Assert.AreEqual("insufficient_material", session.View.Over.Reason);
            using (GameSession session = Start("4kb2/8/8/8/8/8/8/3BK3 w - - 0 1"))
                Assert.IsFalse(session.View.IsOver);
        }

        [Test]
        public void TheDrawRulesFollowTheOptions()
        {
            string[] shuffle = { "g1f3", "g8f6", "f3g1", "f6g8" };
            using (GameSession session = Start(null, "{\"repetition\":0}"))
            {
                MoveAll(session, shuffle);
                MoveAll(session, shuffle);
                MoveAll(session, shuffle);
                Assert.IsFalse(session.View.IsOver);
            }
            using (GameSession session = Start(null, "{\"repetition\":2}"))
            {
                MoveAll(session, shuffle);
                Assert.AreEqual("repetition", session.View.Over.Reason);
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/8/R3K3 w Q - 99 80", "{\"halfmove_limit\":150}"))
            {
                MoveAll(session, "a1a2");
                Assert.IsFalse(session.View.IsOver);
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/8/R3K3 w Q - 99 80", "{\"halfmove_limit\":0}"))
            {
                MoveAll(session, "a1a2");
                Assert.IsFalse(session.View.IsOver);
            }
        }

        [Test]
        public void ResigningAndAgreeingToADraw()
        {
            using (GameSession session = Start())
            {
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "draw")).IsAccepted);
                Assert.AreEqual(2, session.Query("state")["draw_offer"].AsInt());
                Assert.AreEqual("DRAW_ALREADY_OFFERED", session.Submit(InputPacket.Action(2, "draw")).Code);
                MoveAll(session, "e2e4");                       // a move withdraws the offer
                Assert.IsFalse(session.Query("state").Has("draw_offer"));
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "draw")).IsAccepted);
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "draw")).IsAccepted);
                Assert.AreEqual("agreement", session.View.Over.Reason);
                Assert.AreEqual(0, session.View.Over.Winner);
            }
            using (GameSession session = Start())
            {
                Click(session, "e2");
                Assert.IsTrue(Hints(session) > 0);
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "resign")).IsAccepted);
                Assert.AreEqual(2, session.View.Over.Winner);
                Assert.AreEqual("resignation", session.View.Over.Reason);
                Assert.AreEqual(0, Hints(session));
            }
            using (GameSession session = Start())
            {
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "resign")).IsAccepted);
                Assert.AreEqual(1, session.View.Over.Winner);
            }
        }

        [Test]
        public void ClicksSelectAPieceShowItsTargetsAndMoveIt()
        {
            using (GameSession session = Start())
            {
                SubmitResult selected = Click(session, "e2");
                Assert.IsTrue(selected.IsAccepted);
                Assert.AreEqual(2, Hints(session));
                Assert.AreEqual(2, CountEvents(selected.Events, "spawn"));
                Assert.AreEqual(Selected, ColorAt(session, "e2"));

                // the same piece again lets go of it, another own piece takes over
                Assert.IsTrue(Click(session, "e2").IsAccepted);
                Assert.AreEqual(0, Hints(session));
                Assert.AreEqual(White, ColorAt(session, "e2"));
                Click(session, "e2");
                Click(session, "g1");
                Assert.AreEqual(2, Hints(session));
                Assert.AreEqual(White, ColorAt(session, "e2"));
                Assert.AreEqual(Selected, ColorAt(session, "g1"));

                // a target that is not legal keeps the selection
                Assert.AreEqual("ILLEGAL_MOVE", Click(session, "g3").Code);
                Assert.AreEqual(Selected, ColorAt(session, "g1"));

                Assert.IsTrue(Click(session, "f3").IsAccepted);
                Assert.AreEqual(0, Hints(session));
                Assert.IsNull(PieceAt(session, "g1"));
                Assert.AreEqual("knight", PieceAt(session, "f3").Kind);
                Assert.AreEqual(White, ColorAt(session, "f3"));
                Assert.AreEqual(2, session.View.Turn);
                Assert.AreEqual("Nf3", Moves(session));
            }
        }

        [Test]
        public void PiecesThatCanBeTakenAreColouredWhileAPieceIsSelected()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4", "d7d5");
                Click(session, "e4");
                Assert.AreEqual(1, Hints(session));                 // e5; the capture on d5 is shown by colour
                Assert.AreEqual("#a3261c", ColorAt(session, "d5"));
                Assert.IsTrue(Click(session, "d5").IsAccepted);
                Assert.AreEqual("pawn", PieceAt(session, "d5").Kind);
                Assert.AreEqual(1, PieceAt(session, "d5").Owner);
                Assert.AreEqual(15, CountOwned(session, 2));
                Assert.AreEqual(0, Hints(session));

                // black's queen could take it back; deselecting restores the colour
                Click(session, "d8");
                Assert.AreEqual(WhiteTarget, ColorAt(session, "d5"));
                Click(session, "d8");
                Assert.AreEqual(White, ColorAt(session, "d5"));
            }
        }

        private static int CountOwned(GameSession session, int owner)
        {
            int count = 0;
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Owner == owner)
                    count++;
            }
            return count;
        }

        [Test]
        public void AKingThatCannotMoveCannotBeSelectedButACheckedKingThatCanIsShownRed()
        {
            using (GameSession session = Start("4k3/8/8/8/8/8/4R3/4K3 b - - 0 1"))
            {
                Assert.AreEqual(BlackCheck, ColorAt(session, "e8"));
                Click(session, "e8");
                Assert.AreEqual(Selected, ColorAt(session, "e8"));
                Click(session, "e8");
                Assert.AreEqual(BlackCheck, ColorAt(session, "e8"));
            }
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4", "f7f5", "d1h5");
                Assert.AreEqual("NO_MOVES", Click(session, "e8").Code);
            }
        }

        [Test]
        public void ClicksPromoteToTheChosenOptionPiece()
        {
            using (GameSession session = Start("8/P6k/8/8/8/8/8/K7 w - - 0 1"))
            {
                Click(session, "a7");
                Assert.AreEqual(1, Hints(session));
                Assert.IsTrue(Click(session, "a8").IsAccepted);
                Assert.AreEqual("queen", PieceAt(session, "a8").Kind);
                Assert.AreEqual(0, Hints(session));
            }
            using (GameSession session = Start("8/P6k/8/8/8/8/8/K7 w - - 0 1", "{\"promotion\":\"n\"}"))
            {
                Click(session, "a7");
                Click(session, "a8");
                Assert.AreEqual("knight", PieceAt(session, "a8").Kind);
                Assert.AreEqual("a8=N", Moves(session));
            }
        }

        [Test]
        public void ASelectionIsPartOfTheHistoryAndComesBackWithUndo()
        {
            using (GameSession session = Start())
            {
                Click(session, "e2");
                string selected = Describe(session.View);
                Click(session, "e4");
                Assert.AreEqual(3, session.HistoryCount);

                Assert.IsTrue(session.Undo());
                Assert.AreEqual(selected, Describe(session.View));
                Assert.AreEqual(2, Hints(session));
                Assert.AreEqual(Selected, ColorAt(session, "e2"));
                Assert.IsTrue(Click(session, "e4").IsAccepted);
                Assert.AreEqual("e4", Moves(session));

                Assert.IsTrue(session.JumpTo(0));
                Assert.AreEqual(32, session.View.ObjectCount);
                Assert.AreEqual(StartFen, Fen(session));
                Assert.AreEqual(0, Hints(session));
            }
        }

        [Test]
        public void SnapshotsRestoreTheGameExactly()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4", "e7e5", "g1f3", "b8c6");
                Click(session, "f1");
                string snapshot = session.CaptureSnapshot();
                string viewBefore = Describe(session.View);
                string fenBefore = Fen(session);
                string movesBefore = Moves(session);

                MoveAll(session, "f1c4", "g8f6");
                Assert.AreNotEqual(viewBefore, Describe(session.View));

                session.LoadSnapshot(snapshot);
                Assert.AreEqual(viewBefore, Describe(session.View));
                Assert.AreEqual(fenBefore, Fen(session));
                Assert.AreEqual(movesBefore, Moves(session));
                Assert.AreEqual(1, session.View.Turn);
                MoveAll(session, "f1c4");
                Assert.AreEqual("e4 e5 Nf3 Nc6 Bc4", Moves(session));
            }

            using (GameSession session = Start())
            {
                MoveAll(session, "f2f3", "e7e5", "g2g4", "d8h4");
                string snapshot = session.CaptureSnapshot();
                session.NewGame();
                Assert.IsFalse(session.View.IsOver);
                session.LoadSnapshot(snapshot);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(2, session.View.Over.Winner);
                Assert.AreEqual("GAME_OVER", Move(session, "a2a3").Code);
                Assert.AreEqual("1. f3 e5 2. g4 Qh4# 0-1", session.Query("pgn").AsString());
            }
        }

        [Test]
        public void SnapshotsKeepTheLastMoveAndTheRepetitionCount()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1");
                string snapshot = session.CaptureSnapshot();
                ViewEvent last = session.View.GetCustom("last_move");
                session.NewGame();
                session.LoadSnapshot(snapshot);
                Assert.AreEqual(last.Raw.ToJson(), session.View.GetCustom("last_move").Raw.ToJson());
                MoveAll(session, "f6g8");
                Assert.AreEqual("repetition", session.View.Over.Reason);
            }
        }

        [Test]
        public void CorruptSnapshotsAreRefusedAndTheGameSurvives()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4");
                string before = Describe(session.View);
                JsonValue good = JsonValue.Parse(session.CaptureSnapshot());
                string[] broken =
                {
                    "{\"v\":1,\"view\":[],\"mod\":{}}",
                    "{\"v\":1,\"view\":[],\"mod\":{\"fen\":\"garbage\",\"start_fen\":\"x\",\"ids\":[],\"hints\":[],\"rep\":[],\"moves\":[]}}",
                    Mutate(good, "ids", "[1,2,3]"),
                    Mutate(good, "ids", "\"x\""),
                    Mutate(good, "rep", "[1]"),
                    Mutate(good, "moves", "{\"a\":1}"),
                    Mutate(good, "hints", "[\"z\"]"),
                };
                foreach (string snapshot in broken)
                    Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot(snapshot)).Code, snapshot);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(before, Describe(session.View));
                Assert.AreEqual("e4", Moves(session));
                MoveAll(session, "e7e5");
                Assert.AreEqual("e4 e5", Moves(session));
            }
        }

        private static string Mutate(JsonValue snapshot, string key, string json)
        {
            JsonValue copy = snapshot.DeepClone();
            copy["mod"].Set(key, JsonValue.Parse(json));
            return copy.ToJson();
        }

        [TestCase(StartFen, 20)]
        [TestCase(Kiwipete, 48)]
        [TestCase("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1", 24)]
        public void EveryLegalCommandIsAccepted(string fen, int count)
        {
            using (GameSession session = Start(fen))
            {
                int seat = session.View.Turn;
                IReadOnlyList<JsonValue> moves = session.LegalCommands(seat);
                Assert.AreEqual(count, moves.Count);
                Assert.AreEqual(count, session.Query("state")["legal_moves"].AsInt());
                Assert.AreEqual(0, session.LegalCommands(3 - seat).Count);
                string snapshot = session.CaptureSnapshot();
                foreach (JsonValue move in moves)
                {
                    Assert.IsTrue(session.Submit(new InputPacket(seat, move)).IsAccepted, move.ToJson());
                    session.LoadSnapshot(snapshot);
                }
            }
        }

        [Test]
        public void RandomCommandsFollowTheSeedAndAreLegal()
        {
            using (GameSession session = Start())
            {
                string a = session.RandomCommand(1, 42).ToJson();
                Assert.AreEqual(a, session.RandomCommand(1, 42).ToJson());
                HashSet<string> distinct = new HashSet<string>();
                for (int seed = 1; seed <= 40; seed++)
                    distinct.Add(session.RandomCommand(1, seed).ToJson());
                Assert.Greater(distinct.Count, 5);
                Assert.IsTrue(session.RandomCommand(2, 1).IsNull);
                Assert.IsTrue(session.Submit(new InputPacket(1, session.RandomCommand(1, 7))).IsAccepted);
            }
        }

        [Test]
        public void SimulatedGamesAllEndAndAddUp()
        {
            SimulationOptions options = new SimulationOptions { Games = 60, Seed = 11 };
            SimulationResult result = SimulationRunner.Run(LoadChess(), options);
            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(60, result.Games);
            Assert.AreEqual(0, result.Rejected);
            Assert.AreEqual(0, result.Aborted);
            int decided = 0;
            foreach (int wins in result.Wins.Values)
                decided += wins;
            Assert.AreEqual(60, decided + result.Draws);
            Assert.Greater(result.Draws, 0);
            Assert.Greater(result.AverageMoves, 20);

            SimulationResult again = SimulationRunner.Run(LoadChess(), options);
            Assert.AreEqual(result.Moves, again.Moves);
            Assert.AreEqual(result.Draws, again.Draws);
        }

        [Test]
        public void SimulationsStartFromACustomPosition()
        {
            SimulationOptions options = new SimulationOptions { Games = 20, Seed = 3, GameOptions = JsonValue.Parse("{\"fen\":\"4k3/8/8/8/8/8/PPP5/4K3 w - - 0 1\"}") };
            SimulationResult result = SimulationRunner.Run(LoadChess(), options);
            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(0, result.Rejected);

            // a position that is over at once still counts as a game
            options = new SimulationOptions { Games = 4, GameOptions = JsonValue.Parse("{\"fen\":\"7k/5Q2/6K1/8/8/8/8/8 b - - 0 1\"}") };
            result = SimulationRunner.Run(LoadChess(), options);
            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(4, result.Draws);
            Assert.AreEqual(0, result.Moves);
        }

        [Test]
        public void QueriesDescribeTheGame()
        {
            using (GameSession session = Start())
            {
                JsonValue state = session.Query("state");
                Assert.AreEqual(1, state["turn"].AsInt());
                Assert.AreEqual(20, state["legal_moves"].AsInt());
                Assert.IsFalse(state["in_check"].AsBool());
                Assert.AreEqual("*", state["result"].AsString());
                Assert.AreEqual(StartFen, state["fen"].AsString());

                List<string> legal = LegalMoves(session);
                Assert.AreEqual(20, legal.Count);
                Assert.IsTrue(legal.Contains("Nf3"));
                Assert.IsTrue(legal.Contains("e4"));

                JsonValue board = session.Query("board");
                Assert.AreEqual(9, board.Count);
                Assert.AreEqual("8  r n b q k b n r", board[0].AsString());
                Assert.AreEqual("1  R N B Q K B N R", board[7].AsString());
                Assert.AreEqual("   a b c d e f g h", board[8].AsString());
                Assert.IsTrue(session.Query("nothing").IsNull);
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/4P3/4K3 b - - 0 7"))
            {
                MoveAll(session, "e8e7", "e2e4", "e7e6");
                Assert.AreEqual("7... Ke7 8. e4 Ke6 *", session.Query("pgn").AsString());
            }
        }

        [Test]
        public void SanNamesThePieceBySquareOnlyWhenTwoCanGoThere()
        {
            using (GameSession session = Start("4k3/8/8/8/8/8/4K3/R6R w - - 0 1"))
            {
                List<string> legal = LegalMoves(session);
                Assert.IsTrue(legal.Contains("Rad1"));
                Assert.IsTrue(legal.Contains("Rhd1"));
                Assert.IsFalse(legal.Contains("Rd1"));
            }
            using (GameSession session = Start("4k3/8/8/R7/8/8/8/R3K3 w - - 0 1"))
            {
                List<string> legal = LegalMoves(session);
                Assert.IsTrue(legal.Contains("R5a3"));
                Assert.IsTrue(legal.Contains("R1a3"));
            }
            using (GameSession session = Start("7k/8/8/1Q1Q4/8/1Q6/8/4K3 w - - 0 1"))
            {
                List<string> legal = LegalMoves(session);
                Assert.IsTrue(legal.Contains("Qb5c4"));
                Assert.IsTrue(legal.Contains("Qdc4"));
                Assert.IsTrue(legal.Contains("Q3c4"));
            }
            using (GameSession session = Start("4k3/8/8/3p4/8/2N1N3/8/4K3 w - - 0 1"))
            {
                List<string> legal = LegalMoves(session);
                Assert.IsTrue(legal.Contains("Ncxd5"));
                Assert.IsTrue(legal.Contains("Nexd5"));
            }
        }

        [Test]
        public void InvalidStartPositionsAreRefusedAndKeepTheRunningGame()
        {
            using (GameSession session = Start())
            {
                MoveAll(session, "e2e4");
                string[] bad =
                {
                    "not a fen",
                    "8/8/8/8/8/8/8/8 w - - 0 1",
                    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBN w KQkq - 0 1",
                    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR x KQkq - 0 1",
                    "P3k3/8/8/8/8/8/8/4K3 w - - 0 1",
                    "4k3/8/8/8/8/8/4q3/4K3 b - - 0 1",
                    "4k3/8/8/8/8/8/8/4K3 w - - 0 0",
                    "9/8/8/8/8/8/8/8 w - - 0 1",
                };
                foreach (string fen in bad)
                {
                    GameSessionException e = Assert.Throws<GameSessionException>(() => session.NewGame(JsonValue.NewObject().Set("fen", fen)), fen);
                    Assert.AreEqual("BAD_OPTION", e.Code, fen);
                }
                Assert.AreEqual("e4", Moves(session));
                Assert.IsTrue(session.IsStarted);
            }
            using (GameSession session = Start("4k3/8/8/8/8/8/8/4K2R w KQ - 0 1"))
                Assert.AreEqual("4k3/8/8/8/8/8/8/4K2R w K - 0 1", Fen(session));
            using (GameSession session = Start("4k3/8/8/8/8/8/8/4K3 w - - 0 1", "{}"))
                Assert.IsTrue(session.View.IsOver);
            using (GameSession session = Start(""))
                Assert.AreEqual(StartFen, Fen(session));
        }

        [Test]
        public void TheConsoleCanPlayChess()
        {
            using (GameSession session = new GameSession(LoadChess()))
            {
                CommandConsole console = new CommandConsole(session);
                StringAssert.StartsWith("new game started", console.Execute("new"));
                StringAssert.StartsWith("accepted", console.Execute("play move from=e2 to=e4"));
                StringAssert.StartsWith("accepted", console.Execute("play move san=e5"));
                StringAssert.StartsWith("accepted", console.Execute("play move uci=g1f3"));
                StringAssert.StartsWith("rejected NOT_YOUR_TURN", console.Execute("play 1 move uci=a2a3"));
                StringAssert.StartsWith("rejected ILLEGAL_MOVE", console.Execute("play move uci=e1e3"));
                StringAssert.Contains("Nc6", console.Execute("play move uci=b8c6"));
                StringAssert.Contains("\"fen\"", console.Execute("query state"));
                StringAssert.Contains("r . b q k b n r", console.Execute("query board"));
                StringAssert.StartsWith("rejected ILLEGAL_MOVE", console.Execute("play move uci=e1e3"));
                StringAssert.StartsWith("new game started", console.Execute("new fen=\"4k3/P7/8/8/8/8/8/4K3 w - - 0 1\""));
                StringAssert.Contains("a8=Q", console.Execute("play move uci=a7a8q"));
                Assert.AreEqual("queen", PieceAt(session, "a8").Kind);
            }
        }

        [Test]
        public void ACommandIsFarInsideTheDefaultTimeLimit()
        {
            // the position with the most known legal moves (218)
            using (GameSession session = Start("R6R/3Q4/1Q4Q1/4Q3/2Q4Q/Q4Q2/pp1Q4/kBNN1KB1 w - - 0 1"))
            {
                Assert.AreEqual(218, session.Query("state")["legal_moves"].AsInt());
                Assert.AreEqual(218, session.LegalCommands(1).Count);
                Assert.IsTrue(session.Submit(new InputPacket(1, session.LegalCommands(1)[0])).IsAccepted);
                Assert.IsFalse(session.VmStats.Faulted);
                Assert.Less(session.VmStats.LastCallMicros, 100000);
            }
        }
    }
}
