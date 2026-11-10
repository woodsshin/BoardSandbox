using BoardSandbox.Core.Commands;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class ConsoleTests
    {
        private const string NoisyRules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command(seat, cmd)
    if cmd.action == 'log' then print('from the script') end
    if cmd.action == 'boom' then error('boom') end
    if cmd.action == 'ok' then bse.turn(1) end
end
function rules.snapshot() return {} end
function rules.restore() end
return rules";

        private const string FragileRules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command(seat, cmd) if cmd.action == 'boom' then error('boom') end end
return rules";

        private static CommandConsole Console(GameSession session)
        {
            return new CommandConsole(session);
        }

        private static string Lines(string text, int index)
        {
            return text.Split('\n')[index];
        }

        [Test]
        public void BlankLinesAndUnknownCommands()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                Assert.AreEqual(string.Empty, console.Execute(""));
                Assert.AreEqual(string.Empty, console.Execute("   \t "));
                Assert.AreEqual(string.Empty, console.Execute(null));
                StringAssert.StartsWith("unknown command 'frobnicate'", console.Execute("frobnicate now"));
                Assert.AreEqual(console.Execute("help"), console.Execute("  HELP  "));
                Assert.AreEqual(console.Execute("help"), console.Execute("?"));
            }
        }

        [Test]
        public void HelpMentionsEveryCommand()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                string help = Console(session).Execute("help");
                foreach (string name in CommandConsole.CommandNames)
                    StringAssert.Contains(name, help);
            }
        }

        [Test]
        public void NewGameAndStateReportTheTable()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                CommandConsole console = Console(session);
                string before = console.Execute("state");
                StringAssert.Contains("mod: Gomoku 1.0.0", before);
                StringAssert.Contains("no game is running", before);

                string started = console.Execute("new size=9 exact_win=true title=\"two words\"");
                StringAssert.StartsWith("new game started", started);
                StringAssert.Contains("turn: seat 1 (Black)", started);
                StringAssert.Contains("objects: 0", started);
                StringAssert.Contains("history: 0/0", started);
                Assert.AreEqual(9, session.GameOptions["size"].AsInt());
                Assert.IsTrue(session.GameOptions["exact_win"].AsBool());
                Assert.AreEqual("two words", session.GameOptions["title"].AsString());

                StringAssert.StartsWith("expected key=value", console.Execute("new size"));
            }
        }

        [Test]
        public void PlayUsesTheSeatOnTurnAndDrawsTheBoard()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":5}")))
            {
                CommandConsole console = Console(session);
                string accepted = console.Execute("play place_stone x=2 y=3");
                StringAssert.StartsWith("accepted", accepted);
                StringAssert.Contains("\"t\":\"spawn\"", accepted);
                Assert.AreEqual(2, session.View.Turn);

                console.Execute("play place_stone x=4 y=3");
                string board = console.Execute("board");
                string[] lines = board.Split('\n');
                Assert.AreEqual(6, lines.Length);
                StringAssert.Contains("1  2  3  4  5", lines[0]);
                StringAssert.Contains("X", lines[3]);
                StringAssert.Contains("[O]", lines[3]);
                StringAssert.DoesNotContain("[", lines[1]);
                Assert.AreEqual(1, session.View.Turn);
            }
        }

        [Test]
        public void PlayReportsRefusalsWithTheirCodes()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                console.Execute("play place_stone x=8 y=8");

                string occupied = console.Execute("play place_stone x=8 y=8");
                StringAssert.StartsWith("rejected OCCUPIED:", occupied);
                StringAssert.Contains("\"x\":8", occupied);

                StringAssert.StartsWith("rejected NOT_YOUR_TURN", console.Execute("play 1 place_stone x=1 y=1"));
                StringAssert.StartsWith("rejected OUT_OF_BOUNDS", console.Execute("play place_stone x=99 y=1"));
                StringAssert.StartsWith("rejected INVALID_ARGUMENT", console.Execute("play place_stone x=1.5 y=1"));
                StringAssert.StartsWith("rejected UNKNOWN_ACTION", console.Execute("play resign"));
                StringAssert.StartsWith("invalid BAD_SEAT", console.Execute("play 7 place_stone x=1 y=1"));
                Assert.AreEqual(1, session.View.ObjectCount);
            }
        }

        [Test]
        public void MalformedInputGetsUsageMessages()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                StringAssert.StartsWith("usage: play", console.Execute("play"));
                StringAssert.StartsWith("usage: play", console.Execute("play 1"));
                StringAssert.StartsWith("expected key=value", console.Execute("play place_stone x"));
                StringAssert.StartsWith("usage: raw", console.Execute("raw"));
                StringAssert.StartsWith("usage: raw", console.Execute("raw x {}"));
                StringAssert.StartsWith("error:", console.Execute("raw 1 {broken"));
                StringAssert.StartsWith("usage: ff", console.Execute("ff"));
                StringAssert.StartsWith("usage: ff", console.Execute("ff 0"));
                StringAssert.StartsWith("usage: ff", console.Execute("ff many"));
                StringAssert.StartsWith("usage: undo", console.Execute("undo x"));
                StringAssert.StartsWith("usage: redo", console.Execute("redo x"));
                StringAssert.StartsWith("usage: goto", console.Execute("goto"));
                StringAssert.StartsWith("usage: save", console.Execute("save"));
                StringAssert.StartsWith("usage: sim", console.Execute("sim"));
                StringAssert.StartsWith("usage: sim", console.Execute("sim 0"));
                StringAssert.StartsWith("usage: query", console.Execute("query"));
                StringAssert.StartsWith("usage: eval", console.Execute("eval"));
                Assert.AreEqual(0, session.View.ObjectCount);
            }
        }

        [Test]
        public void RawSubmitsJsonCommands()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                StringAssert.StartsWith("accepted", console.Execute("raw 1 {\"action\":\"place_stone\",\"x\":3,\"y\":3}"));
                Assert.AreEqual(1, session.View.ObjectCount);
                StringAssert.StartsWith("rejected", console.Execute("raw 2 {\"action\":\"place_stone\",\"x\":3,\"y\":3}"));
            }
        }

        [Test]
        public void LegalListsAPrefixOfTheCommands()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                string text = console.Execute("legal");
                string[] lines = text.Split('\n');
                Assert.AreEqual("225 legal commands for seat 1", lines[0]);
                Assert.AreEqual(26, lines.Length);
                Assert.AreEqual("  ...", lines[25]);
                StringAssert.StartsWith("0 legal commands for seat 2", console.Execute("legal 2"));
            }
        }

        [Test]
        public void RandomAndFastForwardPlayTheGame()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                string one = console.Execute("random seed=5");
                StringAssert.StartsWith("play {", one);
                StringAssert.Contains("accepted", one);
                Assert.AreEqual(1, session.View.ObjectCount);

                string forwarded = console.Execute("ff 10 seed=3");
                StringAssert.StartsWith("played 10 commands; stopped: move limit reached", forwarded);
                Assert.AreEqual(11, session.View.ObjectCount);
            }
        }

        [Test]
        public void FastForwardIsDeterministicAndStopsWhenTheGameEnds()
        {
            string first;
            string second;
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                CommandConsole console = Console(session);
                string text = console.Execute("ff 100 seed=11");
                StringAssert.Contains("stopped: game over", text);
                Assert.IsTrue(session.View.IsOver);
                first = console.Execute("board");
                StringAssert.Contains("game over", console.Execute("ff 5"));
            }

            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                Console(session).Execute("ff 100 seed=11");
                second = Console(session).Execute("board");
            }
            Assert.AreEqual(first, second);
        }

        [Test]
        public void UndoRedoGotoAndHistoryWalkTheGame()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                console.Execute("play place_stone x=1 y=1");
                console.Execute("play place_stone x=2 y=2");
                console.Execute("play place_stone x=3 y=3");

                string history = console.Execute("history");
                string[] lines = history.Split('\n');
                Assert.AreEqual(4, lines.Length);
                StringAssert.Contains("(start)", lines[0]);
                StringAssert.StartsWith("> 3:", lines[3]);
                StringAssert.Contains("\"x\":3", lines[3]);

                StringAssert.StartsWith("undone", console.Execute("undo"));
                Assert.AreEqual(2, session.View.ObjectCount);
                StringAssert.StartsWith("undone", console.Execute("undo 2"));
                Assert.AreEqual(0, session.View.ObjectCount);
                Assert.AreEqual("nothing to undo", console.Execute("undo"));

                StringAssert.StartsWith("redone", console.Execute("redo 2"));
                Assert.AreEqual(2, session.View.ObjectCount);
                StringAssert.StartsWith("at history 3", console.Execute("goto 3"));
                Assert.AreEqual("nothing to redo", console.Execute("redo"));
                Assert.AreEqual("no such history entry", console.Execute("goto 99"));
            }
        }

        [Test]
        public void SaveAndLoadKeepNamedSnapshots()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                console.Execute("play place_stone x=5 y=5");
                StringAssert.StartsWith("saved 'early'", console.Execute("save early"));
                string earlyBoard = console.Execute("board");

                console.Execute("play place_stone x=6 y=6");
                console.Execute("play place_stone x=7 y=7");
                Assert.AreNotEqual(earlyBoard, console.Execute("board"));

                StringAssert.StartsWith("loaded 'early'", console.Execute("load early"));
                Assert.AreEqual(earlyBoard, console.Execute("board"));
                StringAssert.StartsWith("no save named 'nope'", console.Execute("load nope"));
                StringAssert.StartsWith("no save named ''", console.Execute("load"));
            }
        }

        [Test]
        public void SimRunsOnAPrivateVm()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                console.Execute("play place_stone x=8 y=8");
                string before = console.Execute("board");

                string text = console.Execute("sim 40 seed=3");
                StringAssert.StartsWith("40 games in", text);
                StringAssert.Contains("games/s", text);
                Assert.AreEqual(before, console.Execute("board"));
                StringAssert.StartsWith("accepted", console.Execute("play place_stone x=9 y=9"));
            }
        }

        [Test]
        public void SimAcceptsOptionsAndHandlesEmptyResults()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                StringAssert.Contains("aborted 10", console.Execute("sim 10 max_moves=1"));
                string small = console.Execute("sim 30 seed=2 size=3 win_length=3");
                StringAssert.StartsWith("30 games in", small);
                StringAssert.Contains("avg moves", small);
            }
        }

        [Test]
        public void QueryAndEvalAreForwarded()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(session);
                string state = console.Execute("query state");
                StringAssert.Contains("\"size\": 15", state);
                StringAssert.StartsWith("error DEV_ONLY", console.Execute("eval 1 + 1"));
            }

            using (GameSession session = TestSupport.StartGomoku(null, new VmOptions { DevMode = true }))
            {
                Assert.AreEqual("2", Console(session).Execute("eval 1 + 1"));
                StringAssert.StartsWith("error COMPILE_ERROR", Console(session).Execute("eval 1 +"));
            }
        }

        [Test]
        public void LogsStatsAndRejectionsReport()
        {
            using (GameSession session = new GameSession(TestSupport.InlineMod(NoisyRules)))
            {
                session.NewGame();
                CommandConsole console = Console(session);
                Assert.AreEqual("no script output", console.Execute("logs"));
                StringAssert.StartsWith("calls ", console.Execute("stats"));
                Assert.AreEqual("no rejection log is attached", console.Execute("rejections"));

                console.Execute("play log");
                StringAssert.Contains("from the script", console.Execute("logs"));

                session.AddInterceptor(new RejectionLog());
                string faulted = console.Execute("play boom");
                StringAssert.StartsWith("faulted SCRIPT_ERROR:", faulted);
                StringAssert.Contains("the game was restored to the last good state", faulted);
                string report = console.Execute("rejections");
                StringAssert.StartsWith("1 refused or failed commands", report);
                StringAssert.Contains("SCRIPT_ERROR: 1", report);
            }
        }

        [Test]
        public void FaultsWithoutARecoveryPointSayTheGameIsLost()
        {
            using (GameSession session = new GameSession(TestSupport.InlineMod(FragileRules)))
            {
                session.NewGame();
                CommandConsole console = Console(session);
                string faulted = console.Execute("play boom");
                StringAssert.StartsWith("faulted SCRIPT_ERROR:", faulted);
                StringAssert.Contains("the game could not be restored", faulted);
                StringAssert.Contains("no game is running", console.Execute("state"));
            }
        }

        [Test]
        public void CommandsFailGracefullyWithoutAGame()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                CommandConsole console = Console(session);
                Assert.AreEqual("(no grid to draw)", console.Execute("board"));
                StringAssert.StartsWith("invalid NOT_STARTED", console.Execute("play place_stone x=1 y=1"));
                StringAssert.StartsWith("error NOT_STARTED", console.Execute("legal"));
                StringAssert.StartsWith("error NOT_STARTED", console.Execute("query state"));
                Assert.AreEqual("nothing to undo", console.Execute("undo"));
                StringAssert.StartsWith("error NOT_STARTED", console.Execute("save x"));
                StringAssert.Contains("no game is running", console.Execute("ff 3"));
            }
        }

        [Test]
        public void ConsoleFollowsAReplacedSession()
        {
            using (GameSession first = TestSupport.StartGomoku())
            using (GameSession second = TestSupport.StartGomoku())
            {
                CommandConsole console = Console(first);
                console.Execute("play place_stone x=1 y=1");
                console.Session = second;
                console.Execute("play place_stone x=2 y=2");
                Assert.AreEqual(1, first.View.ObjectCount);
                Assert.AreEqual(1, second.View.ObjectCount);
                Assert.Throws<System.ArgumentNullException>(() => new CommandConsole(null));
            }
        }

        [Test]
        public void AsciiBoardFallsBackToObjectPositions()
        {
            ViewModel view = new ViewModel();
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":1,\"kind\":\"p\",\"x\":3,\"y\":2,\"owner\":2}")));
            view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":2,\"kind\":\"p\",\"x\":1,\"y\":1,\"owner\":1}")));
            string[] lines = AsciiBoard.Render(view).Split('\n');
            Assert.AreEqual(3, lines.Length);
            StringAssert.Contains("X", lines[1]);
            StringAssert.Contains("O", lines[2]);
            Assert.AreEqual("(no grid to draw)", AsciiBoard.Render(new ViewModel()));
        }
    }
}
