using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class AppTests
    {
        private const string MinimalManifest = "{\"id\":\"mini\",\"name\":\"Mini\",\"api\":1,\"entry\":\"main.luau\"}";
        private const string MinimalScript = "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() end return rules";

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

        private string NewDirectory()
        {
            string path = TestSupport.TempDirectory();
            _temporary.Add(path);
            return path;
        }

        private GameHost NewHost(Action<GameHostOptions> configure = null)
        {
            GameHostOptions options = new GameHostOptions { Clock = () => _time, AiDelaySeconds = 0.5, AiSeed = 17 };
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

        private static string Board(GameHost host)
        {
            JsonValue array = JsonValue.NewArray();
            foreach (ViewEvent e in host.Session.View.Synthesize())
                array.Add(e.Raw);
            return array.ToJson();
        }

        [Test]
        public void ToastsExpireAndFade()
        {
            ToastQueue queue = new ToastQueue(3, 2.0);
            queue.Add("first", ToastLevel.Info, 10.0);
            Assert.AreEqual(1, queue.Active(10.5).Count);
            Assert.AreEqual(1f, queue.Active(10.5)[0].Opacity(10.5), 0.0001f);
            Assert.AreEqual(0.5f, queue.Active(11.75)[0].Opacity(11.75), 0.0001f);
            Assert.AreEqual(0, queue.Active(12.0).Count);
        }

        [Test]
        public void ToastsCutVeryLongText()
        {
            ToastQueue queue = new ToastQueue();
            queue.Add(new string('x', 1000), ToastLevel.Warning, 0);
            string shown = queue.Active(0)[0].Text;
            Assert.AreEqual(ToastQueue.MaxTextLength, shown.Length);
            Assert.IsTrue(shown.EndsWith("\u2026"));

            queue.Add(new string('y', ToastQueue.MaxTextLength), ToastLevel.Info, 0);
            Assert.AreEqual(ToastQueue.MaxTextLength, queue.Active(0)[1].Text.Length);
            Assert.IsFalse(queue.Active(0)[1].Text.EndsWith("\u2026"));
        }

        [Test]
        public void ToastsKeepTheNewestAndMergeRepeats()
        {
            ToastQueue queue = new ToastQueue(2, 5.0);
            queue.Add("a", ToastLevel.Info, 0);
            queue.Add("b", ToastLevel.Warning, 1);
            queue.Add("c", ToastLevel.Error, 2);
            IReadOnlyList<Toast> shown = queue.Active(2);
            Assert.AreEqual(2, shown.Count);
            Assert.AreEqual("b", shown[0].Text);
            Assert.AreEqual("c", shown[1].Text);

            queue.Add("c", ToastLevel.Error, 4.5);
            Assert.AreEqual(2, queue.Active(4.5).Count);
            Assert.AreEqual(1, queue.Active(6.0).Count);
            Assert.AreEqual(1, queue.Active(9.0).Count);
            Assert.AreEqual(0, queue.Active(9.5).Count);

            queue.Add("", ToastLevel.Info, 9.5);
            queue.Add(null, ToastLevel.Info, 9.5);
            Assert.AreEqual(0, queue.Active(9.5).Count);
            queue.Add("d", ToastLevel.Info, 9.5);
            Assert.AreEqual(1, queue.Active(9.5).Count);
            queue.Clear();
            Assert.AreEqual(0, queue.Active(9.5).Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ToastQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ToastQueue(1, 0));
        }

        [Test]
        public void CatalogFindsModsInEveryRoot()
        {
            string extra = NewDirectory();
            TestSupport.WriteFile(extra, "mini/config.json", MinimalManifest);
            TestSupport.WriteFile(extra, "mini/main.luau", MinimalScript);

            ModCatalog catalog = new ModCatalog(new[] { TestSupport.ModsRoot, extra, "", null, Path.Combine(extra, "missing") });
            Assert.AreEqual(3, catalog.Sources.Count);
            Assert.AreEqual(0, catalog.Errors.Count);
            Assert.IsNotNull(catalog.Find("gomoku"));
            Assert.IsNotNull(catalog.Find("mini"));
            Assert.IsNull(catalog.Find("nope"));
            Assert.IsNull(catalog.Find(null));
        }

        [Test]
        public void CatalogKeepsTheFirstOfDuplicateIds()
        {
            string first = NewDirectory();
            string second = NewDirectory();
            TestSupport.WriteFile(first, "m/config.json", MinimalManifest);
            TestSupport.WriteFile(first, "m/main.luau", MinimalScript);
            TestSupport.WriteFile(second, "other/config.json", MinimalManifest.Replace("Mini", "Other"));
            TestSupport.WriteFile(second, "other/main.luau", MinimalScript);

            ModCatalog catalog = new ModCatalog(new[] { first, second });
            Assert.AreEqual(1, catalog.Mods.Count);
            Assert.AreEqual("Mini", catalog.Find("mini").Manifest.Name);
            Assert.AreEqual(1, catalog.Errors.Count);
            StringAssert.Contains("already provided", catalog.Errors[0].Message);
        }

        [Test]
        public void CatalogRefreshSeesNewMods()
        {
            string root = NewDirectory();
            ModCatalog catalog = new ModCatalog(new[] { root });
            Assert.AreEqual(0, catalog.Mods.Count);
            TestSupport.WriteFile(root, "mini/config.json", MinimalManifest);
            TestSupport.WriteFile(root, "mini/main.luau", MinimalScript);
            TestSupport.WriteFile(root, "broken/config.json", "{ nope");
            catalog.Refresh();
            Assert.AreEqual(1, catalog.Mods.Count);
            Assert.AreEqual(1, catalog.Errors.Count);
        }

        [Test]
        public void HostSelectsAModAndBuildsItsSession()
        {
            using (GameHost host = new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot })))
            {
                Assert.IsNull(host.Session);
                Assert.IsNull(host.Console);
                Assert.IsFalse(host.StartNewGame());
                Assert.IsFalse(host.AcceptsHumanInput);

                int replaced = 0;
                host.SessionReplaced += () => replaced++;
                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.AreEqual(1, replaced);
                Assert.AreEqual("gomoku", host.Mod.Manifest.Id);
                Assert.IsNotNull(host.Console);
                Assert.IsFalse(host.Session.IsStarted);
                Assert.AreEqual(15, host.NewGameOptions["size"].AsInt());
                Assert.IsTrue(host.Session.Interceptors.Count > 0);

                Assert.IsTrue(host.StartNewGame());
                Assert.IsTrue(host.Session.IsStarted);
                Assert.IsTrue(host.AcceptsHumanInput);
            }
        }

        [Test]
        public void HostReportsUnknownMods()
        {
            using (GameHost host = NewHost())
            {
                GameSession before = host.Session;
                Assert.IsFalse(host.SelectMod("not-there"));
                Assert.AreSame(before, host.Session);
                IReadOnlyList<Toast> toasts = host.Toasts.Active(host.Now);
                Assert.AreEqual(1, toasts.Count);
                Assert.AreEqual(ToastLevel.Error, toasts[0].Level);
                Assert.Throws<ArgumentNullException>(() => host.SelectMod((ModPackage)null));
            }
        }

        [Test]
        public void HostKeepsTheOldSessionWhenAModWillNotLoad()
        {
            using (GameHost host = NewHost())
            {
                host.Submit(1, Place(4, 4));
                GameSession before = host.Session;

                ModPackage broken = TestSupport.InlineMod("local x = = 1");
                Assert.IsFalse(host.SelectMod(broken));
                Assert.AreSame(before, host.Session);
                Assert.IsTrue(before.IsStarted);
                Assert.AreEqual(1, before.View.ObjectCount);
                Assert.AreEqual(ToastLevel.Error, host.Toasts.Active(host.Now)[0].Level);
            }
        }

        [Test]
        public void ReplacingTheModDisposesTheOldSession()
        {
            using (GameHost host = NewHost())
            {
                GameSession before = host.Session;
                Assert.IsFalse(host.Submit(1, Place(0, 0)).IsAccepted);
                Assert.AreEqual(1, host.Rejections.TotalCount);
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(MinimalScript)));
                Assert.AreNotSame(before, host.Session);
                Assert.Throws<ObjectDisposedException>(() => before.NewGame());
                Assert.AreEqual(0, host.Rejections.TotalCount);
            }
        }

        [Test]
        public void EditedOptionsReachTheNextGame()
        {
            using (GameHost host = NewHost())
            {
                host.SetOption("size", JsonValue.Of(9));
                host.SetOption("exact_win", JsonValue.Of(true));
                Assert.IsTrue(host.StartNewGame());
                Assert.AreEqual(9, host.Session.GameOptions["size"].AsInt());
                Assert.IsTrue(host.Session.GameOptions["exact_win"].AsBool());
                Assert.AreEqual(15, host.Mod.Manifest.DefaultOptions["size"].AsInt());
                Assert.Throws<ArgumentException>(() => host.SetOption("", JsonValue.Of(1)));
            }
        }

        [Test]
        public void RefusedMovesBecomeToasts()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.Submit(1, Place(8, 8)).IsAccepted);
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);

                SubmitResult occupied = host.Submit(2, Place(8, 8));
                Assert.AreEqual("OCCUPIED", occupied.Code);
                IReadOnlyList<Toast> toasts = host.Toasts.Active(host.Now);
                Assert.AreEqual(1, toasts.Count);
                StringAssert.Contains("OCCUPIED", toasts[0].Text);
                Assert.AreEqual(ToastLevel.Warning, toasts[0].Level);

                Assert.AreEqual(SubmitStatus.Invalid, host.Submit(9, Place(1, 1)).Status);
                Assert.AreEqual(2, host.Toasts.Active(host.Now).Count);
                Assert.AreEqual(2, host.Rejections.TotalCount);
            }
        }

        [Test]
        public void ScriptFaultsBecomeErrorToasts()
        {
            using (GameHost host = new GameHost(new ModCatalog(new string[0]), new GameHostOptions { Clock = () => _time }))
            {
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(
                    "local rules = {} function rules.new_game() bse.turn(1) end function rules.command() error('boom') end " +
                    "function rules.snapshot() return {} end function rules.restore() end return rules")));
                Assert.IsTrue(host.StartNewGame());
                SubmitResult result = host.Submit(1, JsonValue.NewObject().Set("action", "x"));
                Assert.AreEqual(SubmitStatus.Faulted, result.Status);
                Toast toast = host.Toasts.Active(host.Now)[0];
                Assert.AreEqual(ToastLevel.Error, toast.Level);
                StringAssert.Contains("the game was restored", toast.Text);
            }
        }

        [Test]
        public void SubmitWithoutAModIsInvalid()
        {
            using (GameHost host = new GameHost(new ModCatalog(new string[0])))
            {
                SubmitResult result = host.Submit(1, Place(1, 1));
                Assert.AreEqual(SubmitStatus.Invalid, result.Status);
                Assert.AreEqual("NO_MOD", result.Code);
            }
        }

        [Test]
        public void SeatModesDecideWhoMayPlay()
        {
            using (GameHost host = NewHost())
            {
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Random));
                Assert.AreEqual(SeatMode.Random, host.GetSeatMode(1));
                Assert.IsFalse(host.AcceptsHumanInput);
                Assert.IsTrue(host.SetSeatMode(1, SeatMode.Human));
                Assert.IsTrue(host.AcceptsHumanInput);
                Assert.IsFalse(host.SetSeatMode(0, SeatMode.Random));
                Assert.IsFalse(host.SetSeatMode(3, SeatMode.Random));
            }
        }

        [Test]
        public void SeatModesResetWithTheMod()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(2, SeatMode.Random);
                Assert.IsTrue(host.SelectMod("gomoku"));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(2));
            }
        }

        [Test]
        public void ModsWithoutAComputerPlayerRefuseTheRandomMode()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsTrue(host.SelectMod(TestSupport.InlineMod(MinimalScript)));
                Assert.IsTrue(host.StartNewGame());
                Assert.IsFalse(host.SetSeatMode(1, SeatMode.Random));
                Assert.AreEqual(SeatMode.Human, host.GetSeatMode(1));
                Assert.AreEqual(ToastLevel.Warning, host.Toasts.Active(host.Now)[0].Level);
            }
        }

        [Test]
        public void TheComputerWaitsForItsDelayThenPlays()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(2, SeatMode.Random);
                Assert.IsFalse(host.Update());
                host.Submit(1, Place(8, 8));

                Assert.IsFalse(host.Update());
                _time = 0.4;
                Assert.IsFalse(host.Update());
                Assert.AreEqual(1, host.Session.View.ObjectCount);
                _time = 0.6;
                Assert.IsTrue(host.Update());
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.AreEqual(1, host.Session.View.Turn);

                Assert.IsFalse(host.Update());
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
            }
        }

        [Test]
        public void TheComputerMovesAreMarkedAsComputerInput()
        {
            using (GameHost host = NewHost())
            {
                List<string> sources = new List<string>();
                host.Session.CommandProcessed += (packet, result) => sources.Add(packet.Source);
                host.SetSeatMode(1, SeatMode.Random);
                host.Update();
                _time = 1;
                Assert.IsTrue(host.Update());
                Assert.IsTrue(host.Submit(2, Place(1, 1)).IsAccepted);
                Assert.AreEqual(2, sources.Count);
                Assert.AreEqual("ai", sources[0]);
                Assert.AreEqual("human", sources[1]);
            }
        }

        [Test]
        public void TwoComputersPlayAGameToTheEnd()
        {
            using (GameHost host = NewHost())
            {
                host.SetOption("size", JsonValue.Of(3));
                host.SetOption("win_length", JsonValue.Of(3));
                Assert.IsTrue(host.StartNewGame());
                host.SetSeatMode(1, SeatMode.Random);
                host.SetSeatMode(2, SeatMode.Random);

                int steps = 0;
                while (!host.Session.View.IsOver && steps < 200)
                {
                    _time += 1.0;
                    host.Update();
                    steps++;
                }
                Assert.IsTrue(host.Session.View.IsOver);
                Assert.AreEqual(0, host.Toasts.Active(host.Now).Count);
                _time += 10;
                Assert.IsFalse(host.Update());
                Assert.IsFalse(host.AcceptsHumanInput);
            }
        }

        [Test]
        public void ComputerGamesRepeatWithTheSameSeed()
        {
            string[] boards = new string[2];
            for (int i = 0; i < 2; i++)
            {
                _time = 0;
                using (GameHost host = NewHost(o => o.AiSeed = 99))
                {
                    host.SetOption("size", JsonValue.Of(5));
                    host.SetOption("win_length", JsonValue.Of(4));
                    host.StartNewGame();
                    host.SetSeatMode(1, SeatMode.Random);
                    host.SetSeatMode(2, SeatMode.Random);
                    for (int step = 0; step < 100 && !host.Session.View.IsOver; step++)
                    {
                        _time += 1.0;
                        host.Update();
                    }
                    boards[i] = Board(host);
                }
            }
            Assert.AreEqual(boards[0], boards[1]);
        }

        [Test]
        public void PausingStopsTheComputer()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(1, SeatMode.Random);
                host.AiPaused = true;
                _time = 5;
                Assert.IsFalse(host.Update());
                _time = 10;
                Assert.IsFalse(host.Update());
                Assert.AreEqual(0, host.Session.View.ObjectCount);

                host.AiPaused = false;
                Assert.IsFalse(host.Update());
                _time = 11;
                Assert.IsTrue(host.Update());
                Assert.AreEqual(1, host.Session.View.ObjectCount);
            }
        }

        [Test]
        public void HistoryMovesGoThroughTheHost()
        {
            using (GameHost host = NewHost())
            {
                Assert.IsFalse(host.Undo());
                host.Submit(1, Place(1, 1));
                host.Submit(2, Place(2, 2));
                Assert.IsTrue(host.Undo());
                Assert.AreEqual(1, host.Session.View.ObjectCount);
                Assert.IsTrue(host.Redo());
                Assert.AreEqual(2, host.Session.View.ObjectCount);
                Assert.IsTrue(host.JumpTo(0));
                Assert.AreEqual(0, host.Session.View.ObjectCount);
                Assert.IsFalse(host.JumpTo(50));
            }
        }

        [Test]
        public void UndoDuringAComputerTurnReschedulesIt()
        {
            using (GameHost host = NewHost())
            {
                host.SetSeatMode(2, SeatMode.Random);
                host.Submit(1, Place(8, 8));
                host.Update();
                _time = 0.4;
                Assert.IsTrue(host.Undo());
                Assert.IsTrue(host.Redo());
                _time = 0.6;
                Assert.IsFalse(host.Update());
                Assert.AreEqual(1, host.Session.View.ObjectCount);
                _time = 1.2;
                Assert.IsTrue(host.Update());
                Assert.AreEqual(2, host.Session.View.ObjectCount);
            }
        }

        [Test]
        public void SimulationJobRunsInTheBackground()
        {
            SimulationJob job = new SimulationJob();
            Assert.IsFalse(job.IsRunning);
            Assert.IsNull(job.LastResult);

            Assert.IsTrue(job.Start(TestSupport.LoadGomoku(), new SimulationOptions { Games = 400, Seed = 3 }));
            Assert.AreEqual(400, job.TotalGames);
            Assert.IsTrue(job.Wait(60000));
            Assert.IsFalse(job.IsRunning);
            Assert.IsNotNull(job.LastResult);
            Assert.IsTrue(job.LastResult.Succeeded, job.LastResult.Error);
            Assert.AreEqual(400, job.LastResult.Games);
            Assert.AreEqual(400, job.Progress.GamesDone);

            Assert.IsTrue(job.Start(TestSupport.LoadGomoku(), new SimulationOptions { Games = 10 }));
            job.Wait(60000);
            Assert.AreEqual(10, job.LastResult.Games);
        }

        [Test]
        public void SimulationJobAllowsOneRunAtATimeAndCancels()
        {
            SimulationJob job = new SimulationJob();
            SimulationOptions options = new SimulationOptions { Games = 5000000, TargetSliceMilliseconds = 20 };
            Assert.IsTrue(job.Start(TestSupport.LoadGomoku(), options));
            Assert.IsTrue(job.IsRunning);
            Assert.IsFalse(job.Start(TestSupport.LoadGomoku(), options));

            Stopwatch watch = Stopwatch.StartNew();
            while (job.Progress == null && watch.ElapsedMilliseconds < 10000)
                System.Threading.Thread.Sleep(5);
            Assert.IsNotNull(job.Progress);

            job.Cancel();
            Assert.IsTrue(job.Wait(20000));
            Assert.IsFalse(job.IsRunning);
            Assert.IsTrue(job.LastResult.Cancelled);
            Assert.Less(job.LastResult.Games, 5000000);
            Assert.Throws<ArgumentNullException>(() => job.Start(null, options));
        }

        [Test]
        public void SimulationJobReportsModErrors()
        {
            SimulationJob job = new SimulationJob();
            Assert.IsTrue(job.Start(TestSupport.InlineMod(MinimalScript), new SimulationOptions { Games = 5 }));
            Assert.IsTrue(job.Wait(20000));
            Assert.IsFalse(job.LastResult.Succeeded);
            StringAssert.Contains("NOT_SUPPORTED", job.LastResult.Error);
        }

        [Test]
        public void DisposingTheHostStopsEverything()
        {
            GameHost host = NewHost();
            host.Simulation.Start(host.Mod, new SimulationOptions { Games = 50000000, TargetSliceMilliseconds = 20 });
            GameSession session = host.Session;

            Stopwatch watch = Stopwatch.StartNew();
            host.Dispose();
            host.Dispose();
            Assert.Less(watch.ElapsedMilliseconds, 10000);
            Assert.IsFalse(host.Simulation.IsRunning);
            Assert.IsNull(host.Session);
            Assert.Throws<ObjectDisposedException>(() => session.NewGame());
            Assert.Throws<ObjectDisposedException>(() => host.StartNewGame());
            Assert.Throws<ObjectDisposedException>(() => host.Submit(1, Place(1, 1)));
            Assert.IsFalse(host.Update());
        }
    }
}
