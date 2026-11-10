using System.Collections.Generic;
using System.Threading;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class SimulationTests
    {
        private const string SpinningAgentRules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command() end
function rules.random_command(seat) while true do end end
return rules";

        private const string PlainRules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command() end
return rules";

        private static SimulationOptions Games(int games, int seed = 1)
        {
            return new SimulationOptions { Games = games, Seed = seed };
        }

        private static string Signature(SimulationResult r)
        {
            List<string> wins = new List<string>();
            foreach (KeyValuePair<int, int> pair in r.Wins)
                wins.Add(pair.Key + "=" + pair.Value);
            return r.Games + "|" + r.Draws + "|" + r.Aborted + "|" + r.Rejected + "|" + r.Moves + "|" + string.Join(",", wins);
        }

        [Test]
        public void TotalsAddUp()
        {
            SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), Games(300));
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.AreEqual(300, r.Games);
            int decided = 0;
            foreach (int wins in r.Wins.Values)
                decided += wins;
            Assert.AreEqual(300, decided + r.Draws + r.Aborted + r.Rejected);
            Assert.AreEqual(0, r.Rejected);
            Assert.AreEqual(0, r.Aborted);
            Assert.Greater(r.Moves, 300);
            Assert.Greater(r.AverageMoves, 9);
            Assert.Greater(r.GamesPerSecond, 0);
            Assert.Greater(r.Wins[1], 0);
            Assert.Greater(r.Wins[2], 0);
            StringAssert.Contains("300 games in", r.ToString());
        }

        [Test]
        public void ResultsFollowTheSeed()
        {
            ModPackage mod = TestSupport.LoadGomoku();
            string a = Signature(SimulationRunner.Run(mod, Games(200, 7)));
            string b = Signature(SimulationRunner.Run(mod, Games(200, 7)));
            string c = Signature(SimulationRunner.Run(mod, Games(200, 8)));
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        [Test]
        public void SlicingDoesNotChangeTheOutcome()
        {
            ModPackage mod = TestSupport.LoadGomoku();
            SimulationOptions fine = Games(400, 5);
            fine.TargetSliceMilliseconds = 10;
            SimulationOptions coarse = Games(400, 5);
            coarse.TargetSliceMilliseconds = 100000;
            Assert.AreEqual(Signature(SimulationRunner.Run(mod, coarse)), Signature(SimulationRunner.Run(mod, fine)));
        }

        [Test]
        public void EachGameUsesItsOwnSeed()
        {
            ModPackage mod = TestSupport.LoadGomoku();
            SimulationResult whole = SimulationRunner.Run(mod, Games(120, 10));
            SimulationResult head = SimulationRunner.Run(mod, Games(70, 10));
            SimulationResult tail = SimulationRunner.Run(mod, Games(50, 80));
            Assert.AreEqual(whole.Moves, head.Moves + tail.Moves);
            Assert.AreEqual(whole.Draws, head.Draws + tail.Draws);
            Assert.AreEqual(whole.Wins[1], head.Wins[1] + tail.Wins[1]);
            Assert.AreEqual(whole.Wins[2], head.Wins[2] + tail.Wins[2]);
        }

        [Test]
        public void PlaysAtLeastAThousandGamesPerSecond()
        {
            SimulationRunner.Run(TestSupport.LoadGomoku(), Games(300));
            SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), Games(3000));
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.GreaterOrEqual(r.GamesPerSecond, 1000.0, r.ToString());
        }

        [Test]
        public void GameOptionsOverrideTheDefaults()
        {
            SimulationOptions options = Games(300, 3);
            options.GameOptions = JsonValue.Parse("{\"size\":3,\"win_length\":3}");
            SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), options);
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.LessOrEqual(r.AverageMoves, 9.0);
            Assert.Greater(r.Draws, 0);
        }

        [Test]
        public void TheMoveLimitAbortsGames()
        {
            SimulationOptions options = Games(25);
            options.MaxMovesPerGame = 3;
            SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), options);
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.AreEqual(25, r.Aborted);
            Assert.AreEqual(0, r.Wins.Count);
            Assert.AreEqual(0, r.Draws);
            Assert.AreEqual(75, r.Moves);
        }

        [Test]
        public void ProgressIsReportedUpToTheTotal()
        {
            List<SimulationProgress> reports = new List<SimulationProgress>();
            SimulationOptions options = Games(500);
            options.TargetSliceMilliseconds = 10;
            SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), options, reports.Add);
            Assert.IsTrue(r.Succeeded, r.Error);
            Assert.Greater(reports.Count, 1);
            int previous = 0;
            foreach (SimulationProgress p in reports)
            {
                Assert.Greater(p.GamesDone, previous);
                Assert.AreEqual(500, p.GamesTotal);
                previous = p.GamesDone;
            }
            Assert.AreEqual(500, previous);
        }

        [Test]
        public void CancellationStopsTheRunAndKeepsThePartialCounts()
        {
            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                SimulationOptions options = Games(2000000);
                options.TargetSliceMilliseconds = 20;
                SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), options, p => source.Cancel(), source.Token);
                Assert.IsTrue(r.Cancelled);
                Assert.IsFalse(r.Succeeded);
                Assert.Greater(r.Games, 0);
                Assert.Less(r.Games, 2000000);
                Assert.Less(r.ElapsedSeconds, 20.0);
                StringAssert.Contains("[cancelled]", r.ToString());
            }

            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                source.Cancel();
                SimulationResult r = SimulationRunner.Run(TestSupport.LoadGomoku(), Games(100), null, source.Token);
                Assert.IsTrue(r.Cancelled);
                Assert.AreEqual(0, r.Games);
            }
        }

        [Test]
        public void ModsWithoutAnAgentReportAnError()
        {
            SimulationResult r = SimulationRunner.Run(TestSupport.InlineMod(PlainRules), Games(10));
            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains("NOT_SUPPORTED", r.Error);
            Assert.AreEqual(0, r.Games);
            StringAssert.Contains("[error:", r.ToString());
        }

        [Test]
        public void RunawayAgentsAreStoppedByTheTimeout()
        {
            SimulationOptions options = Games(10);
            options.Vm = new VmOptions { CallTimeoutMs = 100, MemoryLimitMb = 64 };
            SimulationResult r = SimulationRunner.Run(TestSupport.InlineMod(SpinningAgentRules), options);
            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains("Timeout", r.Error);
            Assert.Less(r.ElapsedSeconds, 10.0);
        }

        [Test]
        public void RunningASimulationLeavesTheLiveGameAlone()
        {
            using (GameSession session = TestSupport.StartGomoku())
            {
                TestSupport.PlaceOk(session, 1, 8, 8);
                TestSupport.PlaceOk(session, 2, 9, 9);
                string state = session.Query("state").ToJson();
                long version = session.View.Version;

                SimulationResult r = SimulationRunner.Run(session.Mod, Games(100));
                Assert.IsTrue(r.Succeeded, r.Error);

                Assert.AreEqual(state, session.Query("state").ToJson());
                Assert.AreEqual(version, session.View.Version);
                Assert.IsTrue(TestSupport.PlaceOk(session, 1, 10, 10).IsAccepted);
            }
        }

        [Test]
        public void AsyncRunsMatchSynchronousOnes()
        {
            ModPackage mod = TestSupport.LoadGomoku();
            SimulationResult sync = SimulationRunner.Run(mod, Games(150, 4));
            SimulationResult async = SimulationRunner.RunAsync(mod, Games(150, 4)).GetAwaiter().GetResult();
            Assert.AreEqual(Signature(sync), Signature(async));
        }

        [Test]
        public void SeveralSimulationsCanRunAtOnce()
        {
            ModPackage mod = TestSupport.LoadGomoku();
            System.Threading.Tasks.Task<SimulationResult>[] tasks =
            {
                SimulationRunner.RunAsync(mod, Games(200, 1)),
                SimulationRunner.RunAsync(mod, Games(200, 1)),
                SimulationRunner.RunAsync(mod, Games(200, 2)),
                SimulationRunner.RunAsync(mod, Games(200, 2))
            };
            System.Threading.Tasks.Task.WaitAll(tasks);
            Assert.AreEqual(Signature(tasks[0].Result), Signature(tasks[1].Result));
            Assert.AreEqual(Signature(tasks[2].Result), Signature(tasks[3].Result));
            Assert.AreNotEqual(Signature(tasks[0].Result), Signature(tasks[2].Result));
        }
    }
}
