using System;
using System.Collections.Generic;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class GomokuTests
    {
        // Coordinates are x, y pairs; the seats alternate starting with the given seat.
        private static SubmitResult PlayAll(GameSession session, int firstSeat, params int[] coordinates)
        {
            SubmitResult last = null;
            for (int i = 0; i < coordinates.Length; i += 2)
                last = TestSupport.PlaceOk(session, (firstSeat - 1 + i / 2) % 2 + 1, coordinates[i], coordinates[i + 1]);
            return last;
        }

        private static string Describe(ViewModel view)
        {
            JsonValue array = JsonValue.NewArray();
            foreach (ViewEvent e in view.Synthesize())
                array.Add(e.Raw);
            return array.ToJson();
        }

        private static ViewEvent Find(IEnumerable<ViewEvent> events, string type)
        {
            foreach (ViewEvent e in events)
            {
                if (e.Type == type)
                    return e;
            }
            return null;
        }

        private static int Count(IEnumerable<ViewEvent> events, string type)
        {
            int count = 0;
            foreach (ViewEvent e in events)
            {
                if (e.Type == type)
                    count++;
            }
            return count;
        }

        private static readonly int[] Horizontal = { 1, 1, 1, 3, 2, 1, 2, 3, 3, 1, 3, 3, 4, 1, 4, 3, 5, 1 };
        private static readonly int[] Vertical = { 1, 1, 3, 1, 1, 2, 3, 2, 1, 3, 3, 3, 1, 4, 3, 4, 1, 5 };
        private static readonly int[] Diagonal = { 1, 1, 2, 1, 2, 2, 3, 1, 3, 3, 4, 1, 4, 4, 5, 1, 5, 5 };
        private static readonly int[] AntiDiagonal = { 5, 1, 1, 1, 4, 2, 2, 1, 3, 3, 3, 1, 2, 4, 4, 1, 1, 5 };

        // Black reaches six in a row on row 1 with its last stone; white's stones never line up.
        private static readonly int[] Overline = { 1, 1, 1, 3, 2, 1, 3, 3, 3, 1, 5, 3, 5, 1, 7, 3, 6, 1, 9, 3, 4, 1 };

        [Test]
        public void NewGameAnnouncesTheBoardAndTheFirstTurn()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                IReadOnlyList<ViewEvent> events = session.NewGame();
                Assert.AreEqual("reset", events[0].Type);
                ViewEvent board = Find(events, "board");
                Assert.IsNotNull(board);
                Assert.AreEqual(15, board.Raw["columns"].AsInt());
                Assert.AreEqual(15, board.Raw["rows"].AsInt());
                Assert.AreEqual(ViewEventKind.Turn, events[events.Count - 1].Kind);
                Assert.AreEqual(1, events[events.Count - 1].Seat);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(0, session.View.ObjectCount);
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(1, session.HistoryCount);
                Assert.AreEqual(0, session.HistoryIndex);
                Assert.IsTrue(session.Capabilities.Snapshot);
                Assert.IsTrue(session.Capabilities.Legal);
                Assert.IsTrue(session.Capabilities.Random);
                Assert.IsTrue(session.Capabilities.Query);
            }
        }

        [Test]
        public void OptionsChangeTheBoardAndAreClamped()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                session.NewGame(TestSupport.Options("{\"size\":9,\"win_length\":4}"));
                JsonValue state = session.Query("state");
                Assert.AreEqual(9, state["size"].AsInt());
                Assert.AreEqual(4, state["win_length"].AsInt());
                Assert.AreEqual(9, session.View.GetCustom("board").Raw["columns"].AsInt());
                Assert.AreEqual(9, session.GameOptions["size"].AsInt());
                Assert.AreEqual(4, session.GameOptions["win_length"].AsInt());
                Assert.IsFalse(session.GameOptions["exact_win"].AsBool(true));

                session.NewGame(TestSupport.Options("{\"size\":100}"));
                Assert.AreEqual(25, session.Query("state")["size"].AsInt());

                session.NewGame(TestSupport.Options("{\"size\":1,\"win_length\":2}"));
                state = session.Query("state");
                Assert.AreEqual(3, state["size"].AsInt());
                Assert.AreEqual(3, state["win_length"].AsInt());

                session.NewGame(TestSupport.Options("{\"size\":6,\"win_length\":50}"));
                state = session.Query("state");
                Assert.AreEqual(6, state["size"].AsInt());
                Assert.AreEqual(6, state["win_length"].AsInt());

                session.NewGame(TestSupport.Options("{\"size\":\"big\",\"exact_win\":true}"));
                state = session.Query("state");
                Assert.AreEqual(15, state["size"].AsInt());
                Assert.IsTrue(state["exact_win"].AsBool());
            }
        }

        [Test]
        public void StonesAlternateBetweenTheSeats()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                SubmitResult first = TestSupport.PlaceOk(session, 1, 8, 8);
                Assert.AreEqual(ViewEventKind.Spawn, first.Events[0].Kind);
                Assert.AreEqual("stone", first.Events[0].ObjectKind);
                Assert.AreEqual(1, first.Events[0].Owner);
                Assert.AreEqual(8, first.Events[0].X);
                Assert.AreEqual(8, first.Events[0].Y);
                Assert.AreEqual("black", first.Events[0].Props["color"].AsString());
                Assert.IsNotNull(Find(first.Events, "last_move"));
                Assert.AreEqual(ViewEventKind.Turn, first.Events[first.Events.Count - 1].Kind);
                Assert.AreEqual(2, first.Events[first.Events.Count - 1].Seat);
                Assert.AreEqual(2, session.View.Turn);

                SubmitResult second = TestSupport.PlaceOk(session, 2, 9, 8);
                Assert.AreEqual("white", second.Events[0].Props["color"].AsString());
                Assert.AreEqual(2, second.Events[0].Owner);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(2, session.View.ObjectCount);
                Assert.AreEqual(9, session.View.GetCustom("last_move").Raw["x"].AsInt());
            }
        }

        [TestCase(2, 8, 8, "NOT_YOUR_TURN")]
        [TestCase(1, 0, 5, "OUT_OF_BOUNDS")]
        [TestCase(1, 16, 5, "OUT_OF_BOUNDS")]
        [TestCase(1, 5, 0, "OUT_OF_BOUNDS")]
        [TestCase(1, 5, 16, "OUT_OF_BOUNDS")]
        [TestCase(1, -3, -3, "OUT_OF_BOUNDS")]
        public void RefusesIllegalPlacementsWithoutChangingTheGame(int seat, int x, int y, string code)
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                long version = session.View.Version;
                int history = session.HistoryCount;
                SubmitResult result = session.Submit(TestSupport.Place(seat, x, y));
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual(code, result.Code);
                Assert.AreEqual(0, result.Events.Count);
                Assert.AreEqual(version, session.View.Version);
                Assert.AreEqual(history, session.HistoryCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        [Test]
        public void RefusesOccupiedPoints()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                TestSupport.PlaceOk(session, 1, 4, 4);
                SubmitResult result = session.Submit(TestSupport.Place(2, 4, 4));
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual("OCCUPIED", result.Code);
                Assert.AreEqual(4, result.Details["x"].AsInt());
                Assert.AreEqual(4, result.Details["y"].AsInt());
                Assert.AreEqual(2, session.View.Turn);
                Assert.AreEqual(1, session.View.ObjectCount);
            }
        }

        [Test]
        public void RefusesMalformedArguments()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(TestSupport.Place(1, 3, 3).With("x", 1.5)).Code);
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(InputPacket.Action(1, "place_stone").With("x", 3)).Code);
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(InputPacket.Action(1, "place_stone").With("x", "3").With("y", "3")).Code);
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(InputPacket.Action(1, "place_stone")).Code);
                Assert.AreEqual("UNKNOWN_ACTION", session.Submit(InputPacket.Action(1, "resign")).Code);
                Assert.AreEqual("UNKNOWN_ACTION", session.Submit(new InputPacket(1, JsonValue.NewObject())).Code);
                Assert.AreEqual(0, session.View.ObjectCount);
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        [Test]
        public void ConnectingFiveInARowWins()
        {
            AssertLineWins(Horizontal, new[] { 1, 2, 3, 4, 5 }, new[] { 1, 1, 1, 1, 1 });
            AssertLineWins(Vertical, new[] { 1, 1, 1, 1, 1 }, new[] { 1, 2, 3, 4, 5 });
            AssertLineWins(Diagonal, new[] { 1, 2, 3, 4, 5 }, new[] { 1, 2, 3, 4, 5 });
            AssertLineWins(AntiDiagonal, new[] { 5, 4, 3, 2, 1 }, new[] { 1, 2, 3, 4, 5 });
        }

        private static void AssertLineWins(int[] sequence, int[] xs, int[] ys)
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                SubmitResult last = PlayAll(session, 1, sequence);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(1, over.Winner);
                Assert.AreEqual("connected", over.Reason);
                Assert.AreEqual(5, over.Cells.Count);
                for (int i = 0; i < 5; i++)
                {
                    bool found = false;
                    foreach (Point2 p in over.Cells)
                        found |= (int)p.X == xs[i] && (int)p.Y == ys[i];
                    Assert.IsTrue(found, "missing cell (" + xs[i] + "," + ys[i] + ")");
                }
                Assert.AreEqual(0, Count(last.Events, "turn"));
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);

                SubmitResult after = session.Submit(TestSupport.Place(2, 10, 10));
                Assert.AreEqual(SubmitStatus.Rejected, after.Status);
                Assert.AreEqual("GAME_OVER", after.Code);
                Assert.AreEqual(9, session.View.ObjectCount);
            }
        }

        [Test]
        public void WhiteCanWinToo()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                SubmitResult last = PlayAll(session, 1, 1, 1, 1, 3, 3, 1, 2, 3, 5, 1, 3, 3, 7, 1, 4, 3, 9, 1, 5, 3);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(2, over.Winner);
                Assert.AreEqual(2, session.View.Over.Winner);
            }
        }

        [Test]
        public void SixInARowWinsInFreestyleButNotInExactMode()
        {
            using (GameSession freestyle = TestSupport.StartGomoku())
            {
                SubmitResult last = PlayAll(freestyle, 1, Overline);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(1, over.Winner);
                Assert.AreEqual(6, over.Cells.Count);
            }

            using (GameSession exact = TestSupport.StartGomoku(TestSupport.Options("{\"exact_win\":true}")))
            {
                SubmitResult last = PlayAll(exact, 1, Overline);
                Assert.IsNull(Find(last.Events, "over"));
                Assert.IsFalse(exact.View.IsOver);
                Assert.AreEqual(2, exact.View.Turn);
            }
        }

        [Test]
        public void ExactModeStillAcceptsExactlyFive()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"exact_win\":true}")))
            {
                SubmitResult last = PlayAll(session, 1, Horizontal);
                Assert.IsNotNull(Find(last.Events, "over"));
                Assert.AreEqual(1, session.View.Over.Winner);
            }
        }

        [Test]
        public void ShorterWinLengthsAreHonored()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"win_length\":3}")))
            {
                SubmitResult last = PlayAll(session, 1, 1, 1, 1, 3, 2, 1, 2, 3, 3, 1);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(3, over.Cells.Count);
            }
        }

        [Test]
        public void ARunOutOfSightOfTheLastStoneDoesNotCount()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                SubmitResult last = PlayAll(session, 1, 1, 1, 1, 3, 2, 1, 2, 3, 3, 1, 3, 3, 4, 1, 4, 3);
                Assert.IsNull(Find(last.Events, "over"));
                Assert.IsFalse(session.View.IsOver);
            }
        }

        [Test]
        public void AFullBoardWithoutALineIsADraw()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                SubmitResult last = PlayAll(session, 1, 1, 1, 2, 1, 3, 1, 2, 2, 1, 2, 3, 2, 2, 3, 1, 3, 3, 3);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(0, over.Winner);
                Assert.AreEqual("draw", over.Reason);
                Assert.AreEqual(0, over.Cells.Count);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(0, session.View.Over.Winner);
                Assert.AreEqual(9, session.View.ObjectCount);
                Assert.AreEqual("GAME_OVER", session.Submit(TestSupport.Place(1, 1, 1)).Code);
            }
        }

        [Test]
        public void SnapshotsRestoreTheGameExactly()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                PlayAll(session, 1, 8, 8, 9, 8, 8, 9);
                string snapshot = session.CaptureSnapshot();
                JsonValue parsed = JsonValue.Parse(snapshot);
                Assert.AreEqual(1, parsed["v"].AsInt());

                string viewBefore = Describe(session.View);
                string stateBefore = session.Query("state").ToJson();

                PlayAll(session, 2, 9, 9, 10, 10, 7, 7);
                string viewAfter = Describe(session.View);
                string stateAfter = session.Query("state").ToJson();
                Assert.AreNotEqual(viewBefore, viewAfter);

                session.LoadSnapshot(snapshot);
                Assert.AreEqual(viewBefore, Describe(session.View));
                Assert.AreEqual(stateBefore, session.Query("state").ToJson());
                Assert.AreEqual(2, session.View.Turn);

                PlayAll(session, 2, 9, 9, 10, 10, 7, 7);
                Assert.AreEqual(viewAfter, Describe(session.View));
                Assert.AreEqual(stateAfter, session.Query("state").ToJson());
            }
        }

        [Test]
        public void SnapshotsCarryTheFinishedState()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                PlayAll(session, 1, Horizontal);
                string snapshot = session.CaptureSnapshot();
                session.NewGame();
                Assert.IsFalse(session.View.IsOver);

                session.LoadSnapshot(snapshot);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual(5, session.View.Over.Cells.Count);
                Assert.AreEqual("GAME_OVER", session.Submit(TestSupport.Place(2, 9, 9)).Code);
            }
        }

        [Test]
        public void CorruptSnapshotsAreRefusedAndTheGameSurvives()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                TestSupport.PlaceOk(session, 1, 8, 8);
                string before = Describe(session.View);

                Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot("not json")).Code);
                Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot("{\"v\":2}")).Code);
                Assert.AreEqual("BAD_SNAPSHOT",
                    Assert.Throws<GameSessionException>(() => session.LoadSnapshot("{\"v\":1,\"view\":[],\"mod\":{\"size\":3,\"board\":\"12\"}}")).Code);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(before, Describe(session.View));
                Assert.AreEqual(15, session.Query("state")["size"].AsInt());
                Assert.AreEqual(1, session.Query("state")["moves"].AsInt());
                Assert.AreEqual(2, session.HistoryCount);

                TestSupport.PlaceOk(session, 2, 9, 9);
                Assert.AreEqual(2, session.View.ObjectCount);
                Assert.AreNotEqual(before, Describe(session.View));
            }
        }

        [Test]
        public void CorruptSnapshotsDoNotHurtASessionWithoutHistory()
        {
            SessionOptions options = new SessionOptions { HistoryLimit = 0 };
            using (GameSession session = new GameSession(TestSupport.LoadGomoku(), options))
            {
                session.NewGame();
                TestSupport.PlaceOk(session, 1, 8, 8);
                string before = Describe(session.View);

                Assert.Throws<GameSessionException>(() => session.LoadSnapshot("{\"v\":1,\"view\":[],\"mod\":{\"size\":3,\"board\":\"12\"}}"));

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(before, Describe(session.View));
                Assert.AreEqual(1, session.Query("state")["moves"].AsInt());
                TestSupport.PlaceOk(session, 2, 9, 9);
            }
        }

        [Test]
        public void UndoRedoAndJumpWalkTheHistory()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                Assert.IsFalse(session.CanUndo);
                Assert.IsFalse(session.CanRedo);
                PlayAll(session, 1, 8, 8, 9, 8, 8, 9);
                Assert.AreEqual(4, session.HistoryCount);
                Assert.AreEqual(3, session.HistoryIndex);
                Assert.IsTrue(session.CanUndo);
                Assert.IsFalse(session.CanRedo);

                Assert.IsTrue(session.Undo());
                Assert.AreEqual(2, session.HistoryIndex);
                Assert.AreEqual(2, session.View.ObjectCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.IsTrue(session.CanRedo);

                Assert.IsTrue(session.Redo());
                Assert.AreEqual(3, session.View.ObjectCount);
                Assert.AreEqual(2, session.View.Turn);

                Assert.IsTrue(session.Undo(3));
                Assert.AreEqual(0, session.HistoryIndex);
                Assert.AreEqual(0, session.View.ObjectCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.IsFalse(session.Undo());

                Assert.IsTrue(session.JumpTo(2));
                Assert.AreEqual(2, session.View.ObjectCount);
                Assert.IsFalse(session.JumpTo(4));
                Assert.IsFalse(session.JumpTo(-1));
                Assert.IsFalse(session.Redo(5));
                Assert.AreEqual(2, session.HistoryIndex);
            }
        }

        [Test]
        public void PlayingAfterUndoDropsTheAbandonedBranch()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                PlayAll(session, 1, 8, 8, 9, 8, 8, 9);
                session.Undo(2);
                Assert.AreEqual(1, session.HistoryIndex);

                TestSupport.PlaceOk(session, 2, 1, 1);
                Assert.AreEqual(3, session.HistoryCount);
                Assert.AreEqual(2, session.HistoryIndex);
                Assert.IsFalse(session.CanRedo);
                Assert.AreEqual(2, session.View.ObjectCount);
                Assert.IsNull(session.GetHistoryPacket(0));
                Assert.AreEqual(8, session.GetHistoryPacket(1).Command["x"].AsInt());
                Assert.AreEqual(1, session.GetHistoryPacket(2).Command["x"].AsInt());
            }
        }

        [Test]
        public void HistoryIsBoundedByTheLimit()
        {
            SessionOptions options = new SessionOptions { HistoryLimit = 3 };
            using (GameSession session = new GameSession(TestSupport.LoadGomoku(), options))
            {
                session.NewGame();
                PlayAll(session, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5);
                Assert.AreEqual(3, session.HistoryCount);
                Assert.AreEqual(2, session.HistoryIndex);
                Assert.IsTrue(session.Undo(2));
                Assert.AreEqual(3, session.View.ObjectCount);
                Assert.IsFalse(session.Undo());
            }
        }

        [Test]
        public void HistoryCanBeSwitchedOff()
        {
            SessionOptions options = new SessionOptions { HistoryLimit = 0 };
            using (GameSession session = new GameSession(TestSupport.LoadGomoku(), options))
            {
                session.NewGame();
                PlayAll(session, 1, 1, 1, 2, 2);
                Assert.IsFalse(session.HistoryEnabled);
                Assert.AreEqual(0, session.HistoryCount);
                Assert.IsFalse(session.CanUndo);
                Assert.IsFalse(session.Undo());
                Assert.AreEqual(2, session.View.ObjectCount);
            }
        }

        [Test]
        public void LegalCommandsListEveryEmptyPointForTheSeatOnTurn()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                Assert.AreEqual(225, session.LegalCommands(1).Count);
                Assert.AreEqual(0, session.LegalCommands(2).Count);
                TestSupport.PlaceOk(session, 1, 8, 8);
                Assert.AreEqual(0, session.LegalCommands(1).Count);
                IReadOnlyList<JsonValue> moves = session.LegalCommands(2);
                Assert.AreEqual(224, moves.Count);
                foreach (JsonValue move in moves)
                {
                    Assert.AreEqual("place_stone", move["action"].AsString());
                    Assert.IsFalse(move["x"].AsInt() == 8 && move["y"].AsInt() == 8);
                }
            }
        }

        [Test]
        public void EveryLegalCommandIsAccepted()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":5,\"win_length\":5}")))
            {
                IReadOnlyList<JsonValue> moves = session.LegalCommands(1);
                Assert.AreEqual(25, moves.Count);
                string snapshot = session.CaptureSnapshot();
                foreach (JsonValue move in moves)
                {
                    Assert.IsTrue(session.Submit(new InputPacket(1, move)).IsAccepted, move.ToJson());
                    session.LoadSnapshot(snapshot);
                }
            }
        }

        [Test]
        public void RandomCommandsFollowTheSeed()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                string a = session.RandomCommand(1, 42).ToJson();
                string b = session.RandomCommand(1, 42).ToJson();
                Assert.AreEqual(a, b);

                HashSet<string> distinct = new HashSet<string>();
                for (int seed = 1; seed <= 30; seed++)
                    distinct.Add(session.RandomCommand(1, seed).ToJson());
                Assert.Greater(distinct.Count, 5);

                JsonValue command = session.RandomCommand(1, 7);
                Assert.AreEqual("place_stone", command["action"].AsString());
                Assert.IsTrue(session.Submit(new InputPacket(1, command)).IsAccepted);
            }
        }

        [Test]
        public void RandomCommandIsNullWhenTheBoardIsFull()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                PlayAll(session, 1, 1, 1, 2, 1, 3, 1, 2, 2, 1, 2, 3, 2, 2, 3, 1, 3, 3, 3);
                Assert.IsTrue(session.RandomCommand(1, 1).IsNull);
            }
        }

        [Test]
        public void ViewQueryReturnsTheTable()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                TestSupport.PlaceOk(session, 1, 3, 4);
                JsonValue view = session.Query("view");
                Assert.AreEqual(1, view.Count);
                Assert.AreEqual("stone", view[0]["kind"].AsString());
                Assert.AreEqual(3, view[0]["x"].AsInt());
                Assert.AreEqual(4, view[0]["y"].AsInt());

                JsonValue info = session.Query("session");
                Assert.AreEqual(2, info["turn"].AsInt());

                JsonValue state = session.Query("state");
                Assert.AreEqual(1, state["moves"].AsInt());
                Assert.AreEqual(3, state["last"]["x"].AsInt());
                Assert.AreEqual(225, state["board"].AsString().Length);
            }
        }

        [Test]
        public void EvalNeedsDevMode()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                GameSessionException e = Assert.Throws<GameSessionException>(() => session.Evaluate("1 + 1"));
                Assert.AreEqual("DEV_ONLY", e.Code);
            }

            using (GameSession session = TestSupport.StartGomoku(null, new VmOptions { DevMode = true }))
            {
                Assert.IsTrue(session.DevMode);
                IReadOnlyList<string> values = session.Evaluate("1 + 1");
                Assert.AreEqual(1, values.Count);
                Assert.AreEqual("2", values[0]);

                TestSupport.PlaceOk(session, 1, 2, 2);
                Assert.AreEqual("1", session.Evaluate("rules.snapshot().moves")[0]);
            }
        }

        [Test]
        public void EvalReportsScriptErrors()
        {
            using (GameSession session = TestSupport.StartGomoku(null, new VmOptions { DevMode = true }))
            {
                GameSessionException e = Assert.Throws<GameSessionException>(() => session.Evaluate("1 +"));
                Assert.AreEqual("COMPILE_ERROR", e.Code);
            }
        }

        [Test]
        public void GameSessionReportsNotStartedUntilNewGame()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                Assert.IsFalse(session.IsStarted);
                SubmitResult result = session.Submit(TestSupport.Place(1, 1, 1));
                Assert.AreEqual(SubmitStatus.Invalid, result.Status);
                Assert.AreEqual("NOT_STARTED", result.Code);
                Assert.Throws<GameSessionException>(() => session.Query("state"));
            }
        }

        [Test]
        public void RulesNeverSeeAFaultFromPlayerInput()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                Random random = new Random(12345);
                for (int i = 0; i < 300; i++)
                {
                    JsonValue command = JsonValue.NewObject().Set("action", "place_stone");
                    switch (random.Next(5))
                    {
                        case 0:
                            command.Set("x", random.Next(-5, 25)).Set("y", random.Next(-5, 25));
                            break;
                        case 1:
                            command.Set("x", random.NextDouble() * 30).Set("y", random.NextDouble() * 30);
                            break;
                        case 2:
                            command.Set("x", "a").Set("y", true);
                            break;
                        case 3:
                            command.Set("x", JsonValue.NewObject()).Set("y", JsonValue.NewArray());
                            break;
                        default:
                            command.Set("x", 1e300).Set("y", -1e300);
                            break;
                    }
                    SubmitResult result = session.Submit(new InputPacket(random.Next(0, 4), command));
                    Assert.AreNotEqual(SubmitStatus.Faulted, result.Status, command.ToJson());
                }
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }
    }
}
