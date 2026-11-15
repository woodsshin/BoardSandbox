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

        // The digits on the grid; the table also holds the tiles under the cells and the keys of the pad.
        private static int Digits(GameSession session)
        {
            int count = 0;
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind == "digit")
                    count++;
            }
            return count;
        }

        // The tile under a cell and the key of a digit (10 is the eraser), which is what a click names.
        private static ViewObject Tile(GameSession session, int x, int y)
        {
            foreach (ViewObject o in session.View.Objects)
            {
                JsonValue pos = o.Props["pos"];
                if (o.Kind == "cell" && pos[0].AsInt(-99) == x - 5 && pos[2].AsInt(-99) == y - 5)
                    return o;
            }
            throw new InvalidOperationException("there is no tile at (" + x + "," + y + ")");
        }

        private static ViewObject Key(GameSession session, int key)
        {
            string label = key == 10 ? "X" : key.ToString();
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind == "key" && o.Props["label"].AsString() == label)
                    return o;
            }
            throw new InvalidOperationException("there is no key " + key);
        }

        private static SubmitResult Tap(GameSession session, int id, bool hold = false)
        {
            return session.Submit(InputPacket.Action(1, "tap").With("id", id).With("button", 0).With("shift", false).With("hold", hold));
        }

        private static SubmitResult TapCell(GameSession session, int x, int y, bool hold = false)
        {
            return Tap(session, Tile(session, x, y).Id, hold);
        }

        private static SubmitResult TapKey(GameSession session, int key)
        {
            return Tap(session, Key(session, key).Id);
        }

        private static double Tint(GameSession session, int x, int y)
        {
            return Tile(session, x, y).Props["alpha"].AsDouble(-1);
        }

        // The small number under a key: how many of its digit are still to be put on the grid ("" for the eraser).
        private static string LeftUnder(GameSession session, int key)
        {
            JsonValue labels = Key(session, key).Props["labels"];
            return labels.IsArray && labels.Count > 0 ? labels[0]["text"].AsString() : string.Empty;
        }

        // Whether a key has gone from the pad.
        private static bool Gone(GameSession session, int key)
        {
            return !Key(session, key).Props["visible"].AsBool(true);
        }

        // The digit that stands at a place of the grid, as the table shows it; null for an empty cell.
        private static ViewObject DigitAt(GameSession session, int x, int y)
        {
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind == "digit" && (int)o.X == x && (int)o.Y == y)
                    return o;
            }
            return null;
        }

        // Puts the right digit in every empty cell of the classic puzzle that lies in the box (1 to 9), the last
        // `leave` of them excepted; returns the result of the last placement.
        private static SubmitResult CompleteBox(GameSession session, int box, int leave = 0)
        {
            List<int> cells = new List<int>();
            for (int i = 0; i < 81; i++)
            {
                int x = i % 9;
                int y = i / 9;
                if (Puzzle[i] == '0' && y / 3 * 3 + x / 3 + 1 == box)
                    cells.Add(i);
            }
            SubmitResult last = null;
            for (int k = 0; k < cells.Count - leave; k++)
                last = PlaceOk(session, cells[k] % 9 + 1, cells[k] / 9 + 1, Solved[cells[k]] - '0');
            return last;
        }

        private static string Notes(GameSession session, int x, int y)
        {
            JsonValue labels = Tile(session, x, y).Props["labels"];
            string text = string.Empty;
            if (labels.IsArray)
            {
                foreach (JsonValue label in labels.Items)
                    text += label["text"].AsString();
            }
            return text;
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
            Assert.AreEqual(clues, Digits(session));
            Assert.AreEqual(puzzle, state["board"].AsString());
        }

        [Test]
        public void TheManifestDeclaresAGridForOneOrTwoPlayersWhoPlayTogether()
        {
            ModManifest manifest = LoadSudoku().Manifest;
            Assert.AreEqual("sudoku", manifest.Id);
            Assert.AreEqual(1, manifest.MinPlayers);
            Assert.AreEqual(2, manifest.MaxPlayers);
            Assert.AreEqual(2, manifest.Seats.Count);
            Assert.AreEqual("Blue", manifest.Seats[0].Name);
            Assert.AreEqual("#4d8fe6", manifest.Seats[0].Color);
            Assert.AreEqual("Orange", manifest.Seats[1].Name);
            Assert.AreNotEqual(manifest.Seats[0].Color, manifest.Seats[1].Color);
            Assert.IsTrue(manifest.Cooperative);
            Assert.IsFalse(manifest.SplitScreen);
            Assert.AreEqual(2, manifest.RoomPlayers);
            Assert.AreEqual(36, manifest.DefaultOptions["clues"].AsInt());
            Assert.AreEqual(0, manifest.DefaultOptions["seed"].AsInt(-1));
            Assert.IsFalse(manifest.DefaultOptions["strict"].AsBool(true));
            // A puzzle of your own is typed in the console; the text field of the options panel is too short for 81 digits.
            Assert.AreEqual(3, manifest.DefaultOptions.Count);
            Assert.IsFalse(manifest.DefaultOptions.Has("puzzle"));
            Assert.AreEqual("tap", manifest.Client["click"]["action"].AsString());
            Assert.AreEqual("object", manifest.Client["click"]["target"].AsString());
            Assert.IsTrue(manifest.Client["click"]["hold"].AsBool());
            Assert.AreEqual("grid", manifest.Client["board"]["style"].AsString());
            Assert.AreEqual(89, manifest.Client["stage"]["camera"]["pitch"].AsInt());
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
                Assert.AreEqual(30 + 91, Count(events, "spawn"), "the clues, the tiles and the pad");
                Assert.AreEqual(ViewEventKind.Turn, events[events.Count - 1].Kind);
                Assert.AreEqual(1, events[events.Count - 1].Seat);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(30, Digits(session));
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
                    if (o.Kind != "digit")
                        continue;
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
                Assert.AreEqual(2, result.Events.Count, "the digit, and the count under the key of the digit");
                ViewEvent spawn = result.Events[0];
                Assert.AreEqual(ViewEventKind.Spawn, spawn.Kind);
                Assert.AreEqual("digit", spawn.ObjectKind);
                Assert.AreEqual(3, (int)spawn.X);
                Assert.AreEqual(1, (int)spawn.Y);
                Assert.AreEqual(1, spawn.Owner);
                Assert.AreEqual("4", spawn.Props["label"].AsString());
                Assert.IsFalse(spawn.Props["given"].AsBool(true));
                Assert.IsFalse(spawn.Props["conflict"].AsBool(true));
                Assert.IsFalse(spawn.Props["locked"].AsBool(true));
                Assert.AreEqual(ViewEventKind.Set, result.Events[1].Kind);
                Assert.AreEqual(Key(session, 4).Id, result.Events[1].Id);

                Assert.AreEqual(31, Digits(session));
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
                Assert.AreEqual(3, changed.Events.Count, "the digit, and the counts under the keys of the two digits");
                Assert.AreEqual(ViewEventKind.Set, changed.Events[0].Kind);
                Assert.AreEqual(id, changed.Events[0].Id);
                Assert.AreEqual("2", session.View.Get(id).Props["label"].AsString());
                Assert.AreEqual(31, Digits(session));

                SubmitResult cleared = Do(session, "clear", 3, 1);
                Assert.IsTrue(cleared.IsAccepted);
                Assert.AreEqual(2, cleared.Events.Count, "the digit goes, and the count under its key changes");
                Assert.AreEqual(ViewEventKind.Remove, cleared.Events[0].Kind);
                Assert.AreEqual(id, cleared.Events[0].Id);
                Assert.IsNull(session.View.Get(id));
                Assert.AreEqual(30, Digits(session));
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
                Assert.AreEqual(2, result.Events.Count);
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
                Assert.AreEqual(3, second.Events.Count, "the new digit, the digit it clashes with, the count under the key");
                Assert.AreEqual(ViewEventKind.Spawn, second.Events[0].Kind);
                Assert.IsTrue(second.Events[0].Props["conflict"].AsBool());
                Assert.AreEqual(ViewEventKind.Set, second.Events[1].Kind);
                Assert.AreEqual(first, second.Events[1].Id);
                Assert.IsTrue(session.View.Get(first).Props["conflict"].AsBool());
                Assert.AreEqual("#ff5c5c", session.View.Get(first).Props["color"].AsString());
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());

                SubmitResult cleared = Do(session, "clear", 4, 1);
                Assert.AreEqual(3, cleared.Events.Count);
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
                // Two cells are left. Each gets the digit that belongs in the other one, so that every digit is on
                // the grid nine times and yet the columns of the two cells see a digit twice.
                List<int> empties = new List<int>();
                for (int i = 0; i < 81; i++)
                {
                    if (Puzzle[i] == '0')
                        empties.Add(i);
                }
                int a = empties[empties.Count - 2];
                int b = empties[empties.Count - 1];
                int ax = a % 9 + 1;
                int ay = a / 9 + 1;
                int bx = b % 9 + 1;
                int by = b / 9 + 1;
                int rightForA = Solved[a] - '0';
                int rightForB = Solved[b] - '0';
                Assert.AreNotEqual(rightForA, rightForB);

                Complete(session, 2);
                PlaceOk(session, ax, ay, rightForB);
                SubmitResult clash = PlaceOk(session, bx, by, rightForA);
                Assert.IsNull(Find(clash.Events, "over"));
                Assert.IsFalse(session.View.IsOver);
                Assert.AreEqual(81, State(session)["filled"].AsInt());
                Assert.Greater(State(session)["conflicts"].AsInt(), 0);
                Assert.IsFalse(State(session)["solved"].AsBool(true));
                for (int key = 1; key <= 9; key++)
                    Assert.IsTrue(Gone(session, key), "every digit is on the grid nine times: key " + key);
                Assert.IsFalse(Gone(session, 10), "the eraser stays");

                // A digit has to be taken out before the right one can go in.
                Assert.AreEqual("DIGIT_USED", Place(session, ax, ay, rightForA).Code);
                Assert.IsTrue(Do(session, "clear", bx, by).IsAccepted);
                PlaceOk(session, ax, ay, rightForA);
                SubmitResult fixedIt = PlaceOk(session, bx, by, rightForB);
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
        public void NothingIsLegalOnceThePuzzleIsSolvedOrForASeatThatIsNotThere()
        {
            using (GameSession session = StartClassic())
            {
                // Both seats may play the same moves; there is no third.
                Assert.AreEqual(session.LegalCommands(1).Count, session.LegalCommands(2).Count);
                Assert.Greater(session.LegalCommands(2).Count, 0);
                Assert.AreEqual(0, session.LegalCommands(3).Count);
                Assert.AreEqual(0, session.LegalCommands(0).Count);
                Complete(session);
                Assert.AreEqual(0, session.LegalCommands(1).Count);
                Assert.AreEqual(0, session.LegalCommands(2).Count);
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
                Assert.AreEqual(32, Digits(session));
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());

                Assert.IsTrue(session.Undo());
                Assert.AreEqual(31, Digits(session));
                Assert.AreEqual(0, State(session)["conflicts"].AsInt());
                Assert.IsFalse(session.View.Get(first).Props["conflict"].AsBool(true));
                Assert.AreEqual(1, session.View.Turn);

                Assert.IsTrue(session.Redo());
                Assert.AreEqual(32, Digits(session));
                Assert.AreEqual(2, State(session)["conflicts"].AsInt());
                Assert.IsTrue(session.View.Get(first).Props["conflict"].AsBool());

                Assert.IsTrue(session.Undo(2));
                Assert.AreEqual(30, Digits(session));
                Assert.AreEqual(Puzzle, State(session)["board"].AsString());

                PlaceOk(session, 3, 1, 4);
                Assert.IsFalse(session.CanRedo);
                Assert.AreEqual(31, Digits(session));
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
                Assert.AreEqual(32, Digits(session));
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
                Assert.AreEqual(0, Digits(session));

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
                Assert.AreEqual(81, Digits(session));
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
                StringAssert.Contains("turn: none (cooperative: every seat plays at once)", started);
                StringAssert.Contains("objects: 121", started);

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
                StringAssert.Contains("result: everyone wins (solved)", text);
                string solved = console.Execute("board");
                StringAssert.Contains("  1  5  3  4   6  7  8   9  1  2", solved);
                StringAssert.Contains("  9  3  4  5   2  8  6   1  7  9", solved);
            }
        }

        [Test]
        public void TheTableHasATileUnderEveryCellAndAPadBelowTheGrid()
        {
            using (GameSession session = StartClassic())
            {
                int tiles = 0;
                int keys = 0;
                foreach (ViewObject o in session.View.Objects)
                {
                    if (o.Kind == "cell")
                    {
                        tiles++;
                        Assert.AreEqual(0.0, o.Props["alpha"].AsDouble(-1), "nothing is pointed out before a digit is chosen");
                        Assert.IsTrue(o.Props["pos"].IsArray, "tiles are not pieces of the grid");
                    }
                    else if (o.Kind == "key")
                    {
                        keys++;
                        Assert.Greater(o.Props["pos"][2].AsDouble(), 4.9, "the pad lies below the grid");
                        Assert.Less(Math.Abs(o.Props["pos"][0].AsDouble()), 4.9);
                    }
                }
                Assert.AreEqual(81, tiles);
                Assert.AreEqual(10, keys);
                Assert.AreEqual(30 + 91, session.View.ObjectCount);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                for (int key = 1; key <= 10; key++)
                    Assert.AreEqual("#e8ecf2", Key(session, key).Props["color"].AsString());
            }
        }

        [Test]
        public void ChoosingADigitPointsOutWhereItIsAndWhatItReaches()
        {
            using (GameSession session = StartClassic())
            {
                // The 5s of the puzzle are at (1,1), (5,2) and (8,8)... the last one is (9,8).
                Assert.IsTrue(TapKey(session, 5).IsAccepted);
                Assert.AreEqual(5, State(session)["selected"].AsInt());
                Assert.AreEqual("#4d8fe6", Key(session, 5).Props["color"].AsString());
                Assert.AreEqual("#ffffff", Key(session, 5).Props["label_color"].AsString());
                Assert.AreEqual("#e8ecf2", Key(session, 4).Props["color"].AsString());

                Assert.AreEqual(0.45, Tint(session, 1, 1), 1e-9, "a cell that holds the digit");
                Assert.AreEqual(0.45, Tint(session, 6, 2), 1e-9);
                Assert.AreEqual(0.45, Tint(session, 9, 8), 1e-9);
                Assert.AreEqual(0.14, Tint(session, 4, 1), 1e-9, "the row of a 5");
                Assert.AreEqual(0.14, Tint(session, 1, 9), 1e-9, "the column of a 5");
                Assert.AreEqual(0.14, Tint(session, 3, 3), 1e-9, "the box of a 5");
                Assert.AreEqual(0.14, Tint(session, 2, 1), 1e-9, "another digit in the row of a 5");
                Assert.AreEqual(0.0, Tint(session, 3, 4), 1e-9, "a 5 can still go here");
                Assert.AreEqual(0.0, Tint(session, 4, 7), 1e-9);

                // The cells left plain are exactly those where the digit fits.
                for (int y = 1; y <= 9; y++)
                {
                    for (int x = 1; x <= 9; x++)
                    {
                        if (Puzzle[(y - 1) * 9 + x - 1] != '0')
                            continue;
                        bool fits = false;
                        foreach (JsonValue digit in session.Query("candidates", JsonValue.NewObject().Set("x", x).Set("y", y)).Items)
                            fits |= digit.AsInt() == 5;
                        Assert.AreEqual(fits, Tint(session, x, y) == 0.0, "cell " + x + "," + y);
                    }
                }

                // Another digit moves the tints, the same key again takes them away.
                Assert.IsTrue(TapKey(session, 7).IsAccepted);
                Assert.AreEqual("#e8ecf2", Key(session, 5).Props["color"].AsString());
                Assert.AreEqual(0.45, Tint(session, 5, 1), 1e-9);
                Assert.AreEqual(0.14, Tint(session, 1, 1), 1e-9);
                Assert.IsTrue(TapKey(session, 7).IsAccepted);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                for (int y = 1; y <= 9; y++)
                {
                    for (int x = 1; x <= 9; x++)
                        Assert.AreEqual(0.0, Tint(session, x, y), 1e-9);
                }
                Assert.AreEqual(0, State(session)["moves"].AsInt(), "choosing is not a move");
            }
        }

        [Test]
        public void ATapEntersTheChosenDigitAndAnotherTapTakesItOut()
        {
            using (GameSession session = StartClassic())
            {
                Assert.AreEqual("NOTHING_CHOSEN", TapCell(session, 3, 1).Code);
                TapKey(session, 4);
                Assert.IsTrue(TapCell(session, 3, 1).IsAccepted);
                Assert.AreEqual('4', State(session)["board"].AsString()[2]);
                Assert.AreEqual(1, State(session)["moves"].AsInt());
                Assert.AreEqual(0.45, Tint(session, 3, 1), 1e-9, "the new 4 is pointed out with the others");
                Assert.AreEqual(0.14, Tint(session, 3, 7), 1e-9, "and now reaches down its column");

                Assert.IsTrue(TapCell(session, 3, 1).IsAccepted);
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
                Assert.AreEqual(0.0, Tint(session, 3, 7), 1e-9);

                // A tap on a clue chooses its digit instead of changing it.
                Assert.IsTrue(TapCell(session, 1, 1).IsAccepted);
                Assert.AreEqual(5, State(session)["selected"].AsInt());
                Assert.AreEqual("GIVEN_CELL", TapCell(session, 1, 1).Code);
                Assert.AreEqual(Puzzle.Substring(0, 9), State(session)["board"].AsString().Substring(0, 9));

                // Another digit over an entry replaces it.
                Assert.IsTrue(TapCell(session, 3, 4).IsAccepted);
                TapKey(session, 2);
                Assert.IsTrue(TapCell(session, 3, 4).IsAccepted);
                Assert.AreEqual('2', State(session)["board"].AsString()[29]);

                // With nothing chosen a tap on an entry chooses its digit.
                TapKey(session, 2);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                Assert.IsTrue(TapCell(session, 3, 4).IsAccepted);
                Assert.AreEqual(2, State(session)["selected"].AsInt());

                Assert.AreEqual("NOT_A_CELL", Tap(session, 999999).Code);
                Assert.AreEqual("INVALID_ARGUMENT", Submit(session, "{\"action\":\"tap\"}").Code);
            }
        }

        [Test]
        public void HoldingACellNotesTheChosenDigit()
        {
            using (GameSession session = StartClassic())
            {
                TapKey(session, 4);
                Assert.IsTrue(TapCell(session, 3, 1, true).IsAccepted);
                Assert.AreEqual("4", Notes(session, 3, 1));
                Assert.AreEqual('0', State(session)["board"].AsString()[2], "a note is not an entry");
                Assert.AreEqual(0, State(session)["moves"].AsInt());

                TapKey(session, 1);
                TapCell(session, 3, 1, true);
                TapKey(session, 9);
                TapCell(session, 3, 1, true);
                Assert.AreEqual("149", Notes(session, 3, 1));

                // Small, thin, and each in its own place of the cell: 1 top left, 9 bottom right.
                JsonValue labels = Tile(session, 3, 1).Props["labels"];
                Assert.AreEqual(3, labels.Count);
                Assert.AreEqual(-0.3, labels[0]["dx"].AsDouble(), 1e-9);
                Assert.AreEqual(-0.3, labels[0]["dy"].AsDouble(), 1e-9);
                Assert.AreEqual(-0.3, labels[1]["dx"].AsDouble(), 1e-9);
                Assert.AreEqual(0.0, labels[1]["dy"].AsDouble(), 1e-9);
                Assert.AreEqual(0.3, labels[2]["dx"].AsDouble(), 1e-9);
                Assert.AreEqual(0.3, labels[2]["dy"].AsDouble(), 1e-9);
                Assert.Less(labels[0]["size"].AsDouble(), 0.3);
                Assert.IsFalse(labels[0]["bold"].AsBool(true));
                Assert.IsTrue(labels[2]["bold"].AsBool(), "the note of the chosen digit stands out");

                // Holding again rubs the note out; the right button and shift do the same as holding.
                Assert.IsTrue(TapCell(session, 3, 1, true).IsAccepted);
                Assert.AreEqual("14", Notes(session, 3, 1));
                int tile = Tile(session, 3, 1).Id;
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "tap").With("id", tile).With("button", 1)).IsAccepted);
                Assert.AreEqual("149", Notes(session, 3, 1));
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "tap").With("id", tile).With("shift", true)).IsAccepted);
                Assert.AreEqual("14", Notes(session, 3, 1));

                JsonValue listed = session.Query("notes");
                Assert.AreEqual(1, listed.Count);
                Assert.AreEqual(3, listed[0]["x"].AsInt());
                Assert.AreEqual(2, listed[0]["digits"].Count);

                Assert.AreEqual("CELL_FILLED", TapCell(session, 1, 1, true).Code);
                Assert.IsTrue(Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":2}").IsAccepted);
                Assert.AreEqual("2", Notes(session, 4, 1));
                Assert.AreEqual("GIVEN_CELL", Submit(session, "{\"action\":\"note\",\"x\":1,\"y\":1,\"digit\":2}").Code);
                Assert.AreEqual("BAD_DIGIT", Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":0}").Code);
            }
        }

        [Test]
        public void EnteringADigitClearsTheNotesItSettles()
        {
            using (GameSession session = StartClassic())
            {
                Submit(session, "{\"action\":\"note\",\"x\":3,\"y\":1,\"digit\":4}");
                Submit(session, "{\"action\":\"note\",\"x\":3,\"y\":1,\"digit\":2}");
                Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":4}");
                Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":6}");
                Submit(session, "{\"action\":\"note\",\"x\":3,\"y\":5,\"digit\":4}");
                Submit(session, "{\"action\":\"note\",\"x\":9,\"y\":7,\"digit\":4}");

                PlaceOk(session, 3, 1, 4);
                Assert.AreEqual("", Notes(session, 3, 1), "the cell is decided");
                Assert.AreEqual("6", Notes(session, 4, 1), "a 4 can no longer go in this row");
                Assert.AreEqual("", Notes(session, 3, 5), "nor in this column");
                Assert.AreEqual("4", Notes(session, 9, 7), "this cell does not see the 4");
            }
        }

        [Test]
        public void TheEraserTakesOutEntriesAndNotes()
        {
            using (GameSession session = StartClassic())
            {
                PlaceOk(session, 3, 1, 4);
                Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":2}");
                Submit(session, "{\"action\":\"note\",\"x\":4,\"y\":1,\"digit\":6}");

                Assert.IsTrue(TapKey(session, 10).IsAccepted);
                Assert.AreEqual(10, State(session)["selected"].AsInt());
                Assert.AreEqual("#4d8fe6", Key(session, 10).Props["color"].AsString());
                Assert.IsTrue(TapCell(session, 3, 1).IsAccepted);
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
                Assert.IsTrue(TapCell(session, 4, 1).IsAccepted);
                Assert.AreEqual("", Notes(session, 4, 1));
                Assert.AreEqual("ALREADY_EMPTY", TapCell(session, 4, 1).Code);
                Assert.AreEqual("GIVEN_CELL", TapCell(session, 1, 1).Code);
            }
        }

        [Test]
        public void KeysChooseDigitsToo()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(Submit(session, "{\"action\":\"select\",\"digit\":5}").IsAccepted);
                Assert.AreEqual(5, State(session)["selected"].AsInt());
                Assert.IsTrue(Submit(session, "{\"action\":\"select\",\"digit\":5}").IsAccepted);
                Assert.AreEqual(0, State(session)["selected"].AsInt(), "the same digit again lets go of it");
                Submit(session, "{\"action\":\"select\",\"digit\":3}");
                Assert.IsTrue(Submit(session, "{\"action\":\"select\",\"digit\":0}").IsAccepted);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                Assert.AreEqual("UNCHANGED", Submit(session, "{\"action\":\"select\",\"digit\":0}").Code);
                Assert.AreEqual("BAD_DIGIT", Submit(session, "{\"action\":\"select\",\"digit\":11}").Code);
                Assert.AreEqual("INVALID_ARGUMENT", Submit(session, "{\"action\":\"select\"}").Code);

                JsonValue keys = LoadSudoku().Manifest.Client["keys"];
                Assert.AreEqual(5, keys["5"]["digit"].AsInt());
                Assert.AreEqual(10, keys["backspace"]["digit"].AsInt());
            }
        }

        [Test]
        public void EveryKeyShowsHowManyOfItsDigitAreStillToCome()
        {
            using (GameSession session = StartClassic())
            {
                string remaining = State(session)["remaining"].AsString();
                Assert.AreEqual(9, remaining.Length);
                for (int key = 1; key <= 9; key++)
                {
                    int onTheGrid = 0;
                    foreach (char c in Puzzle)
                    {
                        if (c == '0' + key)
                            onTheGrid++;
                    }
                    Assert.AreEqual((9 - onTheGrid).ToString(), LeftUnder(session, key), "key " + key);
                    Assert.AreEqual((char)('0' + 9 - onTheGrid), remaining[key - 1]);

                    // The number is small and lies below the key, which is at the pad row of the table.
                    JsonValue label = Key(session, key).Props["labels"][0];
                    Assert.Less(label["size"].AsDouble(), 0.4, "small");
                    Assert.Greater(label["dy"].AsDouble(), 0.5, "below the key");
                    Assert.AreEqual(0.0, label["dx"].AsDouble(), 1e-9);
                    Assert.IsTrue(Key(session, key).Props["visible"].AsBool(false));
                }
                Assert.AreEqual(string.Empty, LeftUnder(session, 10), "the eraser has nothing to count");

                // Every digit that goes on the grid counts down, every one that is taken out counts up.
                string before = LeftUnder(session, 4);
                PlaceOk(session, 3, 1, 4);
                Assert.AreEqual((int.Parse(before) - 1).ToString(), LeftUnder(session, 4));
                PlaceOk(session, 3, 1, 2);
                Assert.AreEqual(before, LeftUnder(session, 4));
                Assert.IsTrue(Do(session, "clear", 3, 1).IsAccepted);
                Assert.AreEqual(remaining, State(session)["remaining"].AsString());
                Assert.AreEqual(before, LeftUnder(session, 4));

                // Choosing a key does not change the count.
                TapKey(session, 4);
                Assert.AreEqual(before, LeftUnder(session, 4));
                Assert.AreEqual("#4d8fe6", Key(session, 4).Props["color"].AsString());
            }
        }

        [Test]
        public void AKeyGoesOnceAllNineOfItsDigitAreOnTheGrid()
        {
            using (GameSession session = StartClassic())
            {
                // The classic puzzle has three 5s; the solution has the other six.
                Assert.AreEqual("6", LeftUnder(session, 5));
                int placed = 0;
                int firstX = 0;
                int firstY = 0;
                for (int i = 0; i < 81 && placed < 6; i++)
                {
                    if (Puzzle[i] != '0' || Solved[i] != '5')
                        continue;
                    placed++;
                    if (placed == 1)
                    {
                        firstX = i % 9 + 1;
                        firstY = i / 9 + 1;
                    }
                    PlaceOk(session, i % 9 + 1, i / 9 + 1, 5);
                    Assert.AreEqual(placed == 6, Gone(session, 5), placed + " of the six");
                    if (placed < 6)
                        Assert.AreEqual((6 - placed).ToString(), LeftUnder(session, 5));
                }
                Assert.IsFalse(Gone(session, 6), "the other keys stay");
                Assert.IsFalse(Gone(session, 10));
                Assert.AreEqual('0', State(session)["remaining"].AsString()[4]);

                // A digit that is used up can not be chosen, and no tenth one can be put on the grid.
                Assert.AreEqual("DIGIT_USED", Submit(session, "{\"action\":\"select\",\"digit\":5}").Code);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                Assert.AreEqual("DIGIT_USED", Place(session, 3, 1, 5).Code);
                Assert.AreEqual(36, Digits(session));

                // Cycling a cell skips it.
                int[] seen = new int[6];
                for (int k = 0; k < 6; k++)
                {
                    Assert.IsTrue(Do(session, "cycle", 3, 1).IsAccepted);
                    seen[k] = State(session)["board"].AsString()[2] - '0';
                }
                Assert.AreEqual("123467", string.Join("", seen));

                // Taking a 5 out brings the key back with the number 1 under it, and it can be chosen again.
                Assert.IsTrue(Do(session, "clear", firstX, firstY).IsAccepted);
                Assert.IsFalse(Gone(session, 5));
                Assert.AreEqual("1", LeftUnder(session, 5));
                Assert.IsTrue(TapKey(session, 5).IsAccepted);
                Assert.AreEqual(5, State(session)["selected"].AsInt());
            }
        }

        [Test]
        public void UndoBringsAKeyBack()
        {
            using (GameSession session = StartClassic())
            {
                int placed = 0;
                for (int i = 0; i < 81 && placed < 6; i++)
                {
                    if (Puzzle[i] == '0' && Solved[i] == '5')
                    {
                        placed++;
                        PlaceOk(session, i % 9 + 1, i / 9 + 1, 5);
                    }
                }
                Assert.IsTrue(Gone(session, 5));
                string table = Describe(session.View);
                Assert.IsTrue(session.Undo());
                Assert.IsFalse(Gone(session, 5));
                Assert.AreEqual("1", LeftUnder(session, 5));
                Assert.IsTrue(session.Redo());
                Assert.IsTrue(Gone(session, 5));
                Assert.AreEqual(table, Describe(session.View));
            }
        }

        [Test]
        public void TheChosenDigitIsLetGoOfWhenItIsUsedUp()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKey(session, 5).IsAccepted);
                int placed = 0;
                for (int i = 0; i < 81 && placed < 6; i++)
                {
                    if (Puzzle[i] != '0' || Solved[i] != '5')
                        continue;
                    placed++;
                    Assert.IsTrue(TapCell(session, i % 9 + 1, i / 9 + 1).IsAccepted);
                    Assert.AreEqual(placed < 6 ? 5 : 0, State(session)["selected"].AsInt(), placed + " of the six");
                }
                Assert.IsTrue(Gone(session, 5));
                for (int y = 1; y <= 9; y++)
                {
                    for (int x = 1; x <= 9; x++)
                        Assert.AreEqual(0.0, Tint(session, x, y), 1e-9, "nothing is pointed out any more: " + x + "," + y);
                }

                // Nothing is chosen, and a 5 can not be chosen by tapping one of them either.
                Assert.AreEqual("NOTHING_CHOSEN", TapCell(session, 3, 1).Code);
                Assert.AreEqual("DIGIT_USED", TapCell(session, 1, 1).Code);
                Assert.AreEqual(0, State(session)["selected"].AsInt());
            }
        }

        [Test]
        public void TheChoiceAndTheNotesComeBackWithUndoAndSnapshots()
        {
            using (GameSession session = StartClassic())
            {
                TapKey(session, 4);
                TapCell(session, 3, 1, true);
                TapCell(session, 5, 5);

                // Undo walks back through entries, notes and choices alike.
                Assert.IsTrue(session.Undo(2));
                Assert.AreEqual("", Notes(session, 3, 1));
                Assert.AreEqual(4, State(session)["selected"].AsInt());
                Assert.IsTrue(session.Undo());
                Assert.AreEqual(0, State(session)["selected"].AsInt());
                Assert.AreEqual(0.0, Tint(session, 1, 5), 1e-9);
                Assert.IsTrue(session.Redo(3));
                Assert.AreEqual("4", Notes(session, 3, 1));
                Assert.AreEqual(0.45, Tint(session, 1, 5), 1e-9);

                string snapshot = session.CaptureSnapshot();
                string table = Describe(session.View);

                TapKey(session, 6);
                TapCell(session, 3, 1, true);
                TapCell(session, 5, 5);
                Assert.AreEqual("46", Notes(session, 3, 1));
                Assert.AreEqual('6', State(session)["board"].AsString()[40]);

                session.LoadSnapshot(snapshot);
                Assert.AreEqual(table, Describe(session.View));
                Assert.AreEqual(4, State(session)["selected"].AsInt());
                Assert.AreEqual("4", Notes(session, 3, 1));
                Assert.AreEqual('4', State(session)["board"].AsString()[40]);

                // The game goes on from there as if nothing had happened: only what changes is sent.
                SubmitResult again = TapCell(session, 3, 1, true);
                Assert.IsTrue(again.IsAccepted);
                Assert.AreEqual(1, again.Events.Count);
                Assert.AreEqual("", Notes(session, 3, 1));
                Assert.IsTrue(TapKey(session, 4).IsAccepted);
                Assert.AreEqual(0.0, Tint(session, 5, 5), 1e-9);

            }
        }

        // Where the tile of a cell (the object, by its id) stands on the grid.
        private static int[] CellOfTile(GameSession session, int id)
        {
            JsonValue pos = session.View.Get(id).Props["pos"];
            return new[] { pos[0].AsInt() + 5, pos[2].AsInt() + 5 };
        }

        // The tiles that light up in a wave, in the order they do, as their distance from the given cell, with the
        // places in the events where the first and the last of them light up.
        private static List<int> WaveOrder(GameSession session, IReadOnlyList<ViewEvent> events, int fromX, int fromY, out int first, out int last)
        {
            List<int> order = new List<int>();
            first = -1;
            last = -1;
            for (int k = 0; k < events.Count; k++)
            {
                ViewEvent e = events[k];
                if (e.Kind != ViewEventKind.Set || e.Props["color"].AsString() != "#ffd166")
                    continue;
                int[] at = CellOfTile(session, e.Id);
                order.Add(Math.Abs(at[0] - fromX) + Math.Abs(at[1] - fromY));
                if (first < 0)
                    first = k;
                last = k;
            }
            return order;
        }

        [Test]
        public void FinishingABoxPlaysAWaveFromTheLastCellAndLocksIt()
        {
            using (GameSession session = StartClassic())
            {
                // The box at the top left holds 5 3 . / 6 . . / . 9 8 and is missing 4, 7, 2 and 1.
                SubmitResult early = CompleteBox(session, 1, 1);
                Assert.AreEqual(0, Count(early.Events, "fx"), "the box is not finished yet");
                Assert.AreEqual(0, session.Query("units")["boxes"].Count);
                Assert.AreEqual(0, State(session)["locked"].AsInt());
                Assert.AreEqual(0.0, Tint(session, 1, 1), 1e-9);

                // The last cell is (1,3), a corner of the box: the farthest cell is four steps away.
                SubmitResult last = PlaceOk(session, 1, 3, 1);
                List<ViewEvent> waits = new List<ViewEvent>();
                foreach (ViewEvent e in last.Events)
                {
                    if (e.Kind == ViewEventKind.Effect)
                        waits.Add(e);
                }
                Assert.AreEqual(6, waits.Count, "a step for every distance from 0 to 4, and one for the last cells to settle");
                foreach (ViewEvent wait in waits)
                    Assert.Greater(wait.Seconds, 0.0);
                Assert.AreEqual(0.09, waits[waits.Count - 1].Seconds, 1e-9, "the step that has none behind it");
                for (int k = 1; k < waits.Count; k++)
                    Assert.Less(waits[k].Seconds, waits[k - 1].Seconds, "the more steps wait behind a step, the longer it is made");

                // The tiles light up one distance after the other, with a step between the distances.
                List<int> order = WaveOrder(session, last.Events, 1, 3, out int firstLit, out int lastLit);
                Assert.AreEqual(9, order.Count, "every cell of the box lights up once");
                Assert.AreEqual(0, order[0], "the wave starts at the cell that was filled");
                Assert.AreEqual(4, order[order.Count - 1]);
                for (int k = 1; k < order.Count; k++)
                    Assert.IsTrue(order[k] >= order[k - 1], "cell " + k + " lights up after the ones that are nearer");
                int stepsBetween = 0;
                for (int k = firstLit; k < lastLit; k++)
                {
                    if (last.Events[k].Kind == ViewEventKind.Effect)
                        stepsBetween++;
                }
                Assert.AreEqual(4, stepsBetween);
                Assert.AreEqual(ViewEventKind.Spawn, last.Events[0].Kind, "the digit comes first");

                // When it is over the whole box is marked as finished and its digits are locked.
                JsonValue units = session.Query("units");
                Assert.AreEqual(1, units["boxes"].Count);
                Assert.AreEqual(1, units["boxes"][0].AsInt());
                Assert.AreEqual(0, units["rows"].Count);
                Assert.AreEqual(0, units["columns"].Count);
                Assert.AreEqual(4, State(session)["locked"].AsInt());
                for (int y = 1; y <= 3; y++)
                {
                    for (int x = 1; x <= 3; x++)
                    {
                        Assert.AreEqual(0.16, Tint(session, x, y), 1e-9, "the box is tinted: " + x + "," + y);
                        Assert.AreEqual("#34c38f", Tile(session, x, y).Props["color"].AsString());
                        ViewObject digit = DigitAt(session, x, y);
                        Assert.AreEqual(0.82, digit.Props["label_size"].AsDouble(0.82), 1e-9, "the digit is back to its size");
                        if (digit.Props["given"].AsBool())
                        {
                            Assert.AreEqual("#2f343d", digit.Props["label_color"].AsString());
                            Assert.IsFalse(digit.Props["locked"].AsBool(true));
                        }
                        else
                        {
                            Assert.AreEqual("#1f9d6b", digit.Props["label_color"].AsString());
                            Assert.AreEqual("#1f9d6b", digit.Props["color"].AsString());
                            Assert.IsTrue(digit.Props["locked"].AsBool());
                        }
                    }
                }
                Assert.AreEqual(0.0, Tint(session, 4, 1), 1e-9, "the cells around it are not");
                Assert.AreEqual(0.0, Tint(session, 1, 4), 1e-9);

                // Nothing in the box can be changed any more.
                long version = session.View.Version;
                int history = session.HistoryCount;
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 9).Code);
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 4).Code);
                Assert.AreEqual("CELL_LOCKED", Do(session, "clear", 3, 1).Code);
                Assert.AreEqual("CELL_LOCKED", Do(session, "cycle", 3, 1).Code);
                Assert.AreEqual("CELL_LOCKED", Submit(session, "{\"action\":\"note\",\"x\":3,\"y\":1,\"digit\":2}").Code);
                Assert.AreEqual("GIVEN_CELL", Do(session, "clear", 1, 1).Code, "a clue is still a clue");
                Assert.AreEqual(version, session.View.Version, "a refused command changes nothing");
                Assert.AreEqual(history, session.HistoryCount);
                Assert.AreEqual("4", session.Query("state")["board"].AsString().Substring(2, 1));
                foreach (JsonValue command in session.LegalCommands(1))
                    Assert.IsFalse(command["x"].AsInt() <= 3 && command["y"].AsInt() <= 3, "no command touches the box: " + command.ToJson());

                // The same goes for taps. A tap on a locked digit with another digit chosen chooses its digit.
                Assert.IsTrue(TapKey(session, 10).IsAccepted);
                Assert.AreEqual("CELL_LOCKED", TapCell(session, 3, 1).Code);
                Assert.IsTrue(TapKey(session, 10).IsAccepted);
                Assert.IsTrue(TapKey(session, 4).IsAccepted);
                Assert.AreEqual("CELL_LOCKED", TapCell(session, 3, 1).Code, "the digit that is chosen is the one on it");
                Assert.IsTrue(TapKey(session, 7).IsAccepted);
                Assert.AreEqual("CELL_FILLED", TapCell(session, 3, 1, true).Code);
                Assert.IsTrue(TapCell(session, 3, 1).IsAccepted);
                Assert.AreEqual(4, State(session)["selected"].AsInt());
                Assert.AreEqual("4", session.Query("state")["board"].AsString().Substring(2, 1));

                // The cells around it are free.
                Assert.IsTrue(Place(session, 4, 1, 6).IsAccepted);
            }
        }

        [Test]
        public void EveryStepOfTheWaveTakesItsTimeOnTheScreen()
        {
            using (GameSession session = StartClassic())
            {
                CompleteBox(session, 1, 1);
                SubmitResult last = PlaceOk(session, 1, 3, 1);

                // The table plays steps faster while others wait behind them; the script has allowed for that.
                PlaybackQueue queue = new PlaybackQueue();
                foreach (ViewEvent e in last.Events)
                    queue.Enqueue(e);
                double seconds = 0;
                while (queue.IsBusy && seconds < 10)
                {
                    queue.Update(0.001);
                    seconds += 0.001;
                }
                Assert.AreEqual(6 * 0.09, seconds, 0.03, "six steps of 0.09 seconds");
            }
        }

        // Plays the events of a move into a second model, the way the screen does, and returns it.
        private static ViewModel PlayOnTheScreen(ViewModel before, IEnumerable<ViewEvent> events)
        {
            PlaybackQueue queue = new PlaybackQueue();
            queue.Reset(before);
            foreach (ViewEvent e in events)
                queue.Enqueue(e);
            double seconds = 0;
            while (queue.IsBusy && seconds < 10)
            {
                queue.Update(0.01);
                seconds += 0.01;
            }
            Assert.IsFalse(queue.IsBusy, "the wave ends");
            return queue.Display;
        }

        [Test]
        public void TheTableEndsUpWhereTheGameIsOnceTheWaveHasRun()
        {
            // What the table shows after a wave is what the game holds, so that none of the lit-up looks of the wave
            // stays behind. A digit is chosen, so that the tiles of the box are pointed out as well, which the wave
            // has to give back.
            using (GameSession session = StartClassic())
            {
                TapKey(session, 4);
                CompleteBox(session, 1, 1);
                ViewModel before = new ViewModel();
                foreach (ViewEvent e in session.View.Synthesize())
                    before.Apply(e);

                SubmitResult box = PlaceOk(session, 1, 3, 1);
                Assert.AreEqual(6, Count(box.Events, "fx"));
                Assert.AreEqual(Describe(session.View), Describe(PlayOnTheScreen(before, box.Events)));
            }

            // The whole puzzle, which finishes a row, a column and a box with the last cell and is over after that.
            using (GameSession session = StartClassic())
            {
                Complete(session, 1);
                ViewModel before = new ViewModel();
                foreach (ViewEvent e in session.View.Synthesize())
                    before.Apply(e);

                int cell = Puzzle.LastIndexOf('0');
                SubmitResult last = PlaceOk(session, cell % 9 + 1, cell / 9 + 1, Solved[cell] - '0');
                ViewModel shown = PlayOnTheScreen(before, last.Events);
                Assert.AreEqual(Describe(session.View), Describe(shown));
                Assert.IsTrue(shown.IsOver);
            }
        }

        [Test]
        public void TheLastCellFinishesItsRowItsColumnAndItsBoxInOneWave()
        {
            using (GameSession session = StartClassic())
            {
                Complete(session, 1);
                int cell = Puzzle.LastIndexOf('0');
                int x = cell % 9 + 1;
                int y = cell / 9 + 1;
                JsonValue before = session.Query("units");
                Assert.AreEqual(8, before["rows"].Count, "all the rows but the last are finished");
                Assert.AreEqual(8, before["columns"].Count);
                Assert.AreEqual(8, before["boxes"].Count);

                SubmitResult result = PlaceOk(session, x, y, Solved[cell] - '0');
                Assert.AreEqual(9, session.Query("units")["rows"].Count);
                Assert.AreEqual(9, session.Query("units")["columns"].Count);
                Assert.AreEqual(9, session.Query("units")["boxes"].Count);
                Assert.AreEqual(9, session.Query("units")["rows"][8].AsInt());

                // One wave for the three units: the cells of the row, the column and the box light up once each,
                // nearest first, and the farthest cell is eight steps away (the top of the column).
                List<int> order = WaveOrder(session, result.Events, x, y, out int firstLit, out int lastLit);
                Assert.AreEqual(21, order.Count, "9 + 9 + 9 cells, less the ones that are in two of the units");
                Assert.AreEqual(0, order[0]);
                Assert.AreEqual(8, order[order.Count - 1]);
                for (int k = 1; k < order.Count; k++)
                    Assert.IsTrue(order[k] >= order[k - 1]);
                Assert.AreEqual(10, Count(result.Events, "fx"));

                // The puzzle is over once the wave has run, not before.
                int lastWait = -1;
                int over = -1;
                for (int k = 0; k < result.Events.Count; k++)
                {
                    if (result.Events[k].Kind == ViewEventKind.Effect)
                        lastWait = k;
                    if (result.Events[k].Type == "over")
                        over = k;
                }
                Assert.Greater(lastWait, lastLit);
                Assert.Greater(over, lastWait);
                Assert.IsTrue(session.View.IsOver);
                for (int j = 1; j <= 9; j++)
                {
                    for (int i = 1; i <= 9; i++)
                        Assert.AreEqual(0.16, Tint(session, i, j), 1e-9);
                }
                Assert.AreEqual(51, State(session)["locked"].AsInt(), "every digit of the player");
            }
        }

        [Test]
        public void UndoOpensAFinishedBoxAndRedoLocksItAgain()
        {
            using (GameSession session = StartClassic())
            {
                CompleteBox(session, 1);
                string finished = Describe(session.View);
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 9).Code);

                Assert.IsTrue(session.Undo());
                Assert.AreEqual(0, session.Query("units")["boxes"].Count);
                Assert.AreEqual(0, State(session)["locked"].AsInt());
                Assert.AreEqual(0.0, Tint(session, 2, 2), 1e-9);
                Assert.AreEqual("#4d8fe6", DigitAt(session, 3, 1).Props["color"].AsString());
                Assert.IsFalse(DigitAt(session, 3, 1).Props["locked"].AsBool(true));

                Assert.IsTrue(session.Redo());
                Assert.AreEqual(finished, Describe(session.View));
                Assert.AreEqual(1, session.Query("units")["boxes"].Count);
                Assert.AreEqual(4, State(session)["locked"].AsInt());
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 9).Code);

                // With the last digit taken back, the digits of the box can be changed again.
                Assert.IsTrue(session.Undo());
                Assert.IsTrue(Place(session, 3, 1, 9).IsAccepted);
                Assert.AreEqual('9', State(session)["board"].AsString()[2]);
                Assert.IsTrue(Place(session, 3, 1, 4).IsAccepted);
                SubmitResult again = PlaceOk(session, 1, 3, 1);
                Assert.AreEqual(6, Count(again.Events, "fx"), "finishing it again plays the wave again");
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 9).Code);
            }
        }

        [Test]
        public void ASnapshotKeepsAFinishedBoxLocked()
        {
            using (GameSession session = StartClassic())
            {
                CompleteBox(session, 1);
                string snapshot = session.CaptureSnapshot();
                string table = Describe(session.View);

                session.NewGame(TestSupport.Options("{\"seed\":5}"));
                session.LoadSnapshot(snapshot);
                Assert.AreEqual(table, Describe(session.View));
                Assert.AreEqual(1, session.Query("units")["boxes"].Count);
                Assert.AreEqual(4, State(session)["locked"].AsInt());
                Assert.AreEqual("CELL_LOCKED", Place(session, 3, 1, 9).Code);

                // The game goes on from there: the next box that is finished plays its wave, and only that one.
                SubmitResult next = CompleteBox(session, 2);
                Assert.AreEqual(2, session.Query("units")["boxes"].Count);
                Assert.Greater(Count(next.Events, "fx"), 0);
                Assert.AreEqual(0.16, Tint(session, 5, 2), 1e-9);
                Assert.AreEqual("CELL_LOCKED", Place(session, 4, 1, 9).Code);
                Assert.AreEqual(9, State(session)["locked"].AsInt());
            }
        }

        [Test]
        public void ADigitOfAFinishedBoxIsNeverTheOneShownInConflict()
        {
            using (GameSession session = StartClassic())
            {
                CompleteBox(session, 1);

                // The 4 at (3,1) is locked. Another 4 in its row clashes with it, and that one is the wrong one.
                SubmitResult clash = PlaceOk(session, 4, 1, 4);
                Assert.IsTrue(clash.Events[0].Props["conflict"].AsBool());
                Assert.AreEqual("#ff5c5c", clash.Events[0].Props["color"].AsString());
                ViewObject locked = DigitAt(session, 3, 1);
                Assert.IsFalse(locked.Props["conflict"].AsBool(true));
                Assert.AreEqual("#1f9d6b", locked.Props["color"].AsString());
                Assert.AreEqual(1, State(session)["conflicts"].AsInt());
                JsonValue conflicts = session.Query("conflicts");
                Assert.AreEqual(1, conflicts.Count);
                Assert.AreEqual(4, conflicts[0]["x"].AsInt());
            }
        }

        [Test]
        public void ADigitThatWasShownInConflictIsRightOnceItsUnitIsFinished()
        {
            using (GameSession session = StartClassic())
            {
                // A wrong 4 in the row of (3,1) makes the digits that are right there clash with it.
                PlaceOk(session, 4, 1, 4);
                CompleteBox(session, 1, 1);
                Assert.IsTrue(DigitAt(session, 3, 1).Props["conflict"].AsBool(), "it clashes with the 4 in its row");
                int before = State(session)["conflicts"].AsInt();
                Assert.AreEqual(2, before);

                SubmitResult last = PlaceOk(session, 1, 3, 1);
                ViewObject digit = DigitAt(session, 3, 1);
                Assert.IsFalse(digit.Props["conflict"].AsBool(true), "the box is right, so the digit in it is right");
                Assert.AreEqual("#1f9d6b", digit.Props["color"].AsString());
                Assert.AreEqual(1, State(session)["conflicts"].AsInt(), "only the wrong 4 is left");
                Assert.AreEqual(1, session.Query("conflicts").Count);
                Assert.Greater(Count(last.Events, "fx"), 0);
            }
        }

        [Test]
        public void TheComputerStillSolvesWhenWrongDigitsHaveUsedUpADigit()
        {
            using (GameSession session = StartClassic())
            {
                // Six wrong 5s use up the 5, so that the cells where it belongs can not take it yet.
                int wrong = 0;
                for (int i = 0; i < 81 && wrong < 6; i++)
                {
                    if (Puzzle[i] == '0' && Solved[i] != '5')
                    {
                        PlaceOk(session, i % 9 + 1, i / 9 + 1, 5);
                        wrong++;
                    }
                }
                Assert.IsTrue(Gone(session, 5));
                LetTheComputerPlay(session);
                Assert.AreEqual(Solved, State(session)["board"].AsString());
                Assert.IsTrue(session.View.IsOver);
                Assert.IsTrue(session.RandomCommand(1, 1).IsNull);
            }
        }

        [Test]
        public void AUnitThatIsGivenFromTheStartIsFinishedWithoutAWave()
        {
            string puzzle = Solved.Substring(0, 9) + new string('0', 72);
            using (GameSession session = Start("{\"puzzle\":\"" + puzzle + "\"}"))
            {
                JsonValue units = session.Query("units");
                Assert.AreEqual(1, units["rows"].Count);
                Assert.AreEqual(1, units["rows"][0].AsInt());
                Assert.AreEqual(0, units["columns"].Count);
                Assert.AreEqual(0, units["boxes"].Count);
                for (int x = 1; x <= 9; x++)
                    Assert.AreEqual(0.16, Tint(session, x, 1), 1e-9, "the row is tinted from the start");
                Assert.AreEqual(0.0, Tint(session, 1, 2), 1e-9);
                Assert.AreEqual(0, State(session)["locked"].AsInt(), "the digits of the puzzle are clues, not locks");
                Assert.AreEqual("GIVEN_CELL", Do(session, "clear", 1, 1).Code);

                // Nothing of it is played again when the player goes on: the row is finished already.
                SubmitResult result = PlaceOk(session, 1, 2, Solved[9] - '0');
                Assert.AreEqual(0, Count(result.Events, "fx"));
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

        // ---- Two players at one grid: seat 1 is blue, seat 2 is orange ----

        private const string Blue = "#4d8fe6";
        private const string Orange = "#e68a2e";
        private const string Both = "#2f343d";
        private const string Plain = "#e8ecf2";

        private static SubmitResult TapAs(GameSession session, int seat, int id, bool hold = false)
        {
            return session.Submit(InputPacket.Action(seat, "tap").With("id", id).With("button", 0).With("shift", false).With("hold", hold));
        }

        private static SubmitResult TapKeyAs(GameSession session, int seat, int key)
        {
            return TapAs(session, seat, Key(session, key).Id);
        }

        private static SubmitResult TapCellAs(GameSession session, int seat, int x, int y, bool hold = false)
        {
            return TapAs(session, seat, Tile(session, x, y).Id, hold);
        }

        private static SubmitResult PlaceAs(GameSession session, int seat, int x, int y, int digit)
        {
            return session.Submit(InputPacket.Action(seat, "place").With("x", x).With("y", y).With("digit", digit));
        }

        private static string KeyColor(GameSession session, int key)
        {
            return Key(session, key).Props["color"].AsString();
        }

        private static string TileColor(GameSession session, int x, int y)
        {
            return Tile(session, x, y).Props["color"].AsString();
        }

        private static string Chosen(GameSession session)
        {
            return State(session)["chosen"].ToJson();
        }

        // The small label of a note in a cell, by its text; null when the cell does not have it.
        private static JsonValue NoteLabel(GameSession session, int x, int y, string text)
        {
            JsonValue labels = Tile(session, x, y).Props["labels"];
            if (labels.IsArray)
            {
                foreach (JsonValue label in labels.Items)
                {
                    if (label["text"].AsString() == text)
                        return label;
                }
            }
            return null;
        }

        [Test]
        public void EachSeatChoosesOnAPadOfItsOwn()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKeyAs(session, 1, 5).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 7).IsAccepted);
                Assert.AreEqual("[5,7]", Chosen(session));
                Assert.AreEqual(5, State(session)["selected"].AsInt(), "selected is what the first seat chose");
                Assert.AreEqual(Blue, KeyColor(session, 5));
                Assert.AreEqual(Orange, KeyColor(session, 7));
                Assert.AreEqual("#ffffff", Key(session, 7).Props["label_color"].AsString());
                Assert.AreEqual(Plain, KeyColor(session, 4));

                // Letting go is one's own business: the other keeps what they chose.
                Assert.IsTrue(TapKeyAs(session, 1, 5).IsAccepted);
                Assert.AreEqual("[0,7]", Chosen(session));
                Assert.AreEqual(Plain, KeyColor(session, 5));
                Assert.AreEqual(Orange, KeyColor(session, 7));

                // Both on one key: it is the color of neither, and when one lets go it is the other's again.
                Assert.IsTrue(TapKeyAs(session, 1, 7).IsAccepted);
                Assert.AreEqual("[7,7]", Chosen(session));
                Assert.AreEqual(Both, KeyColor(session, 7));
                Assert.IsTrue(TapKeyAs(session, 2, 7).IsAccepted);
                Assert.AreEqual(Blue, KeyColor(session, 7));

                // The keys of the keyboard are commands too, and each seat has its own eraser.
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "select").With("digit", 10)).IsAccepted);
                Assert.AreEqual("[7,10]", Chosen(session));
                Assert.AreEqual(Orange, KeyColor(session, 10));
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "select").With("digit", 0)).IsAccepted);
                Assert.AreEqual("[7,0]", Chosen(session));
                Assert.AreEqual("UNCHANGED", session.Submit(InputPacket.Action(2, "select").With("digit", 0)).Code);
                Assert.AreEqual(0, State(session)["moves"].AsInt(), "choosing is not a move");
            }
        }

        [Test]
        public void ATapEntersTheDigitOfTheSeatThatTapped()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKeyAs(session, 1, 4).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 6).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 1, 3, 1).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 2, 4, 1).IsAccepted);
                string board = State(session)["board"].AsString();
                Assert.AreEqual('4', board[2]);
                Assert.AreEqual('6', board[3]);
                Assert.AreEqual("[4,6]", Chosen(session), "entering a digit does not let go of the choice");

                // Who put what: 0 for the clues and the empty cells.
                string owners = State(session)["owners"].AsString();
                Assert.AreEqual(81, owners.Length);
                Assert.AreEqual("001200000", owners.Substring(0, 9));
                Assert.AreEqual(2, State(session)["moves"].AsInt());

                // A tap with nothing chosen picks the digit of the cell, for the seat that tapped.
                Assert.IsTrue(TapKeyAs(session, 2, 6).IsAccepted);
                Assert.AreEqual("[4,0]", Chosen(session));
                Assert.IsTrue(TapCellAs(session, 2, 1, 1).IsAccepted);
                Assert.AreEqual("[4,5]", Chosen(session));
            }
        }

        [Test]
        public void TheDigitsAreInTheColorOfTheSeatThatPutThem()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(PlaceAs(session, 1, 3, 1, 4).IsAccepted);
                Assert.IsTrue(PlaceAs(session, 2, 4, 1, 6).IsAccepted);
                Assert.AreEqual(1, DigitAt(session, 3, 1).Owner);
                Assert.AreEqual(Blue, DigitAt(session, 3, 1).Props["color"].AsString());
                Assert.AreEqual(2, DigitAt(session, 4, 1).Owner);
                Assert.AreEqual(Orange, DigitAt(session, 4, 1).Props["color"].AsString());
                Assert.AreEqual(Orange, DigitAt(session, 4, 1).Props["label_color"].AsString());
                Assert.AreEqual(0, DigitAt(session, 1, 1).Owner, "a clue belongs to nobody");
                Assert.AreEqual("#2f343d", DigitAt(session, 1, 1).Props["color"].AsString());

                // The owner of an object does not change, so a digit of the other seat put over it is a new one.
                int digits = Digits(session);
                int old = DigitAt(session, 3, 1).Id;
                Assert.IsTrue(PlaceAs(session, 2, 3, 1, 2).IsAccepted);
                ViewObject now = DigitAt(session, 3, 1);
                Assert.AreNotEqual(old, now.Id);
                Assert.AreEqual(2, now.Owner);
                Assert.AreEqual("2", now.Props["label"].AsString());
                Assert.AreEqual(digits, Digits(session));
                Assert.AreEqual("0022", State(session)["owners"].AsString().Substring(0, 4));

                // A seat that changes its own digit keeps the object.
                int id = DigitAt(session, 3, 1).Id;
                Assert.IsTrue(PlaceAs(session, 2, 3, 1, 1).IsAccepted);
                Assert.AreEqual(id, DigitAt(session, 3, 1).Id);
                Assert.AreEqual("1", DigitAt(session, 3, 1).Props["label"].AsString());
            }
        }

        [Test]
        public void ASeatsChoiceTintsTheGridInItsColor()
        {
            using (GameSession session = StartClassic())
            {
                // Orange looks for the 5s: (1,1), (6,2) and (9,8).
                Assert.IsTrue(TapKeyAs(session, 2, 5).IsAccepted);
                Assert.AreEqual(Orange, TileColor(session, 1, 1));
                Assert.AreEqual(0.45, Tint(session, 1, 1), 1e-9);
                Assert.AreEqual(Orange, TileColor(session, 4, 1));
                Assert.AreEqual(0.14, Tint(session, 4, 1), 1e-9, "the row of a 5");
                Assert.AreEqual(0.0, Tint(session, 3, 4), 1e-9, "a 5 can still go here");

                // Blue looks for the 3s: (2,1), (9,4) and (6,5).
                Assert.IsTrue(TapKeyAs(session, 1, 3).IsAccepted);
                Assert.AreEqual(Blue, TileColor(session, 2, 1));
                Assert.AreEqual(0.45, Tint(session, 2, 1), 1e-9, "a 3 in the row of a 5 is the 3 that counts");
                Assert.AreEqual(Orange, TileColor(session, 1, 1));
                Assert.AreEqual(0.45, Tint(session, 1, 1), 1e-9, "and a 5 in the row of a 3 is the 5");
                Assert.AreEqual(Blue, TileColor(session, 7, 5));
                Assert.AreEqual(0.14, Tint(session, 7, 5), 1e-9, "only a 3 reaches here");
                Assert.AreEqual(Orange, TileColor(session, 7, 2));
                Assert.AreEqual(0.14, Tint(session, 7, 2), 1e-9, "only a 5 reaches here");
                Assert.AreEqual("#69727f", TileColor(session, 3, 1));
                Assert.AreEqual(0.13, Tint(session, 3, 1), 1e-9, "both reach here: the color of neither");

                // Both look for the same digit: the cells that hold it are gray and strong.
                Assert.IsTrue(TapKeyAs(session, 1, 5).IsAccepted);
                Assert.AreEqual("#69727f", TileColor(session, 1, 1));
                Assert.AreEqual(0.35, Tint(session, 1, 1), 1e-9);
                Assert.AreEqual(0.13, Tint(session, 4, 1), 1e-9);
                Assert.AreEqual(0.0, Tint(session, 3, 4), 1e-9);

                // Nobody chooses any more, and the grid is plain again.
                Assert.IsTrue(TapKeyAs(session, 1, 5).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 5).IsAccepted);
                for (int y = 1; y <= 9; y++)
                {
                    for (int x = 1; x <= 9; x++)
                        Assert.AreEqual(0.0, Tint(session, x, y), 1e-9, x + "," + y);
                }
            }
        }

        [Test]
        public void TheNotesLightUpInTheColorOfTheSeatThatChoseThem()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKeyAs(session, 1, 4).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 1, 3, 1, true).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 6).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 2, 3, 1, true).IsAccepted);
                Assert.AreEqual("46", Notes(session, 3, 1), "the notes are everybody's");
                Assert.AreEqual(Blue, NoteLabel(session, 3, 1, "4")["color"].AsString());
                Assert.IsTrue(NoteLabel(session, 3, 1, "4")["bold"].AsBool());
                Assert.AreEqual(Orange, NoteLabel(session, 3, 1, "6")["color"].AsString());
                Assert.IsTrue(NoteLabel(session, 3, 1, "6")["bold"].AsBool());

                // A note that both chose is lit in the color of neither, one that nobody chose is left plain.
                Assert.IsTrue(TapKeyAs(session, 2, 4).IsAccepted);
                Assert.AreEqual(Both, NoteLabel(session, 3, 1, "4")["color"].AsString());
                Assert.AreEqual("#69727f", NoteLabel(session, 3, 1, "6")["color"].AsString());
                Assert.IsFalse(NoteLabel(session, 3, 1, "6")["bold"].AsBool());

                // A note of one seat is taken out by the other's tap as well: it is a note of the table.
                Assert.IsTrue(TapCellAs(session, 2, 3, 1, true).IsAccepted);
                Assert.AreEqual("6", Notes(session, 3, 1));
            }
        }

        [Test]
        public void ADigitOfTheOtherSeatIsNotTakenOutByTheSameDigitAgain()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKeyAs(session, 1, 4).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 4).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 1, 3, 1).IsAccepted);

                // The tap of the other seat most likely crossed the first one.
                SubmitResult crossed = TapCellAs(session, 2, 3, 1);
                Assert.AreEqual("NOT_YOURS", crossed.Code);
                Assert.AreEqual('4', State(session)["board"].AsString()[2]);
                Assert.AreEqual(1, State(session)["moves"].AsInt(), "the refused tap was not a move");

                // The seat that put it there takes it out with the same tap, and so does the eraser of the other.
                Assert.IsTrue(TapCellAs(session, 1, 3, 1).IsAccepted);
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
                Assert.IsTrue(TapCellAs(session, 2, 3, 1).IsAccepted);
                Assert.AreEqual('4', State(session)["board"].AsString()[2]);
                Assert.IsTrue(TapKeyAs(session, 1, 10).IsAccepted);
                Assert.IsTrue(TapCellAs(session, 1, 3, 1).IsAccepted);
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
                Assert.AreEqual("000000000", State(session)["owners"].AsString().Substring(0, 9));
            }
        }

        [Test]
        public void NobodyWaitsForATurn()
        {
            using (GameSession session = StartClassic())
            {
                Assert.AreEqual(1, session.View.Turn);
                Assert.IsTrue(PlaceAs(session, 2, 3, 1, 4).IsAccepted);
                Assert.IsTrue(PlaceAs(session, 2, 4, 1, 6).IsAccepted);
                Assert.IsTrue(PlaceAs(session, 1, 7, 1, 9).IsAccepted);
                Assert.IsTrue(PlaceAs(session, 2, 8, 1, 1).IsAccepted);
                Assert.AreEqual("002200120", State(session)["owners"].AsString().Substring(0, 9));
                Assert.AreEqual(1, session.View.Turn, "the turn only names the seat a simulation plays");

                // A seat that is not at the table is turned away by the session before the rules see it.
                SubmitResult stranger = session.Submit(InputPacket.Action(3, "select").With("digit", 1));
                Assert.IsFalse(stranger.IsAccepted);
                Assert.AreEqual("BAD_SEAT", stranger.Code);
            }
        }

        [Test]
        public void ADigitThatIsUsedUpIsLetGoOfByBothSeats()
        {
            using (GameSession session = StartClassic())
            {
                Assert.IsTrue(TapKeyAs(session, 1, 5).IsAccepted);
                Assert.IsTrue(TapKeyAs(session, 2, 5).IsAccepted);
                Assert.AreEqual(Both, KeyColor(session, 5));
                int placed = 0;
                int firstX = 0;
                int firstY = 0;
                for (int i = 0; i < 81 && placed < 6; i++)
                {
                    if (Puzzle[i] != '0' || Solved[i] != '5')
                        continue;
                    placed++;
                    if (placed == 1)
                    {
                        firstX = i % 9 + 1;
                        firstY = i / 9 + 1;
                    }
                    Assert.IsTrue(TapCellAs(session, placed % 2 + 1, i % 9 + 1, i / 9 + 1).IsAccepted);
                }
                Assert.AreEqual("[0,0]", Chosen(session));
                Assert.IsTrue(Gone(session, 5));
                Assert.AreEqual("DIGIT_USED", TapKeyAs(session, 2, 5).Code);
                Assert.AreEqual("DIGIT_USED", TapKeyAs(session, 1, 5).Code);

                // Taking one of them out brings the key back for whoever chooses it.
                Assert.IsTrue(session.Submit(InputPacket.Action(2, "clear").With("x", firstX).With("y", firstY)).IsAccepted);
                Assert.IsFalse(Gone(session, 5));
                Assert.IsTrue(TapKeyAs(session, 2, 5).IsAccepted);
                Assert.AreEqual("[0,5]", Chosen(session));
                Assert.AreEqual(Orange, KeyColor(session, 5));
            }
        }

        [Test]
        public void AFinishedBoxIsLockedForBothSeats()
        {
            using (GameSession session = StartClassic())
            {
                // The empty cells of the top left box are (3,1), (2,2), (3,2) and (1,3); the first seat puts the first
                // three and the second one the last, which starts the wave.
                int[][] cells = { new[] { 3, 1 }, new[] { 2, 2 }, new[] { 3, 2 }, new[] { 1, 3 } };
                SubmitResult last = null;
                for (int k = 0; k < cells.Length; k++)
                {
                    int x = cells[k][0];
                    int y = cells[k][1];
                    last = PlaceAs(session, k < 3 ? 1 : 2, x, y, Solved[(y - 1) * 9 + x - 1] - '0');
                    Assert.IsTrue(last.IsAccepted, last.ToString());
                }
                Assert.Greater(Count(last.Events, "fx"), 0, "the wave plays for the table");
                Assert.AreEqual("001000000" + "011000000" + "200000000", State(session)["owners"].AsString().Substring(0, 27));

                // Locked is locked for both of them, and the locked digits are green whoever put them there.
                Assert.AreEqual("CELL_LOCKED", PlaceAs(session, 1, 3, 1, 1).Code);
                Assert.AreEqual("CELL_LOCKED", PlaceAs(session, 2, 3, 1, 1).Code);
                Assert.AreEqual("CELL_LOCKED", PlaceAs(session, 2, 1, 3, 9).Code);
                Assert.AreEqual("#1f9d6b", DigitAt(session, 3, 1).Props["color"].AsString());
                Assert.AreEqual("#1f9d6b", DigitAt(session, 1, 3).Props["color"].AsString());
                Assert.AreEqual("#2f343d", DigitAt(session, 1, 1).Props["color"].AsString(), "a clue stays as it was");

                // Tapping a locked digit picks it for the seat that tapped, unless that seat has it chosen already.
                Assert.IsTrue(TapKeyAs(session, 2, 7).IsAccepted);
                Assert.AreEqual("CELL_LOCKED", TapCellAs(session, 2, 2, 2).Code);
                Assert.IsTrue(TapCellAs(session, 1, 2, 2).IsAccepted);
                Assert.AreEqual("[7,7]", Chosen(session));
            }
        }

        [Test]
        public void ThePuzzleIsSolvedTogetherAndTheStatusSaysWhoDidWhat()
        {
            using (GameSession session = StartClassic())
            {
                SubmitResult last = null;
                int k = 0;
                for (int i = 0; i < 81; i++)
                {
                    if (Puzzle[i] != '0')
                        continue;
                    last = PlaceAs(session, k % 2 + 1, i % 9 + 1, i / 9 + 1, Solved[i] - '0');
                    Assert.IsTrue(last.IsAccepted, "move " + k + ": " + last);
                    k++;
                }
                Assert.AreEqual(51, k);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual("solved", session.View.Over.Reason);
                Assert.AreEqual(1, session.View.Over.Winner, "the last digit was the first seat's");
                Assert.AreEqual("Solved in 51 moves: Blue 26, Orange 25", Find(last.Events, "status").Text);
                Assert.AreEqual("GAME_OVER", PlaceAs(session, 2, 3, 1, 1).Code);

                // The digits stay in the colors of those who put them, or green where a finished unit locked them.
                string owners = State(session)["owners"].AsString();
                int blue = 0;
                int orange = 0;
                foreach (char c in owners)
                {
                    blue += c == '1' ? 1 : 0;
                    orange += c == '2' ? 1 : 0;
                }
                Assert.AreEqual(26, blue);
                Assert.AreEqual(25, orange);
            }
        }

        [Test]
        public void APuzzleSolvedByOneSeatAloneIsReportedWithoutNames()
        {
            using (GameSession session = StartClassic())
            {
                SubmitResult last = Complete(session);
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual("Solved in 51 moves", Find(last.Events, "status").Text);
            }
        }

        [Test]
        public void TheSecondSeatsChoiceAndDigitsComeBackWithUndoAndSnapshots()
        {
            using (GameSession session = StartClassic())
            {
                TapKeyAs(session, 1, 4);
                TapKeyAs(session, 2, 6);
                TapCellAs(session, 2, 4, 1);
                TapCellAs(session, 1, 3, 1);

                // Undo walks back through the moves of both seats.
                Assert.IsTrue(session.Undo());
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
                Assert.IsTrue(session.Undo());
                Assert.AreEqual('0', State(session)["board"].AsString()[3]);
                Assert.AreEqual("000000000", State(session)["owners"].AsString().Substring(0, 9));
                Assert.AreEqual("[4,6]", Chosen(session));
                Assert.IsTrue(session.Undo());
                Assert.AreEqual("[4,0]", Chosen(session));
                Assert.AreEqual(Plain, KeyColor(session, 6));
                Assert.IsTrue(session.Redo(3));
                Assert.AreEqual("001200000", State(session)["owners"].AsString().Substring(0, 9));
                Assert.AreEqual(Orange, DigitAt(session, 4, 1).Props["color"].AsString());

                string snapshot = session.CaptureSnapshot();
                string table = Describe(session.View);
                TapKeyAs(session, 2, 9);
                PlaceAs(session, 2, 3, 1, 1);
                Assert.AreEqual(2, DigitAt(session, 3, 1).Owner);

                session.LoadSnapshot(snapshot);
                Assert.AreEqual(table, Describe(session.View));
                Assert.AreEqual("[4,6]", Chosen(session));
                Assert.AreEqual("001200000", State(session)["owners"].AsString().Substring(0, 9));
                Assert.AreEqual(1, DigitAt(session, 3, 1).Owner);

                // The game goes on from there for both of them: the second seat still has the 6 chosen, and it fits at (3,5).
                Assert.IsTrue(TapCellAs(session, 2, 3, 5).IsAccepted);
                Assert.AreEqual('6', State(session)["board"].AsString()[38]);
                Assert.AreEqual('2', State(session)["owners"].AsString()[38]);
            }
        }

        [Test]
        public void ASnapshotOfAGameOfOneStillLoads()
        {
            using (GameSession session = StartClassic())
            {
                TapKeyAs(session, 1, 4);
                TapCellAs(session, 1, 3, 1);
                string snapshot = session.CaptureSnapshot();
                string table = Describe(session.View);

                // What a game of one wrote: only what the first seat chose, and no owners.
                JsonValue root = JsonValue.Parse(snapshot);
                JsonValue mod = root["mod"];
                Assert.IsTrue(mod.Has("chosen"));
                Assert.IsTrue(mod.Has("owners"));
                mod.Remove("chosen");
                mod.Remove("owners");
                session.LoadSnapshot(root.ToJson());
                Assert.AreEqual(table, Describe(session.View));
                Assert.AreEqual("[4,0]", Chosen(session));
                Assert.AreEqual("001000000", State(session)["owners"].AsString().Substring(0, 9));
                Assert.IsTrue(TapCellAs(session, 1, 3, 1).IsAccepted);
                Assert.AreEqual('0', State(session)["board"].AsString()[2]);
            }
        }

        [Test]
        public void ASnapshotThatDisagreesAboutWhoPutADigitIsRefused()
        {
            using (GameSession session = StartClassic())
            {
                PlaceAs(session, 2, 3, 1, 4);
                string snapshot = session.CaptureSnapshot();
                JsonValue root = JsonValue.Parse(snapshot);
                // The table says the second seat, the snapshot says the first.
                root["mod"].Set("owners", "001" + new string('0', 78));
                string table = Describe(session.View);
                Assert.Throws<GameSessionException>(() => session.LoadSnapshot(root.ToJson()));
                Assert.AreEqual(table, Describe(session.View), "a refused snapshot changes nothing");

                // A choice that is not on the pad, or a pad for another number of seats.
                foreach (string bad in new[] { "[11,0]", "[0,-1]", "[0]", "[0,0,0]", "[\"a\",0]" })
                {
                    JsonValue broken = JsonValue.Parse(snapshot);
                    broken["mod"].Set("chosen", JsonValue.Parse(bad));
                    Assert.Throws<GameSessionException>(() => session.LoadSnapshot(broken.ToJson()), bad);
                }
            }
        }

        [Test]
        public void TheComputerPlaysEitherSeat()
        {
            using (GameSession session = StartClassic())
            {
                for (int seat = 1; seat <= 2; seat++)
                {
                    JsonValue command = session.RandomCommand(seat, 3);
                    Assert.IsTrue(command.IsObject, "seat " + seat);
                    SubmitResult result = session.Submit(new InputPacket(seat, command));
                    Assert.IsTrue(result.IsAccepted, result.ToString());
                }
                string owners = State(session)["owners"].AsString();
                Assert.IsTrue(owners.Contains("1") && owners.Contains("2"), owners);
                Assert.IsFalse(session.RandomCommand(3, 3).IsObject);
            }
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
