using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class SudokuTests
    {
        // A classic puzzle with 30 clues and one solution, so that the right digits are known. 0 is an empty cell.
        private const string Puzzle = "530070000600195000098000060800060003400803001700020006060000280000419005000080079";
        private const string Solved = "534678912672195348198342567859761423426853791713924856961537284287419635345286179";
        private const string Empty = "000000000000000000000000000000000000000000000000000000000000000000000000000000000";

        private static ModPackage LoadSudoku()
        {
            return ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "sudoku"));
        }

        private static GameSession Start(string options)
        {
            GameSession session = new GameSession(LoadSudoku());
            session.NewGame(TestSupport.Options(options));
            return session;
        }

        // The classic puzzle; extra is more options, such as ",\"strict\":true".
        private static GameSession StartClassic(string extra = "")
        {
            return Start("{\"puzzle\":\"" + Puzzle + "\"" + extra + "}");
        }

        private static InputPacket Packet(string action, int x, int y)
        {
            return InputPacket.Action(1, action).With("x", x).With("y", y);
        }

        private static SubmitResult Do(GameSession session, string action, int x, int y)
        {
            return session.Submit(Packet(action, x, y));
        }

        private static SubmitResult Place(GameSession session, int x, int y, int digit)
        {
            return session.Submit(Packet("place", x, y).With("digit", digit));
        }

        private static SubmitResult PlaceOk(GameSession session, int x, int y, int digit)
        {
            SubmitResult result = Place(session, x, y, digit);
            if (!result.IsAccepted)
                throw new InvalidOperationException("expected " + digit + " at (" + x + "," + y + ") to be accepted but got " + result);
            return result;
        }

        private static SubmitResult Submit(GameSession session, string json)
        {
            return session.Submit(new InputPacket(1, JsonValue.Parse(json)));
        }

        // Puts the right digit in the empty cells of the classic puzzle, all but the last `leave` of them.
        private static SubmitResult Complete(GameSession session, int leave = 0)
        {
            List<int> empties = new List<int>();
            for (int i = 0; i < 81; i++)
            {
                if (Puzzle[i] == '0')
                    empties.Add(i);
            }
            SubmitResult last = null;
            for (int k = 0; k < empties.Count - leave; k++)
            {
                int i = empties[k];
                last = PlaceOk(session, i % 9 + 1, i / 9 + 1, Solved[i] - '0');
            }
            return last;
        }

        // Lets the computer player play until the puzzle is solved; returns the commands it played.
        private static List<JsonValue> LetTheComputerPlay(GameSession session)
        {
            List<JsonValue> played = new List<JsonValue>();
            while (!session.View.IsOver)
            {
                JsonValue command = session.RandomCommand(1, played.Count + 1);
                Assert.IsTrue(command.IsObject, "the computer has no command");
                SubmitResult result = session.Submit(new InputPacket(1, command));
                Assert.IsTrue(result.IsAccepted, command.ToJson() + " -> " + result);
                played.Add(command);
                Assert.Less(played.Count, 500, "the computer does not get anywhere");
            }
            return played;
        }

        private static JsonValue State(GameSession session)
        {
            return session.Query("state");
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

        private static int[] Cells(string text)
        {
            int[] cells = new int[81];
            for (int i = 0; i < 81; i++)
                cells[i] = text[i] - '0';
            return cells;
        }

        // A solver of its own, so that what the game says about its puzzles is not checked by the game's own search:
        // the number of solutions of a grid, counted up to limit.
        private static int CountSolutions(string text, int limit)
        {
            int[] cells = Cells(text);
            int[] rows = new int[9];
            int[] cols = new int[9];
            int[] boxes = new int[9];
            for (int i = 0; i < 81; i++)
            {
                if (cells[i] == 0)
                    continue;
                int bit = 1 << (cells[i] - 1);
                int r = i / 9;
                int c = i % 9;
                int b = r / 3 * 3 + c / 3;
                if (((rows[r] | cols[c] | boxes[b]) & bit) != 0)
                    return 0;
                rows[r] |= bit;
                cols[c] |= bit;
                boxes[b] |= bit;
            }
            int count = 0;
            Search(cells, rows, cols, boxes, limit, ref count);
            return count;
        }

        private static void Search(int[] cells, int[] rows, int[] cols, int[] boxes, int limit, ref int count)
        {
            int best = -1;
            int bestMask = 0;
            int bestSize = 10;
            for (int i = 0; i < 81 && bestSize > 1; i++)
            {
                if (cells[i] != 0)
                    continue;
                int mask = ~(rows[i / 9] | cols[i % 9] | boxes[i / 27 * 3 + i % 9 / 3]) & 0x1FF;
                int size = 0;
                for (int m = mask; m != 0; m &= m - 1)
                    size++;
                if (size < bestSize)
                {
                    best = i;
                    bestMask = mask;
                    bestSize = size;
                }
            }
            if (best < 0)
            {
                count++;
                return;
            }

            int r = best / 9;
            int c = best % 9;
            int b = r / 3 * 3 + c / 3;
            for (int digit = 1; digit <= 9 && count < limit; digit++)
            {
                int bit = 1 << (digit - 1);
                if ((bestMask & bit) == 0)
                    continue;
                cells[best] = digit;
                rows[r] |= bit;
                cols[c] |= bit;
                boxes[b] |= bit;
                Search(cells, rows, cols, boxes, limit, ref count);
                cells[best] = 0;
                rows[r] &= ~bit;
                cols[c] &= ~bit;
                boxes[b] &= ~bit;
            }
        }

        private static bool IsFullGrid(string text)
        {
            for (int unit = 0; unit < 9; unit++)
            {
                int row = 0;
                int col = 0;
                int box = 0;
                for (int k = 0; k < 9; k++)
                {
                    row |= 1 << (text[unit * 9 + k] - '1');
                    col |= 1 << (text[k * 9 + unit] - '1');
                    box |= 1 << (text[(unit / 3 * 3 + k / 3) * 9 + unit % 3 * 3 + k % 3] - '1');
                }
                if (row != 0x1FF || col != 0x1FF || box != 0x1FF)
                    return false;
            }
            return true;
        }

        // How many (cell, digit) pairs of the empty cells of a puzzle break no rule of the grid.
        private static int CountFittingDigits(string puzzle)
        {
            int[] cells = Cells(puzzle);
            int total = 0;
            for (int i = 0; i < 81; i++)
            {
                if (cells[i] != 0)
                    continue;
                for (int digit = 1; digit <= 9; digit++)
                {
                    bool fits = true;
                    for (int j = 0; j < 81 && fits; j++)
                    {
                        bool sees = j / 9 == i / 9 || j % 9 == i % 9 || (j / 27 == i / 27 && j % 9 / 3 == i % 9 / 3);
                        if (j != i && sees && cells[j] == digit)
                            fits = false;
                    }
                    if (fits)
                        total++;
                }
            }
            return total;
        }

        // Checks what the game claims about a puzzle it dealt: one solution, the one it reports, and the clues are part of it.
        private static void AssertSoundPuzzle(GameSession session)
        {
            JsonValue state = State(session);
            string puzzle = state["puzzle"].AsString();
            string solution = session.Query("solution").AsString();
            Assert.AreEqual(81, puzzle.Length);
            Assert.AreEqual(1, CountSolutions(puzzle, 2), "the puzzle has to have exactly one solution");
            Assert.IsTrue(IsFullGrid(solution), "the solution is not a valid grid");
            for (int i = 0; i < 81; i++)
                Assert.IsTrue(puzzle[i] == '0' || puzzle[i] == solution[i], "clue " + i + " is not part of the solution");
            int clues = 0;
            foreach (char c in puzzle)
            {
                if (c != '0')
                    clues++;
            }
            Assert.AreEqual(clues, state["clues"].AsInt());
            Assert.AreEqual(clues, session.View.ObjectCount);
            Assert.AreEqual(puzzle, state["board"].AsString());
        }

        [Test]
        public void TheManifestDeclaresASinglePlayerGrid()
        {
            ModManifest manifest = LoadSudoku().Manifest;
            Assert.AreEqual("sudoku", manifest.Id);
            Assert.AreEqual(1, manifest.MinPlayers);
            Assert.AreEqual(1, manifest.MaxPlayers);
            Assert.AreEqual(1, manifest.Seats.Count);
            Assert.AreEqual(36, manifest.DefaultOptions["clues"].AsInt());
            Assert.AreEqual(0, manifest.DefaultOptions["seed"].AsInt(-1));
            Assert.IsFalse(manifest.DefaultOptions["strict"].AsBool(true));
            // A puzzle of your own is typed in the console; the text field of the options panel is too short for 81 digits.
            Assert.AreEqual(3, manifest.DefaultOptions.Count);
            Assert.IsFalse(manifest.DefaultOptions.Has("puzzle"));
            Assert.AreEqual("cycle", manifest.Client["click"]["action"].AsString());
            Assert.AreEqual(9, manifest.Client["board"]["columns"].AsInt());
            Assert.AreEqual(3, manifest.Client["board"]["block"].AsInt());
        }

        [Test]
        public void NewGameAnnouncesTheGridAndTheFirstTurn()
        {
            using (GameSession session = new GameSession(LoadSudoku()))
            {
                IReadOnlyList<ViewEvent> events = session.NewGame(TestSupport.Options("{\"puzzle\":\"" + Puzzle + "\"}"));
                Assert.AreEqual("reset", events[0].Type);
                ViewEvent board = Find(events, "board");
                Assert.IsNotNull(board);
                Assert.AreEqual(9, board.Raw["columns"].AsInt());
                Assert.AreEqual(9, board.Raw["rows"].AsInt());
                Assert.AreEqual(3, board.Raw["block"].AsInt());
                Assert.AreEqual(30, Count(events, "spawn"));
                Assert.AreEqual(ViewEventKind.Turn, events[events.Count - 1].Kind);
                Assert.AreEqual(1, events[events.Count - 1].Seat);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(30, session.View.ObjectCount);
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(1, session.HistoryCount);
                Assert.IsTrue(session.Capabilities.Snapshot);
                Assert.IsTrue(session.Capabilities.Legal);
                Assert.IsTrue(session.Capabilities.Random);
                Assert.IsTrue(session.Capabilities.Query);
            }
        }

        [Test]
        public void AGivenPuzzleShowsItsClues()
        {
            using (GameSession session = StartClassic())
            {
                foreach (ViewObject o in session.View.Objects)
                {
                    int x = (int)o.X;
                    int y = (int)o.Y;
                    char digit = Puzzle[(y - 1) * 9 + x - 1];
                    Assert.AreNotEqual('0', digit);
                    Assert.AreEqual("digit", o.Kind);
                    Assert.AreEqual(digit.ToString(), o.Props["label"].AsString());
                    Assert.IsTrue(o.Props["given"].AsBool());
                    Assert.IsFalse(o.Props["conflict"].AsBool(true));
                    Assert.AreEqual(0, o.Owner);
                }

                JsonValue state = State(session);
                Assert.AreEqual(30, state["clues"].AsInt());
                Assert.AreEqual(30, state["filled"].AsInt());
                Assert.AreEqual(51, state["empty"].AsInt());
                Assert.AreEqual(0, state["conflicts"].AsInt());
                Assert.AreEqual(0, state["moves"].AsInt());
                Assert.AreEqual(0, state["seed"].AsInt(-1));
                Assert.IsTrue(state["unique"].AsBool());
                Assert.IsFalse(state["strict"].AsBool(true));
                Assert.IsFalse(state["solved"].AsBool(true));
                Assert.AreEqual(Puzzle, state["board"].AsString());
                Assert.AreEqual(Puzzle, state["puzzle"].AsString());
                Assert.AreEqual(Solved, session.Query("solution").AsString());
            }
        }

        [Test]
        public void PlacingADigitFillsTheCell()
        {
            using (GameSession session = StartClassic())
            {
                SubmitResult result = PlaceOk(session, 3, 1, 4);
                Assert.AreEqual(1, result.Events.Count);
                ViewEvent spawn = result.Events[0];
                Assert.AreEqual(ViewEventKind.Spawn, spawn.Kind);
                Assert.AreEqual("digit", spawn.ObjectKind);
                Assert.AreEqual(3, (int)spawn.X);
                Assert.AreEqual(1, (int)spawn.Y);
                Assert.AreEqual(1, spawn.Owner);
                Assert.AreEqual("4", spawn.Props["label"].AsString());
                Assert.IsFalse(spawn.Props["given"].AsBool(true));
                Assert.IsFalse(spawn.Props["conflict"].AsBool(true));

                Assert.AreEqual(31, session.View.ObjectCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(2, session.HistoryCount);
                JsonValue state = State(session);
                Assert.AreEqual(31, state["filled"].AsInt());
                Assert.AreEqual(50, state["empty"].AsInt());
                Assert.AreEqual(1, state["moves"].AsInt());
                Assert.AreEqual(30, state["clues"].AsInt());
                Assert.AreEqual('4', state["board"].AsString()[2]);
                Assert.AreEqual('0', state["puzzle"].AsString()[2]);
            }
        }

        [Test]
        public void DigitsAreChangedAndTakenOut()
        {
            using (GameSession session = StartClassic())
            {
                int id = PlaceOk(session, 3, 1, 4).Events[0].Id;

                SubmitResult changed = PlaceOk(session, 3, 1, 2);
                Assert.AreEqual(1, changed.Events.Count);
                Assert.AreEqual(ViewEventKind.Set, changed.Events[0].Kind);
                Assert.AreEqual(id, changed.Events[0].Id);
                Assert.AreEqual("2", session.View.Get(id).Props["label"].AsString());
                Assert.AreEqual(31, session.View.ObjectCount);

                SubmitResult cleared = Do(session, "clear", 3, 1);
                Assert.IsTrue(cleared.IsAccepted);
                Assert.AreEqual(1, cleared.Events.Count);
                Assert.AreEqual(ViewEventKind.Remove, cleared.Events[0].Kind);
                Assert.AreEqual(id, cleared.Events[0].Id);
                Assert.IsNull(session.View.Get(id));
                Assert.AreEqual(30, session.View.ObjectCount);
                Assert.AreEqual(3, State(session)["moves"].AsInt());
                Assert.AreEqual(Puzzle, State(session)["board"].AsString());
            }
        }

        [Test]
        public void AnUnchangedDigitIsRefused()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 4);
                long version = session.View.Version;
                SubmitResult result = Place(session, 3, 1, 4);
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual("UNCHANGED", result.Code);
                Assert.AreEqual(version, session.View.Version);
                Assert.AreEqual(2, session.HistoryCount);
            }
        }

        [TestCase("place", 0, 1, 5, "OUT_OF_BOUNDS")]
        [TestCase("place", 10, 1, 5, "OUT_OF_BOUNDS")]
        [TestCase("place", 3, 0, 5, "OUT_OF_BOUNDS")]
        [TestCase("place", 3, 10, 5, "OUT_OF_BOUNDS")]
        [TestCase("place", -3, -3, 5, "OUT_OF_BOUNDS")]
        [TestCase("cycle", 0, 5, 0, "OUT_OF_BOUNDS")]
        [TestCase("place", 3, 1, 0, "BAD_DIGIT")]
        [TestCase("place", 3, 1, 10, "BAD_DIGIT")]
        [TestCase("place", 3, 1, -1, "BAD_DIGIT")]
        [TestCase("clear", 3, 1, 0, "ALREADY_EMPTY")]
        [TestCase("place", 1, 1, 9, "GIVEN_CELL")]
        [TestCase("clear", 1, 1, 0, "GIVEN_CELL")]
        [TestCase("cycle", 1, 1, 0, "GIVEN_CELL")]
        public void RefusesIllegalCommandsWithoutChangingTheGame(string action, int x, int y, int digit, string code)
        {
            using (GameSession session = StartClassic())
            {
                long version = session.View.Version;
                SubmitResult result = action == "place" ? Place(session, x, y, digit) : Do(session, action, x, y);
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual(code, result.Code);
                Assert.AreEqual(0, result.Events.Count);
                Assert.AreEqual(version, session.View.Version);
                Assert.AreEqual(1, session.HistoryCount);
                Assert.AreEqual(Puzzle, State(session)["board"].AsString());
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        [Test]
        public void RefusesMalformedArguments()
        {
            using (GameSession session = StartClassic())
            {
                string[] invalid =
                {
                    "{\"action\":\"place\",\"x\":3,\"y\":1}",
                    "{\"action\":\"place\",\"x\":3,\"y\":1,\"digit\":2.5}",
                    "{\"action\":\"place\",\"x\":3,\"y\":1,\"digit\":\"4\"}",
                    "{\"action\":\"place\",\"x\":3.5,\"y\":1,\"digit\":4}",
                    "{\"action\":\"place\",\"x\":\"3\",\"y\":1,\"digit\":4}",
                    "{\"action\":\"place\",\"x\":3,\"y\":true,\"digit\":4}",
                    "{\"action\":\"place\",\"y\":1,\"digit\":4}",
                    "{\"action\":\"clear\",\"x\":[3],\"y\":1}",
                    "{\"action\":\"cycle\",\"x\":3,\"y\":{}}"
                };
                foreach (string json in invalid)
                    Assert.AreEqual("INVALID_ARGUMENT", Submit(session, json).Code, json);

                string[] unknown = { "{\"action\":\"resign\"}", "{}", "{\"action\":5}", "{\"action\":[[1]]}", "{\"action\":{\"a\":1}}" };
                foreach (string json in unknown)
                    Assert.AreEqual("UNKNOWN_ACTION", Submit(session, json).Code, json);

                Assert.AreEqual(Puzzle, State(session)["board"].AsString());
                Assert.AreEqual(1, session.HistoryCount);
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        [Test]
        public void ADigitThatClashesIsShownButAllowedByDefault()
        {
            using (GameSession session = StartClassic())
            {
                SubmitResult result = PlaceOk(session, 3, 1, 5);
                Assert.AreEqual(1, result.Events.Count);
                Assert.IsTrue(result.Events[0].Props["conflict"].AsBool());
                Assert.AreEqual("#ff5c5c", result.Events[0].Props["color"].AsString());
                Assert.AreEqual(1, State(session)["conflicts"].AsInt());

                JsonValue conflicts = session.Query("conflicts");
                Assert.AreEqual(1, conflicts.Count);
                Assert.AreEqual(3, conflicts[0]["x"].AsInt());
                Assert.AreEqual(1, conflicts[0]["y"].AsInt());
                Assert.AreEqual(5, conflicts[0]["digit"].AsInt());

                Assert.IsTrue(Do(session, "clear", 3, 1).IsAccepted);
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
                Assert.AreEqual(0, session.Query("conflicts").Count);
            }
        }

        [Test]
        public void TwoClashingEntriesAreBothShown()
        {
            using (GameSession session = StartClassic())
            {
                int first = PlaceOk(session, 3, 1, 1).Events[0].Id;
                Assert.IsFalse(session.View.Get(first).Props["conflict"].AsBool(true));

                SubmitResult second = PlaceOk(session, 4, 1, 1);
                Assert.AreEqual(2, second.Events.Count);
                Assert.AreEqual(ViewEventKind.Spawn, second.Events[0].Kind);
                Assert.IsTrue(second.Events[0].Props["conflict"].AsBool());
                Assert.AreEqual(ViewEventKind.Set, second.Events[1].Kind);
                Assert.AreEqual(first, second.Events[1].Id);
                Assert.IsTrue(session.View.Get(first).Props["conflict"].AsBool());
                Assert.AreEqual("#ff5c5c", session.View.Get(first).Props["color"].AsString());
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());

                SubmitResult cleared = Do(session, "clear", 4, 1);
                Assert.AreEqual(2, cleared.Events.Count);
                Assert.AreEqual(ViewEventKind.Remove, cleared.Events[0].Kind);
                Assert.IsFalse(session.View.Get(first).Props["conflict"].AsBool(true));
                Assert.AreEqual("#4d8fe6", session.View.Get(first).Props["color"].AsString());
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
            }
        }

        [Test]
        public void ClueCellsNeverCountAsTheOnesInConflict()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 5);
                PlaceOk(session, 8, 1, 5);
                foreach (ViewObject o in session.View.Objects)
                {
                    if (o.Props["given"].AsBool())
                        Assert.IsFalse(o.Props["conflict"].AsBool(true), "a clue is marked as a conflict");
                }
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());
            }
        }

        [Test]
        public void StrictModeRefusesDigitsThatAreAlreadyInUse()
        {
            using (GameSession session = StartClassic(",\"strict\":true"))
            {
                Assert.IsTrue(State(session)["strict"].AsBool());
                long version = session.View.Version;
                SubmitResult result = Place(session, 3, 1, 5);
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual("CONFLICT", result.Code);
                Assert.AreEqual(3, result.Details["x"].AsInt());
                Assert.AreEqual(1, result.Details["y"].AsInt());
                Assert.AreEqual(5, result.Details["digit"].AsInt());
                Assert.AreEqual(1, result.Details["with"]["x"].AsInt());
                Assert.AreEqual(1, result.Details["with"]["y"].AsInt());
                Assert.AreEqual(version, session.View.Version);

                Assert.AreEqual("CONFLICT", Place(session, 3, 1, 8).Code);
                Assert.IsTrue(Place(session, 3, 1, 4).IsAccepted);
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
            }
        }

        [Test]
        public void ClicksStepThroughTheDigits()
        {
            using (GameSession session = StartClassic())
            {
                List<string> seen = new List<string>();
                for (int i = 0; i < 11; i++)
                {
                    SubmitResult result = Do(session, "cycle", 3, 1);
                    Assert.IsTrue(result.IsAccepted);
                    ViewEvent e = result.Events[0];
                    seen.Add(e.Kind == ViewEventKind.Remove ? "-" : e.Props["label"].AsString());
                }
                Assert.AreEqual("1,2,3,4,5,6,7,8,9,-,1", string.Join(",", seen));
            }
        }

        [Test]
        public void StrictClicksSkipTheDigitsThatAreInUse()
        {
            using (GameSession session = StartClassic(",\"strict\":true"))
            {
                // Of 1 to 9, only 1, 2 and 4 are not yet in the row, the column or the box of the cell at column 3, row 1.
                List<string> seen = new List<string>();
                for (int i = 0; i < 5; i++)
                {
                    SubmitResult result = Do(session, "cycle", 3, 1);
                    Assert.IsTrue(result.IsAccepted);
                    ViewEvent e = result.Events[0];
                    seen.Add(e.Kind == ViewEventKind.Remove ? "-" : e.Props["label"].AsString());
                }
                Assert.AreEqual("1,2,4,-,1", string.Join(",", seen));
            }
        }

        [Test]
        public void AClickOnACellWithNothingThatFitsIsRefused()
        {
            using (GameSession session = StartClassic(",\"strict\":true"))
            {
                PlaceOk(session, 7, 1, 1);
                PlaceOk(session, 4, 1, 2);
                PlaceOk(session, 6, 1, 4);
                Assert.AreEqual(0, session.Query("candidates", TestSupport.Options("{\"x\":3,\"y\":1}")).Count);

                SubmitResult result = Do(session, "cycle", 3, 1);
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual("NO_CANDIDATE", result.Code);
                Assert.AreEqual(4, session.HistoryCount);
            }
        }

        [Test]
        public void FillingTheGridSolvesIt()
        {
            using (GameSession session = StartClassic())
            {
                SubmitResult last = Complete(session);
                ViewEvent over = Find(last.Events, "over");
                Assert.IsNotNull(over);
                Assert.AreEqual(1, over.Winner);
                Assert.AreEqual("solved", over.Reason);
                Assert.AreEqual(0, over.Cells.Count);
                Assert.AreEqual(0, Count(last.Events, "turn"));
                StringAssert.Contains("51 moves", Find(last.Events, "status").Text);

                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
                JsonValue state = State(session);
                Assert.IsTrue(state["solved"].AsBool());
                Assert.AreEqual(Solved, state["board"].AsString());
                Assert.AreEqual(81, state["filled"].AsInt());
                Assert.AreEqual(0, state["empty"].AsInt());
                Assert.AreEqual(0, state["conflicts"].AsInt());
                Assert.AreEqual(51, state["moves"].AsInt());

                Assert.AreEqual("GAME_OVER", Place(session, 3, 1, 1).Code);
                Assert.AreEqual("GAME_OVER", Do(session, "clear", 3, 1).Code);
                Assert.AreEqual("GAME_OVER", Do(session, "cycle", 3, 1).Code);
            }
        }

        [Test]
        public void AFullGridWithAClashIsNotSolved()
        {
            using (GameSession session = StartClassic())
            {
                Complete(session, 1);
                int last = Puzzle.LastIndexOf('0');
                int x = last % 9 + 1;
                int y = last / 9 + 1;
                int right = Solved[last] - '0';
                int wrong = right % 9 + 1;

                SubmitResult clash = PlaceOk(session, x, y, wrong);
                Assert.IsNull(Find(clash.Events, "over"));
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(81, State(session)["filled"].AsInt());
                Assert.Greater(State(session)["conflicts"].AsInt(), 0);
                Assert.IsFalse(State(session)["solved"].AsBool(true));

                SubmitResult fixedIt = PlaceOk(session, x, y, right);
                Assert.IsNotNull(Find(fixedIt.Events, "over"));
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
            }
        }

        [Test]
        public void TheComputerSolvesThePuzzle()
        {
            using (GameSession session = StartClassic())
            {
                List<JsonValue> played = LetTheComputerPlay(session);
                Assert.AreEqual(51, played.Count);
                foreach (JsonValue command in played)
                    Assert.AreEqual("place", command["action"].AsString());
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual(Solved, State(session)["board"].AsString());
                Assert.IsTrue(session.RandomCommand(1, 1).IsNull);
            }
        }

        [Test]
        public void TheComputerPutsWrongDigitsRight()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 1);
                PlaceOk(session, 4, 1, 2);
                PlaceOk(session, 6, 1, 9);
                List<JsonValue> played = LetTheComputerPlay(session);
                Assert.AreEqual(51, played.Count);
                Assert.AreEqual(Solved, State(session)["board"].AsString());
            }
        }

        [Test]
        public void TheComputerTakesOutADigitThatBlocksTheRightOneInStrictMode()
        {
            using (GameSession session = StartClassic(",\"strict\":true"))
            {
                // The digits of the cells at column 3 and column 8 of row 1 swapped: neither can be put right while the other stays.
                PlaceOk(session, 3, 1, 1);
                PlaceOk(session, 8, 1, 4);
                Assert.AreEqual("CONFLICT", Place(session, 3, 1, 4).Code);

                List<JsonValue> played = LetTheComputerPlay(session);
                Assert.AreEqual(52, played.Count);
                int clears = 0;
                foreach (JsonValue command in played)
                {
                    if (command["action"].AsString() == "clear")
                        clears++;
                }
                Assert.AreEqual(1, clears);
                Assert.AreEqual(Solved, State(session)["board"].AsString());
            }
        }

        [Test]
        public void EveryLegalCommandIsAccepted()
        {
            using (GameSession session = StartClassic())
            {
                IReadOnlyList<JsonValue> moves = session.LegalCommands(1);
                Assert.AreEqual(51 * 9, moves.Count);
                string snapshot = session.CaptureSnapshot();
                foreach (JsonValue move in moves)
                {
                    Assert.IsTrue(session.Submit(new InputPacket(1, move)).IsAccepted, move.ToJson());
                    session.LoadSnapshot(snapshot);
                }
            }
        }

        [Test]
        public void StrictModeListsExactlyTheCommandsItAccepts()
        {
            using (GameSession session = StartClassic(",\"strict\":true"))
            {
                IReadOnlyList<JsonValue> moves = session.LegalCommands(1);
                Assert.AreEqual(CountFittingDigits(Puzzle), moves.Count);
                HashSet<string> legal = new HashSet<string>();
                string snapshot = session.CaptureSnapshot();
                foreach (JsonValue move in moves)
                {
                    legal.Add(move["x"].AsInt() + "," + move["y"].AsInt() + "," + move["digit"].AsInt());
                    Assert.IsTrue(session.Submit(new InputPacket(1, move)).IsAccepted, move.ToJson());
                    session.LoadSnapshot(snapshot);
                }

                for (int i = 0; i < 81; i++)
                {
                    if (Puzzle[i] != '0')
                        continue;
                    for (int digit = 1; digit <= 9; digit++)
                    {
                        int x = i % 9 + 1;
                        int y = i / 9 + 1;
                        if (legal.Contains(x + "," + y + "," + digit))
                            continue;
                        SubmitResult result = Place(session, x, y, digit);
                        Assert.AreEqual(SubmitStatus.Rejected, result.Status, x + "," + y + "," + digit);
                        Assert.AreEqual("CONFLICT", result.Code);
                    }
                }
            }
        }

        [Test]
        public void ClearsAreLegalWhereThePlayerHasEnteredADigit()
        {
            using (GameSession session = StartClassic())
            {
                foreach (JsonValue move in session.LegalCommands(1))
                    Assert.AreEqual("place", move["action"].AsString());

                PlaceOk(session, 3, 1, 4);
                int clears = 0;
                foreach (JsonValue move in session.LegalCommands(1))
                {
                    int x = move["x"].AsInt();
                    int y = move["y"].AsInt();
                    if (move["action"].AsString() == "clear")
                    {
                        clears++;
                        Assert.AreEqual(3, x);
                        Assert.AreEqual(1, y);
                    }
                    Assert.IsFalse(x == 3 && y == 1 && move["digit"].AsInt() == 4, "the digit that is there is not a move");
                    Assert.IsFalse(Puzzle[(y - 1) * 9 + x - 1] != '0', "a clue is not a move");
                }
                Assert.AreEqual(1, clears);
            }
        }

        [Test]
        public void NothingIsLegalOnceThePuzzleIsSolvedOrForAnotherSeat()
        {
            using (GameSession session = StartClassic())
            {
                Assert.AreEqual(0, session.LegalCommands(2).Count);
                Complete(session);
                Assert.AreEqual(0, session.LegalCommands(1).Count);
            }
        }

        [Test]
        public void QueriesDescribeTheGame()
        {
            using (GameSession session = StartClassic())
            {
                JsonValue candidates = session.Query("candidates", TestSupport.Options("{\"x\":3,\"y\":1}"));
                Assert.AreEqual("[1,2,4]", candidates.ToJson());
                Assert.AreEqual("[5]", session.Query("candidates", TestSupport.Options("{\"x\":1,\"y\":1}")).ToJson());

                PlaceOk(session, 3, 1, 4);
                Assert.AreEqual("[1,2,4]", session.Query("candidates", TestSupport.Options("{\"x\":3,\"y\":1}")).ToJson());
                Assert.AreEqual("[2,6]", session.Query("candidates", TestSupport.Options("{\"x\":4,\"y\":1}")).ToJson());

                Assert.IsTrue(session.Query("candidates").IsNull);
                Assert.IsTrue(session.Query("candidates", TestSupport.Options("{\"x\":0,\"y\":1}")).IsNull);
                Assert.IsTrue(session.Query("candidates", TestSupport.Options("{\"x\":10,\"y\":1}")).IsNull);
                Assert.IsTrue(session.Query("candidates", TestSupport.Options("{\"x\":1.5,\"y\":1}")).IsNull);
                Assert.IsTrue(session.Query("candidates", TestSupport.Options("{\"x\":\"1\",\"y\":1}")).IsNull);
                Assert.IsTrue(session.Query("nothing").IsNull);
            }
        }

        [Test]
        public void UndoAndRedoWalkTheHistory()
        {
            using (GameSession session = StartClassic())
            {
                int first = PlaceOk(session, 3, 1, 1).Events[0].Id;
                PlaceOk(session, 4, 1, 1);
                Assert.AreEqual(32, session.View.ObjectCount);
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());

                Assert.IsTrue(session.Undo());
                Assert.AreEqual(31, session.View.ObjectCount);
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
                Assert.IsFalse(session.View.Get(first).Props["conflict"].AsBool(true));
                Assert.AreEqual(1, session.View.Turn);

                Assert.IsTrue(session.Redo());
                Assert.AreEqual(32, session.View.ObjectCount);
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());
                Assert.IsTrue(session.View.Get(first).Props["conflict"].AsBool());

                Assert.IsTrue(session.Undo(2));
                Assert.AreEqual(30, session.View.ObjectCount);
                Assert.AreEqual(Puzzle, State(session)["board"].AsString());

                PlaceOk(session, 3, 1, 4);
                Assert.IsFalse(session.CanRedo);
                Assert.AreEqual(31, session.View.ObjectCount);
            }
        }

        [Test]
        public void SnapshotsRestoreTheGameExactly()
        {
            using (GameSession session = StartClassic())
            {
                int first = PlaceOk(session, 3, 1, 1).Events[0].Id;
                PlaceOk(session, 4, 1, 1);
                PlaceOk(session, 4, 1, 2);
                string snapshot = session.CaptureSnapshot();
                string viewBefore = Describe(session.View);
                string stateBefore = State(session).ToJson();

                Do(session, "clear", 3, 1);
                PlaceOk(session, 9, 1, 9);
                Assert.AreNotEqual(viewBefore, Describe(session.View));

                session.LoadSnapshot(snapshot);
                Assert.AreEqual(viewBefore, Describe(session.View));
                Assert.AreEqual(stateBefore, State(session).ToJson());
                Assert.AreEqual(1, session.View.Turn);

                // The restored digits are still the ones on the table: they can be changed and taken out.
                SubmitResult cleared = Do(session, "clear", 3, 1);
                Assert.IsTrue(cleared.IsAccepted);
                Assert.AreEqual(ViewEventKind.Remove, cleared.Events[0].Kind);
                Assert.AreEqual(first, cleared.Events[0].Id);
                Assert.AreEqual(ViewEventKind.Set, PlaceOk(session, 4, 1, 3).Events[0].Kind);
            }
        }

        [Test]
        public void SnapshotsCarryTheFinishedState()
        {
            using (GameSession session = StartClassic())
            {
                Complete(session);
                string snapshot = session.CaptureSnapshot();
                session.NewGame(TestSupport.Options("{\"seed\":5}"));
                Assert.IsFalse(session.View.IsOver);

                session.LoadSnapshot(snapshot);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual(Solved, State(session)["board"].AsString());
                Assert.IsTrue(State(session)["solved"].AsBool());
                Assert.AreEqual("GAME_OVER", Place(session, 3, 1, 1).Code);
            }
        }

        [Test]
        public void CorruptSnapshotsAreRefusedAndTheGameSurvives()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 4);
                string before = Describe(session.View);

                Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot("{\"v\":1,\"view\":[],\"mod\":{\"grid\":\"12\"}}")).Code);
                Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot("{\"v\":1,\"view\":[],\"mod\":{}}")).Code);

                // The cells of the snapshot name objects that are not on its table.
                JsonValue snapshot = JsonValue.Parse(session.CaptureSnapshot());
                snapshot.Set("view", JsonValue.NewArray());
                Assert.AreEqual("BAD_SNAPSHOT", Assert.Throws<GameSessionException>(() => session.LoadSnapshot(snapshot.ToJson())).Code);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(before, Describe(session.View));
                Assert.AreEqual(31, State(session)["filled"].AsInt());
                Assert.AreEqual(2, session.HistoryCount);
                PlaceOk(session, 4, 1, 2);
                Assert.AreEqual(32, session.View.ObjectCount);
            }
        }

        [Test]
        public void ThePuzzleOptionIsChecked()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 4);
                string before = Describe(session.View);
                string[] bad =
                {
                    "123",
                    "x" + new string('0', 80),
                    "55" + new string('0', 79),
                    "5" + new string('0', 7) + "5" + new string('0', 72),
                    "123456780" + "000000009" + new string('0', 63),
                    Puzzle + "0"
                };
                foreach (string puzzle in bad)
                {
                    GameSessionException e = Assert.Throws<GameSessionException>(() => session.NewGame(TestSupport.Options("{\"puzzle\":\"" + puzzle + "\"}")));
                    Assert.AreEqual("BAD_PUZZLE", e.Code, puzzle);
                }
                Assert.AreEqual("a puzzle has 81 digits (0 or . for an empty cell), this one has 3",
                    Assert.Throws<GameSessionException>(() => session.NewGame(TestSupport.Options("{\"puzzle\":\"123\"}"))).Detail);
                StringAssert.Contains("contradict", Assert.Throws<GameSessionException>(() => session.NewGame(TestSupport.Options("{\"puzzle\":\"" + bad[2] + "\"}"))).Detail);
                StringAssert.Contains("no solution", Assert.Throws<GameSessionException>(() => session.NewGame(TestSupport.Options("{\"puzzle\":\"" + bad[4] + "\"}"))).Detail);

                // A refused puzzle leaves the game that is running as it was.
                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(before, Describe(session.View));
                PlaceOk(session, 4, 1, 2);
            }
        }

        [Test]
        public void ThePuzzleCanBeWrittenWithDotsAndLineBreaks()
        {
            List<string> rows = new List<string>();
            for (int i = 0; i < 81; i += 9)
                rows.Add(Puzzle.Substring(i, 9));
            // Inside a JSON string, a backslash and an n are a line break.
            foreach (string text in new[] { Puzzle.Replace('0', '.'), string.Join("\\n", rows), string.Join(" ", rows) })
            {
                using (GameSession session = Start("{\"puzzle\":\"" + text + "\"}"))
                    Assert.AreEqual(Puzzle, State(session)["board"].AsString(), text);
            }
        }

        [Test]
        public void APuzzleWithSeveralSolutionsIsPlayedWithAWarning()
        {
            using (GameSession session = new GameSession(LoadSudoku()))
            {
                IReadOnlyList<ViewEvent> events = session.NewGame(TestSupport.Options("{\"puzzle\":\"" + Empty + "\"}"));
                bool warned = false;
                foreach (ViewEvent e in events)
                    warned |= e.Kind == ViewEventKind.Status && e.Level == "warning";
                Assert.IsTrue(warned);
                Assert.IsFalse(State(session)["unique"].AsBool(true));
                Assert.AreEqual(0, session.View.ObjectCount);

                List<JsonValue> played = LetTheComputerPlay(session);
                Assert.AreEqual(81, played.Count);
                Assert.IsTrue(IsFullGrid(State(session)["board"].AsString()));
            }
        }

        [Test]
        public void AFinishedGridStartsSolved()
        {
            using (GameSession session = new GameSession(LoadSudoku()))
            {
                IReadOnlyList<ViewEvent> events = session.NewGame(TestSupport.Options("{\"puzzle\":\"" + Solved + "\"}"));
                Assert.IsNotNull(Find(events, "over"));
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual(81, session.View.ObjectCount);
                Assert.AreEqual("GAME_OVER", Place(session, 1, 1, 5).Code);
            }
        }

        [Test]
        public void TheSeedDecidesThePuzzle()
        {
            string[] puzzles = new string[3];
            string[] seeds = { "123", "123", "124" };
            for (int i = 0; i < 3; i++)
            {
                using (GameSession session = Start("{\"seed\":" + seeds[i] + "}"))
                {
                    puzzles[i] = State(session)["puzzle"].AsString();
                    Assert.AreEqual(int.Parse(seeds[i]), State(session)["seed"].AsInt());
                }
            }
            Assert.AreEqual(puzzles[0], puzzles[1]);
            Assert.AreNotEqual(puzzles[0], puzzles[2]);
        }

        [Test]
        public void ASeedOfZeroDealsAPuzzleOfItsOwn()
        {
            using (GameSession session = Start("{}"))
            {
                int seed = State(session)["seed"].AsInt();
                Assert.GreaterOrEqual(seed, 1);
                Assert.Less(seed, int.MaxValue);
                AssertSoundPuzzle(session);

                // The seed it reports deals the same puzzle again.
                string puzzle = State(session)["puzzle"].AsString();
                session.NewGame(TestSupport.Options("{\"seed\":" + seed + "}"));
                Assert.AreEqual(puzzle, State(session)["puzzle"].AsString());
            }
        }

        [TestCase("{\"seed\":5,\"clues\":45}", 45)]
        [TestCase("{\"seed\":5,\"clues\":36}", 36)]
        [TestCase("{\"seed\":5}", 36)]
        [TestCase("{\"seed\":5,\"clues\":28}", 28)]
        [TestCase("{\"seed\":5,\"clues\":\"many\"}", 36)]
        [TestCase("{\"seed\":5,\"clues\":80}", 80)]
        [TestCase("{\"seed\":5,\"clues\":200}", 80)]
        public void TheCluesOptionDecidesHowManyCluesAPuzzleKeeps(string options, int expected)
        {
            using (GameSession session = Start(options))
            {
                Assert.AreEqual(expected, State(session)["clues"].AsInt());
                AssertSoundPuzzle(session);
            }
        }

        [Test]
        public void ThePuzzleKeepsMoreCluesThanAskedWhenItCannotBeThinnedFurther()
        {
            using (GameSession session = Start("{\"seed\":5,\"clues\":3}"))
            {
                int clues = State(session)["clues"].AsInt();
                Assert.GreaterOrEqual(clues, 17);
                Assert.LessOrEqual(clues, 30);
                AssertSoundPuzzle(session);
            }
        }

        [Test]
        public void DealtPuzzlesHaveExactlyOneSolution()
        {
            foreach (int clues in new[] { 45, 36, 28, 22 })
            {
                for (int seed = 1; seed <= 6; seed++)
                {
                    using (GameSession session = Start("{\"seed\":" + seed + ",\"clues\":" + clues + "}"))
                    {
                        Assert.GreaterOrEqual(State(session)["clues"].AsInt(), clues);
                        AssertSoundPuzzle(session);
                    }
                }
            }
        }

        [Test]
        public void HardPuzzlesAreDealtWithinTheTimeLimitOfAScript()
        {
            // A script that runs past its time limit faults, which would make NewGame throw.
            for (int seed = 1; seed <= 12; seed++)
            {
                using (GameSession session = Start("{\"seed\":" + seed + ",\"clues\":17}"))
                {
                    int clues = State(session)["clues"].AsInt();
                    Assert.GreaterOrEqual(clues, 17);
                    Assert.LessOrEqual(clues, 30);
                    AssertSoundPuzzle(session);
                }
            }
        }

        [Test]
        public void PuzzlesAreDealtTheSameWhereverTheyAreDealt()
        {
            // The generator has a random generator of its own and works with budgets of search steps, not with the clock or
            // with math.random, so a seed gives the same puzzle on every machine. These are two of them: when they change,
            // so has the generator, and the puzzles that players know by their seeds are not the same any more.
            using (GameSession session = Start("{\"seed\":1,\"clues\":36}"))
            {
                Assert.AreEqual("005004720000050009209360005070000350563720094800000201000006430056008900081070502", State(session)["puzzle"].AsString());
                Assert.AreEqual("615894723738152649249367185172489356563721894894635271927516438356248917481973562", session.Query("solution").AsString());
            }
            using (GameSession session = Start("{\"seed\":99,\"clues\":30}"))
            {
                Assert.AreEqual("978021300000090200000053094009000640004075032030000000090040025001500000005080900", State(session)["puzzle"].AsString());
                Assert.AreEqual("978421356453796218162853794519238647684975132237614589396147825821569473745382961", session.Query("solution").AsString());
            }
        }

        private static string Signature(SimulationResult r)
        {
            List<string> wins = new List<string>();
            foreach (KeyValuePair<int, int> pair in r.Wins)
                wins.Add(pair.Key + "=" + pair.Value);
            return r.Games + "|" + r.Draws + "|" + r.Aborted + "|" + r.Rejected + "|" + r.Moves + "|" + string.Join(",", wins);
        }

        [Test]
        public void SimulatedGamesAreAllWon()
        {
            ModPackage mod = LoadSudoku();
            SimulationResult r = SimulationRunner.Run(mod, new SimulationOptions { Games = 12, Seed = 5 });
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.AreEqual(12, r.Games);
            Assert.AreEqual(12, r.Wins[1]);
            Assert.AreEqual(0, r.Draws);
            Assert.AreEqual(0, r.Aborted);
            Assert.AreEqual(0, r.Rejected);
            Assert.AreEqual(12 * 45, r.Moves);

            SimulationOptions strict = new SimulationOptions { Games = 12, Seed = 5, GameOptions = TestSupport.Options("{\"strict\":true,\"clues\":30}") };
            SimulationResult s = SimulationRunner.Run(mod, strict);
            Assert.IsTrue(s.Succeeded, s.Error);
            Assert.AreEqual(12, s.Wins[1]);
            Assert.AreEqual(0, s.Rejected);
            Assert.AreEqual(12 * 51, s.Moves);

            Assert.AreEqual(Signature(r), Signature(SimulationRunner.Run(mod, new SimulationOptions { Games = 12, Seed = 5 })));
        }

        [Test]
        public void PuzzlesOfYourOwnCanBeSimulated()
        {
            SimulationOptions options = new SimulationOptions { Games = 5, Seed = 1, GameOptions = TestSupport.Options("{\"puzzle\":\"" + Puzzle + "\"}") };
            SimulationResult r = SimulationRunner.Run(LoadSudoku(), options);
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.AreEqual(5, r.Wins[1]);
            Assert.AreEqual(5 * 51, r.Moves);
        }

        [Test]
        public void RunningASimulationLeavesTheLiveGameAlone()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 4);
                string state = State(session).ToJson();
                long version = session.View.Version;
                SimulationResult r = SimulationRunner.Run(session.Mod, new SimulationOptions { Games = 6 });
                Assert.IsTrue(r.Succeeded, r.Error);
                Assert.AreEqual(state, State(session).ToJson());
                Assert.AreEqual(version, session.View.Version);
                PlaceOk(session, 4, 1, 6);
            }
        }

        [Test]
        public void RulesNeverSeeAFaultFromPlayerInput()
        {
            using (GameSession session = Start("{\"seed\":9,\"clues\":30}"))
            {
                Random random = new Random(777);
                string[] actions = { "place", "clear", "cycle", "place", "cycle", "swap", string.Empty };
                for (int i = 0; i < 600; i++)
                {
                    JsonValue command = JsonValue.NewObject();
                    command.Set("action", actions[random.Next(actions.Length)]);
                    if (random.Next(10) == 0)
                        command.Set("action", random.Next(2) == 0 ? JsonValue.NewArray() : JsonValue.NewObject());
                    Junk(random, command, "x");
                    Junk(random, command, "y");
                    Junk(random, command, "digit");
                    SubmitResult result = session.Submit(new InputPacket(random.Next(0, 3), command));
                    Assert.AreNotEqual(SubmitStatus.Faulted, result.Status, command.ToJson());
                    if (i % 150 == 149)
                        session.NewGame(TestSupport.Options("{\"seed\":" + i + ",\"strict\":" + (i % 2 == 0 ? "true" : "false") + "}"));
                }
                Assert.IsFalse(session.VmStats.Faulted);
            }
        }

        private static void Junk(Random random, JsonValue command, string key)
        {
            switch (random.Next(9))
            {
                case 0:
                    command.Set(key, random.Next(-3, 14));
                    break;
                case 1:
                    command.Set(key, random.NextDouble() * 20);
                    break;
                case 2:
                    command.Set(key, "a");
                    break;
                case 3:
                    command.Set(key, true);
                    break;
                case 4:
                    command.Set(key, JsonValue.NewObject());
                    break;
                case 5:
                    command.Set(key, JsonValue.NewArray());
                    break;
                case 6:
                    command.Set(key, 1e300);
                    break;
                case 7:
                    break;
                default:
                    command.Set(key, random.Next(1, 10));
                    break;
            }
        }

        [Test]
        public void TheConsoleDrawsTheDigitsInBlocks()
        {
            using (GameSession session = new GameSession(LoadSudoku()))
            {
                CommandConsole console = new CommandConsole(session);
                string started = console.Execute("new puzzle=" + Puzzle);
                StringAssert.StartsWith("new game started", started);
                StringAssert.Contains("turn: seat 1 (Player)", started);
                StringAssert.Contains("objects: 30", started);

                string[] lines = console.Execute("board").Split('\n');
                string[] expected =
                {
                    "     1  2  3   4  5  6   7  8  9 ",
                    "  1  5  3  .   .  7  .   .  .  . ",
                    "  2  6  .  .   1  9  5   .  .  . ",
                    "  3  .  9  8   .  .  .   .  6  . ",
                    "",
                    "  4  8  .  .   .  6  .   .  .  3 ",
                    "  5  4  .  .   8  .  3   .  .  1 ",
                    "  6  7  .  .   .  2  .   .  .  6 ",
                    "",
                    "  7  .  6  .   .  .  .   2  8  . ",
                    "  8  .  .  .   4  1  9   .  .  5 ",
                    "  9  .  .  .   .  8  .   .  7  9 "
                };
                Assert.AreEqual(expected.Length, lines.Length);
                for (int i = 0; i < expected.Length; i++)
                    Assert.AreEqual(expected[i], lines[i], "line " + i);
            }
        }

        [Test]
        public void TheConsolePlaysAPuzzle()
        {
            using (GameSession session = new GameSession(LoadSudoku()))
            {
                CommandConsole console = new CommandConsole(session);
                console.Execute("new puzzle=" + Puzzle);

                StringAssert.StartsWith("accepted", console.Execute("play place x=3 y=1 digit=4"));
                StringAssert.Contains("  1  5  3  4   .  7  .", console.Execute("board"));
                StringAssert.StartsWith("rejected GIVEN_CELL", console.Execute("play place x=1 y=1 digit=9"));
                StringAssert.StartsWith("rejected BAD_DIGIT", console.Execute("play place x=4 y=1 digit=12"));
                StringAssert.StartsWith("accepted", console.Execute("play cycle x=4 y=1"));
                StringAssert.Contains("  1  5  3  4   1  7  .", console.Execute("board"));

                StringAssert.StartsWith("459 legal commands for seat 1", console.Execute("legal"));
                // The 1 that the click put at column 4, row 1 is the digit of the clue below it.
                StringAssert.Contains("\"conflicts\": 1", console.Execute("query state"));
                Assert.AreEqual("[\n  2,\n  6\n]", console.Execute("query candidates {\"x\":4,\"y\":1}"));

                string text = console.Execute("ff 100 seed=1");
                StringAssert.Contains("stopped: game over", text);
                StringAssert.Contains("result: Player wins (solved)", text);
                string solved = console.Execute("board");
                StringAssert.Contains("  1  5  3  4   6  7  8   9  1  2", solved);
                StringAssert.Contains("  9  3  4  5   2  8  6   1  7  9", solved);
            }
        }

        [Test]
        public void TheAsciiBoardDrawsLabelsAndSeparatesBlocks()
        {
            ViewModel view = new ViewModel();
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"board\",\"columns\":4,\"rows\":4,\"block\":2}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":1,\"kind\":\"digit\",\"x\":1,\"y\":1,\"props\":{\"label\":\"7\"}}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":2,\"kind\":\"stone\",\"x\":4,\"y\":4,\"owner\":2}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"last_move\",\"x\":4,\"y\":4}")));
            string[] expected =
            {
                "     1  2   3  4 ",
                "  1  7  .   .  . ",
                "  2  .  .   .  . ",
                "",
                "  3  .  .   .  . ",
                "  4  .  .   . [O]"
            };
            string[] lines = AsciiBoard.Render(view).Split('\n');
            Assert.AreEqual(expected.Length, lines.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], lines[i], "line " + i);
        }

        [Test]
        public void ALabelWinsOverTheSymbolOfTheOwner()
        {
            ViewModel view = new ViewModel();
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"board\",\"columns\":3,\"rows\":1}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":1,\"y\":1,\"owner\":1,\"props\":{\"label\":\"K!\"}}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":2,\"kind\":\"a\",\"x\":2,\"y\":1,\"owner\":1,\"props\":{\"label\":\"\"}}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":3,\"kind\":\"a\",\"x\":3,\"y\":1,\"owner\":2,\"props\":{\"label\":5}}")));
            string[] lines = AsciiBoard.Render(view).Split('\n');
            Assert.AreEqual("  1  K  X  O ", lines[1]);

            // A label that changes later changes what is drawn.
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"set\",\"id\":2,\"props\":{\"label\":\"9\"}}")));
            Assert.AreEqual("  1  K  9  O ", AsciiBoard.Render(view).Split('\n')[1]);
        }

        [Test]
        public void ABlockThatCannotSeparateAnythingIsIgnored()
        {
            foreach (string block in new[] { "0", "1", "3", "9", "-2" })
            {
                ViewModel view = new ViewModel();
                view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"board\",\"columns\":3,\"rows\":3,\"block\":" + block + "}")));
                string[] lines = AsciiBoard.Render(view).Split('\n');
                Assert.AreEqual(4, lines.Length, "block " + block);
                Assert.AreEqual("     1  2  3 ", lines[0], "block " + block);
            }
        }
    }
}
