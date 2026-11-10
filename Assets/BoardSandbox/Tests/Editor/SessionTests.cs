using System;
using System.Collections.Generic;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class SessionTests
    {
        private const string CounterRules = @"
local rules = {}
local count = 0
local on_turn = 1
local counter_id = 0

function rules.new_game(options)
    if options.fail == true then error('cannot start') end
    count = if type(options.start) == 'number' then options.start else 0
    on_turn = 1
    counter_id = bse.view.spawn('counter', 0, 0, { props = { value = count } })
    bse.turn(on_turn)
end

function rules.command(seat, cmd)
    local a = cmd.action
    if a == 'add' then
        local n = cmd.n
        if type(n) ~= 'number' then return bse.reject('BAD_N', 'n must be a number') end
        count += n
        bse.view.set(counter_id, { value = count })
        on_turn = 3 - on_turn
        bse.turn(on_turn)
    elseif a == 'boom' then
        count = -1
        error('boom')
    elseif a == 'spin' then
        count = -2
        while true do end
    elseif a == 'hog' then
        count = -3
        local t = {}
        for i = 1, 1e8 do t[i] = { i } end
    elseif a == 'reject' then
        return bse.reject('NOPE', 'not allowed', { why = 'test' })
    elseif a == 'bad_reject' then
        bse.view.set(counter_id, { value = 99 })
        return bse.reject('LATE', 'too late')
    elseif a == 'log' then
        print('hello')
        bse.warn('careful')
        bse.err('bad')
    elseif a == 'note' then
        bse.emit({ t = 'note', text = cmd.text })
    end
    return nil
end

function rules.query(name)
    if name == 'count' then return count end
    if name == 'boom' then error('query failed') end
    if name == 'spin' then count = -4 while true do end end
    return nil
end

function rules.snapshot() return { count = count, on_turn = on_turn, id = counter_id } end
function rules.restore(data) count = data.count on_turn = data.on_turn counter_id = data.id end
return rules";

        private const string NoSnapshotRules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command(seat, cmd)
    if cmd.action == 'boom' then error('boom') end
end
return rules";

        private static GameSession Counter(SessionOptions options = null, string startOptions = "{}")
        {
            GameSession session = new GameSession(TestSupport.InlineMod(CounterRules), options);
            session.NewGame(JsonValue.Parse(startOptions));
            return session;
        }

        private static SubmitResult Do(GameSession session, int seat, string action)
        {
            return session.Submit(InputPacket.Action(seat, action));
        }

        private static SubmitResult Add(GameSession session, int seat, int n)
        {
            return session.Submit(InputPacket.Action(seat, "add").With("n", n));
        }

        private static int Count(GameSession session)
        {
            return session.Query("count").AsInt();
        }

        private static int ShownValue(GameSession session)
        {
            foreach (ViewObject o in session.View.Objects)
            {
                if (o.Kind == "counter")
                    return o.Props["value"].AsInt();
            }
            return int.MinValue;
        }

        private sealed class ScriptedInterceptor : ICommandInterceptor
        {
            public Func<InputPacket, SubmitResult> Before;
            public Action<InputPacket, SubmitResult> After;
            public int BeforeCalls;
            public int AfterCalls;

            public SubmitResult BeforeSubmit(GameSession session, InputPacket packet)
            {
                BeforeCalls++;
                return Before?.Invoke(packet);
            }

            public void AfterSubmit(GameSession session, InputPacket packet, SubmitResult result)
            {
                AfterCalls++;
                After?.Invoke(packet, result);
            }
        }

        [Test]
        public void ScriptErrorsRollBackToTheLastGoodState()
        {
            using (GameSession session = Counter())
            {
                Add(session, 1, 5);
                Add(session, 2, 3);
                Assert.AreEqual(8, Count(session));
                int history = session.HistoryCount;

                SubmitResult result = Do(session, 1, "boom");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.AreEqual("SCRIPT_ERROR", result.Code);
                StringAssert.Contains("boom", result.Message);
                Assert.IsTrue(result.Recovered);
                Assert.AreNotEqual(string.Empty, result.Traceback);

                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(8, Count(session));
                Assert.AreEqual(8, ShownValue(session));
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(history, session.HistoryCount);
                Assert.IsFalse(session.VmStats.Faulted);

                Assert.IsTrue(Add(session, 1, 2).IsAccepted);
                Assert.AreEqual(10, Count(session));
            }
        }

        [Test]
        public void TimeoutsFaultTheVmAndTheSessionRecovers()
        {
            VmOptions vm = new VmOptions { CallTimeoutMs = 50 };
            using (GameSession session = Counter(new SessionOptions { Vm = vm }))
            {
                Add(session, 1, 4);
                SubmitResult result = Do(session, 2, "spin");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.AreEqual("TIMEOUT", result.Code);
                Assert.IsTrue(result.Recovered);
                Assert.AreEqual(4, Count(session));
                Assert.IsFalse(session.VmStats.Faulted);
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);
                Assert.AreEqual(5, Count(session));
            }
        }

        [Test]
        public void MemoryHogsAreStoppedAndTheSessionRecovers()
        {
            VmOptions vm = new VmOptions { MemoryLimitMb = 8 };
            using (GameSession session = Counter(new SessionOptions { Vm = vm }))
            {
                Add(session, 1, 7);
                SubmitResult result = Do(session, 2, "hog");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.AreEqual("MEMORY", result.Code);
                Assert.IsTrue(result.Recovered);
                Assert.AreEqual(7, Count(session));
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);
            }
        }

        [Test]
        public void ModsWithoutSnapshotsCannotRecoverButCanRestart()
        {
            using (GameSession session = new GameSession(TestSupport.InlineMod(NoSnapshotRules)))
            {
                session.NewGame();
                Assert.IsFalse(session.Capabilities.Snapshot);
                Assert.IsFalse(session.HistoryEnabled);
                Assert.IsFalse(session.CanUndo);
                Assert.IsFalse(session.Undo());
                Assert.Throws<GameSessionException>(() => session.CaptureSnapshot());

                SubmitResult result = Do(session, 1, "boom");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.IsFalse(result.Recovered);
                Assert.IsFalse(session.IsStarted);

                SubmitResult after = Do(session, 1, "anything");
                Assert.AreEqual(SubmitStatus.Invalid, after.Status);
                Assert.AreEqual("NOT_STARTED", after.Code);

                session.NewGame();
                Assert.IsTrue(session.IsStarted);
                Assert.IsTrue(Do(session, 1, "anything").IsAccepted);
            }
        }

        [Test]
        public void AutoRecoverCanBeSwitchedOff()
        {
            using (GameSession session = Counter(new SessionOptions { AutoRecover = false }))
            {
                Add(session, 1, 1);
                SubmitResult result = Do(session, 2, "boom");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.IsFalse(result.Recovered);
                Assert.IsFalse(session.IsStarted);
                Assert.AreEqual("NOT_STARTED", Add(session, 2, 1).Code);

                session.NewGame();
                Assert.IsTrue(Add(session, 1, 1).IsAccepted);
            }
        }

        [Test]
        public void RejectionsKeepTheGameAndItsDetails()
        {
            using (GameSession session = Counter())
            {
                long version = session.View.Version;
                SubmitResult result = Do(session, 1, "reject");
                Assert.AreEqual(SubmitStatus.Rejected, result.Status);
                Assert.AreEqual("NOPE", result.Code);
                Assert.AreEqual("not allowed", result.Message);
                Assert.AreEqual("test", result.Details["why"].AsString());
                Assert.AreEqual(version, session.View.Version);
                Assert.IsFalse(session.VmStats.Faulted);

                SubmitResult badArgument = session.Submit(InputPacket.Action(1, "add").With("n", "x"));
                Assert.AreEqual("BAD_N", badArgument.Code);
                Assert.IsTrue(badArgument.Details.IsNull);
            }
        }

        [Test]
        public void RejectingAfterChangingTheTableIsAFaultThatRollsBack()
        {
            using (GameSession session = Counter())
            {
                Add(session, 1, 3);
                SubmitResult result = Do(session, 2, "bad_reject");
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Assert.AreEqual("INVALID_REJECTION", result.Code);
                Assert.IsTrue(result.Recovered);
                Assert.AreEqual(3, ShownValue(session));
                Assert.AreEqual(3, Count(session));
            }
        }

        [Test]
        public void InvalidPacketsNeverReachTheScript()
        {
            using (GameSession session = Counter())
            {
                long calls = session.VmStats.CallCount;
                Assert.AreEqual("BAD_SEAT", Do(session, 0, "add").Code);
                Assert.AreEqual("BAD_SEAT", Do(session, 3, "add").Code);
                Assert.AreEqual("BAD_SEAT", Do(session, -1, "add").Code);
                Assert.AreEqual("BAD_COMMAND", session.Submit(new InputPacket(1, null)).Code);
                Assert.AreEqual("BAD_COMMAND", session.Submit(new InputPacket(1, JsonValue.NewArray())).Code);
                Assert.AreEqual("BAD_COMMAND", session.Submit(new InputPacket(1, JsonValue.Of("add"))).Code);
                Assert.AreEqual(SubmitStatus.Invalid, Do(session, 0, "add").Status);
                Assert.AreEqual(calls, session.VmStats.CallCount);
                Assert.Throws<ArgumentNullException>(() => session.Submit(null));
            }
        }

        [Test]
        public void TurnGuardStopsOffTurnPacketsBeforeTheScript()
        {
            using (GameSession session = Counter())
            {
                session.AddInterceptor(new TurnGuard());
                long calls = session.VmStats.CallCount;
                SubmitResult vetoed = Add(session, 2, 1);
                Assert.AreEqual(SubmitStatus.Vetoed, vetoed.Status);
                Assert.AreEqual("NOT_YOUR_TURN", vetoed.Code);
                Assert.AreEqual(calls, session.VmStats.CallCount);
                Assert.AreEqual(0, Count(session));

                Assert.IsTrue(Add(session, 1, 1).IsAccepted);
                Assert.AreEqual(SubmitStatus.Vetoed, Add(session, 1, 1).Status);
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);
            }
        }

        [Test]
        public void TurnGuardStepsAsideOnceTheGameIsOver()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                session.AddInterceptor(new TurnGuard());
                TestSupport.PlaceOk(session, 1, 1, 1);
                TestSupport.PlaceOk(session, 2, 1, 2);
                TestSupport.PlaceOk(session, 1, 2, 1);
                TestSupport.PlaceOk(session, 2, 2, 2);
                TestSupport.PlaceOk(session, 1, 3, 1);
                Assert.IsTrue(session.View.IsOver);

                SubmitResult after = session.Submit(TestSupport.Place(2, 3, 3));
                Assert.AreEqual(SubmitStatus.Rejected, after.Status);
                Assert.AreEqual("GAME_OVER", after.Code);
            }
        }

        [Test]
        public void RejectionLogCountsEverythingThatDidNotGoThrough()
        {
            using (GameSession session = Counter())
            {
                RejectionLog log = new RejectionLog(3);
                session.AddInterceptor(new TurnGuard());
                session.AddInterceptor(log);

                Assert.IsTrue(Add(session, 1, 1).IsAccepted);
                Assert.AreEqual(SubmitStatus.Rejected, Do(session, 2, "reject").Status);
                Assert.AreEqual(SubmitStatus.Vetoed, Add(session, 1, 1).Status);
                Assert.AreEqual(SubmitStatus.Faulted, Do(session, 2, "boom").Status);
                Assert.AreEqual(SubmitStatus.Invalid, Do(session, 9, "add").Status);
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);

                Assert.AreEqual(4, log.TotalCount);
                Assert.AreEqual(1, log.CountsByCode["NOPE"]);
                Assert.AreEqual(1, log.CountsByCode["NOT_YOUR_TURN"]);
                Assert.AreEqual(1, log.CountsByCode["SCRIPT_ERROR"]);
                Assert.AreEqual(1, log.CountsByCode["BAD_SEAT"]);
                Assert.IsFalse(log.CountsByCode.ContainsKey("OK"));
                Assert.AreEqual(3, log.Records.Count);

                List<RejectionRecord> records = new List<RejectionRecord>(log.Records);
                Assert.AreEqual("local", records[0].Source);
                Assert.AreEqual(SubmitStatus.Vetoed, records[0].Status);
                Assert.Less(records[0].Sequence, records[1].Sequence);
                Assert.Less(records[1].Sequence, records[2].Sequence);
                Assert.AreEqual(SubmitStatus.Invalid, records[2].Status);
                StringAssert.Contains("\"action\":\"add\"", records[2].Command);

                log.Clear();
                Assert.AreEqual(0, log.TotalCount);
                Assert.AreEqual(0, log.Records.Count);
                Assert.AreEqual(0, log.CountsByCode.Count);
            }
        }

        [Test]
        public void InterceptorsCanVetoAndAreRemovable()
        {
            using (GameSession session = Counter())
            {
                ScriptedInterceptor interceptor = new ScriptedInterceptor
                {
                    Before = packet => packet.Command["n"].AsInt() > 10 ? SubmitResult.Vetoed("TOO_BIG", "no more than 10") : null
                };
                session.AddInterceptor(interceptor);

                SubmitResult vetoed = Add(session, 1, 11);
                Assert.AreEqual(SubmitStatus.Vetoed, vetoed.Status);
                Assert.AreEqual("TOO_BIG", vetoed.Code);
                Assert.AreEqual(0, Count(session));
                Assert.IsTrue(Add(session, 1, 10).IsAccepted);
                Assert.AreEqual(2, interceptor.BeforeCalls);
                Assert.AreEqual(2, interceptor.AfterCalls);

                Assert.IsTrue(session.RemoveInterceptor(interceptor));
                Assert.IsFalse(session.RemoveInterceptor(interceptor));
                Assert.IsTrue(Add(session, 2, 11).IsAccepted);
                Assert.AreEqual(2, interceptor.BeforeCalls);
                Assert.Throws<ArgumentNullException>(() => session.AddInterceptor(null));
            }
        }

        [Test]
        public void FailingInterceptorsAreReportedAndSkipped()
        {
            using (GameSession session = Counter())
            {
                List<Exception> failures = new List<Exception>();
                session.HandlerFailed += failures.Add;
                session.AddInterceptor(new ScriptedInterceptor { Before = p => throw new InvalidOperationException("before"), After = (p, r) => throw new InvalidOperationException("after") });

                SubmitResult result = Add(session, 1, 1);
                Assert.IsTrue(result.IsAccepted);
                Assert.AreEqual(2, failures.Count);
                Assert.AreEqual("before", failures[0].Message);
                Assert.AreEqual("after", failures[1].Message);
            }
        }

        [Test]
        public void EventsReachSubscribersInOrder()
        {
            GameSession session = new GameSession(TestSupport.InlineMod(CounterRules));
            using (session)
            {
                List<string> types = new List<string>();
                session.EventApplied += e => types.Add(e.Type);

                session.NewGame();
                Assert.AreEqual("reset,spawn,turn", string.Join(",", types));

                types.Clear();
                SubmitResult result = Add(session, 1, 2);
                Assert.AreEqual("set,turn", string.Join(",", types));
                Assert.AreEqual(2, result.Events.Count);

                types.Clear();
                session.Undo();
                Assert.AreEqual("reset", types[0]);
                Assert.IsTrue(types.Contains("spawn"));
                Assert.AreEqual("turn", types[types.Count - 1]);
            }
        }

        [Test]
        public void CommandProcessedFiresOncePerPacket()
        {
            using (GameSession session = Counter())
            {
                List<long> sequences = new List<long>();
                List<SubmitStatus> statuses = new List<SubmitStatus>();
                session.CommandProcessed += (packet, result) =>
                {
                    sequences.Add(packet.Sequence);
                    statuses.Add(result.Status);
                };

                Add(session, 1, 1);
                Do(session, 2, "reject");
                Do(session, 5, "add");
                Assert.AreEqual(3, sequences.Count);
                Assert.Less(sequences[0], sequences[1]);
                Assert.Less(sequences[1], sequences[2]);
                Assert.AreEqual(SubmitStatus.Accepted, statuses[0]);
                Assert.AreEqual(SubmitStatus.Rejected, statuses[1]);
                Assert.AreEqual(SubmitStatus.Invalid, statuses[2]);
            }
        }

        [Test]
        public void FailingSubscribersDoNotDisturbTheGame()
        {
            using (GameSession session = Counter())
            {
                int failures = 0;
                int delivered = 0;
                session.HandlerFailed += e => failures++;
                session.EventApplied += e => throw new InvalidOperationException("view broke");
                session.EventApplied += e => delivered++;
                session.CommandProcessed += (p, r) => throw new InvalidOperationException("ui broke");

                Assert.IsTrue(Add(session, 1, 1).IsAccepted);
                Assert.AreEqual(2, delivered);
                Assert.AreEqual(3, failures);
                Assert.AreEqual(1, Count(session));
            }
        }

        [Test]
        public void SynthesizedEventsRebuildTheSameTable()
        {
            using (GameSession session = Counter())
            {
                Add(session, 1, 6);
                Do(session, 2, "note");
                session.Submit(InputPacket.Action(2, "note").With("text", "hello"));

                ViewModel copy = new ViewModel();
                foreach (ViewEvent e in session.View.Synthesize())
                    copy.Apply(e);

                Assert.AreEqual(session.View.Turn, copy.Turn);
                Assert.AreEqual(session.View.ObjectCount, copy.ObjectCount);
                Assert.AreEqual("hello", copy.GetCustom("note").Raw["text"].AsString());
                foreach (ViewObject o in session.View.Objects)
                {
                    ViewObject twin = copy.Get(o.Id);
                    Assert.IsNotNull(twin);
                    Assert.AreEqual(o.Kind, twin.Kind);
                    Assert.AreEqual(o.Props.ToJson(), twin.Props.ToJson());
                }
            }

            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":3,\"win_length\":3}")))
            {
                TestSupport.PlaceOk(session, 1, 1, 1);
                TestSupport.PlaceOk(session, 2, 1, 2);
                TestSupport.PlaceOk(session, 1, 2, 1);
                TestSupport.PlaceOk(session, 2, 2, 2);
                TestSupport.PlaceOk(session, 1, 3, 1);

                ViewModel copy = new ViewModel();
                foreach (ViewEvent e in session.View.Synthesize())
                    copy.Apply(e);
                Assert.IsTrue(copy.IsOver);
                Assert.AreEqual(1, copy.Over.Winner);
                Assert.AreEqual(3, copy.Over.Cells.Count);
                Assert.AreEqual(5, copy.ObjectCount);
            }
        }

        [Test]
        public void ViewModelIgnoresEventsForUnknownObjects()
        {
            ViewModel view = new ViewModel();
            Assert.IsFalse(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"move\",\"id\":9,\"x\":1,\"y\":1}"))));
            Assert.IsFalse(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"set\",\"id\":9,\"props\":{\"a\":1}}"))));
            Assert.IsFalse(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"remove\",\"id\":9}"))));
            Assert.IsTrue(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"id\":9,\"kind\":\"k\",\"x\":2,\"y\":3,\"owner\":2,\"props\":{\"a\":1}}"))));
            Assert.IsTrue(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"move\",\"id\":9,\"x\":4,\"y\":5}"))));
            Assert.IsTrue(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"set\",\"id\":9,\"props\":{\"b\":2}}"))));
            ViewObject o = view.Get(9);
            Assert.AreEqual(4, o.X);
            Assert.AreEqual(5, o.Y);
            Assert.AreEqual(2, o.Owner);
            Assert.AreEqual(1, o.Props["a"].AsInt());
            Assert.AreEqual(2, o.Props["b"].AsInt());
            Assert.IsTrue(view.Apply(ViewEvent.Parse(JsonValue.Parse("{\"t\":\"remove\",\"id\":9}"))));
            Assert.AreEqual(0, view.ObjectCount);
        }

        [Test]
        public void ViewEventsAreValidated()
        {
            Assert.Throws<FormatException>(() => ViewEvent.Parse(null));
            Assert.Throws<FormatException>(() => ViewEvent.Parse(JsonValue.Parse("[]")));
            Assert.Throws<FormatException>(() => ViewEvent.Parse(JsonValue.Parse("{}")));
            Assert.Throws<FormatException>(() => ViewEvent.Parse(JsonValue.Parse("{\"t\":\"spawn\",\"x\":1}")));
            Assert.Throws<FormatException>(() => ViewEvent.Parse(JsonValue.Parse("{\"t\":\"move\",\"id\":\"a\"}")));
            ViewEvent custom = ViewEvent.Parse(JsonValue.Parse("{\"t\":\"sparkle\",\"amount\":3}"));
            Assert.AreEqual(ViewEventKind.Custom, custom.Kind);
            Assert.AreEqual("sparkle", custom.Type);
            ViewEvent status = ViewEvent.Parse(JsonValue.Parse("{\"t\":\"status\",\"text\":\"hi\"}"));
            Assert.AreEqual("info", status.Level);
            Assert.AreEqual("hi", status.Text);
        }

        [Test]
        public void NewGameFailureKeepsTheRunningGame()
        {
            using (GameSession session = Counter())
            {
                Add(session, 1, 5);
                GameSessionException e = Assert.Throws<GameSessionException>(() => session.NewGame(JsonValue.Parse("{\"fail\":true}")));
                Assert.AreEqual("SCRIPT_ERROR", e.Code);
                StringAssert.Contains("cannot start", e.Detail);
                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(5, Count(session));
                Assert.AreEqual(5, ShownValue(session));
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);
            }
        }

        [Test]
        public void NewGameStartsFromFreshStateAndMergesOptions()
        {
            using (GameSession session = Counter())
            {
                Add(session, 1, 5);
                session.NewGame(JsonValue.Parse("{\"start\":40}"));
                Assert.AreEqual(40, Count(session));
                Assert.AreEqual(40, ShownValue(session));
                Assert.AreEqual(1, session.HistoryCount);
                Assert.AreEqual(1, session.View.ObjectCount);
                Assert.AreEqual(40, session.GameOptions["start"].AsInt());

                session.NewGame();
                Assert.AreEqual(0, Count(session));
            }
        }

        [Test]
        public void ScriptOutputIsCollected()
        {
            using (GameSession session = Counter(new SessionOptions { LogLimit = 2 }))
            {
                List<LogLine> seen = new List<LogLine>();
                session.ScriptLog += seen.Add;

                Do(session, 1, "log");
                Assert.AreEqual(3, seen.Count);
                Assert.AreEqual(0, seen[0].Level);
                StringAssert.Contains("hello", seen[0].Message);
                Assert.AreEqual(1, seen[1].Level);
                Assert.AreEqual(2, seen[2].Level);

                Assert.AreEqual(2, session.RecentLogs.Count);
                List<LogLine> kept = new List<LogLine>(session.RecentLogs);
                Assert.AreEqual(1, kept[0].Level);
                Assert.AreEqual(2, kept[1].Level);
            }
        }

        [Test]
        public void QueriesThatFailAreReportedWithoutLosingTheGame()
        {
            VmOptions vm = new VmOptions { CallTimeoutMs = 50 };
            using (GameSession session = Counter(new SessionOptions { Vm = vm }))
            {
                Add(session, 1, 9);

                GameSessionException scriptError = Assert.Throws<GameSessionException>(() => session.Query("boom"));
                Assert.AreEqual("SCRIPT_ERROR", scriptError.Code);
                Assert.AreEqual(9, Count(session));

                GameSessionException timeout = Assert.Throws<GameSessionException>(() => session.Query("spin"));
                Assert.AreEqual("TIMEOUT", timeout.Code);
                Assert.IsTrue(session.IsStarted);
                Assert.AreEqual(9, Count(session));
                Assert.IsFalse(session.VmStats.Faulted);
                Assert.IsTrue(Add(session, 2, 1).IsAccepted);
            }
        }

        [Test]
        public void LegalAndRandomCommandsNeedModSupport()
        {
            using (GameSession session = Counter())
            {
                Assert.IsFalse(session.Capabilities.Legal);
                Assert.IsFalse(session.Capabilities.Random);
                Assert.IsTrue(session.Capabilities.Query);
                Assert.AreEqual("NOT_SUPPORTED", Assert.Throws<GameSessionException>(() => session.LegalCommands(1)).Code);
                Assert.AreEqual("NOT_SUPPORTED", Assert.Throws<GameSessionException>(() => session.RandomCommand(1)).Code);
                Assert.IsTrue(Add(session, 1, 1).IsAccepted);
            }
        }

        [Test]
        public void SnapshotsOfASessionLoadIntoAnotherOne()
        {
            ModPackage mod = TestSupport.InlineMod(CounterRules);
            string snapshot;
            using (GameSession first = new GameSession(mod))
            {
                first.NewGame();
                Add(first, 1, 12);
                snapshot = first.CaptureSnapshot();
            }

            using (GameSession second = new GameSession(mod))
            {
                Assert.IsFalse(second.IsStarted);
                second.LoadSnapshot(snapshot);
                Assert.IsTrue(second.IsStarted);
                Assert.AreEqual(12, Count(second));
                Assert.AreEqual(12, ShownValue(second));
                Assert.AreEqual(2, second.View.Turn);
                Assert.IsTrue(Add(second, 2, 1).IsAccepted);
                Assert.AreEqual(13, Count(second));
            }
        }

        [Test]
        public void DisposedSessionsRefuseWork()
        {
            GameSession session = Counter();
            session.Dispose();
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.Submit(InputPacket.Action(1, "add")));
            Assert.Throws<ObjectDisposedException>(() => session.NewGame());
            Assert.Throws<ObjectDisposedException>(() => session.Query("count"));
            Assert.Throws<ObjectDisposedException>(() => session.Undo());
        }

        [Test]
        public void SessionsDoNotShareState()
        {
            ModPackage mod = TestSupport.InlineMod(CounterRules);
            using (GameSession a = new GameSession(mod))
            using (GameSession b = new GameSession(mod))
            {
                a.NewGame();
                b.NewGame();
                Add(a, 1, 5);
                Add(b, 1, 7);
                Assert.AreEqual(5, Count(a));
                Assert.AreEqual(7, Count(b));
            }
        }
    }
}
