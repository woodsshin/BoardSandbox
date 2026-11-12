using System.Collections.Generic;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class PlaybackTests
    {
        private static ViewEvent Event(string json)
        {
            return ViewEvent.Parse(JsonValue.Parse(json));
        }

        private static ViewEvent Spawn(int id)
        {
            return Event("{\"t\":\"spawn\",\"id\":" + id + ",\"kind\":\"a\",\"x\":1,\"y\":1}");
        }

        private static ViewEvent Fx(double seconds)
        {
            return Event("{\"t\":\"fx\",\"kind\":\"wait\",\"seconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        }

        [Test]
        public void EffectsAreParsedAndTheirTimeIsLimited()
        {
            ViewEvent e = Event("{\"t\":\"fx\",\"kind\":\"rotate\",\"seconds\":0.25}");
            Assert.AreEqual(ViewEventKind.Effect, e.Kind);
            Assert.AreEqual(0.25, e.Seconds, 1e-9);
            Assert.AreEqual(0.0, Event("{\"t\":\"fx\"}").Seconds);
            Assert.AreEqual(0.0, Event("{\"t\":\"fx\",\"seconds\":-1}").Seconds);
            Assert.AreEqual(0.0, Event("{\"t\":\"fx\",\"seconds\":\"x\"}").Seconds);
            Assert.AreEqual(ViewEvent.MaxEffectSeconds, Event("{\"t\":\"fx\",\"seconds\":1e9}").Seconds);
            Assert.AreEqual(0.0, Spawn(1).Seconds);
        }

        [Test]
        public void EffectsLeaveTheModelAlone()
        {
            ViewModel model = new ViewModel();
            model.Apply(Spawn(1));
            long version = model.Version;
            Assert.IsTrue(model.Apply(Fx(1)));
            Assert.AreEqual(version, model.Version);
            Assert.IsNull(model.GetCustom("fx"));
            foreach (ViewEvent e in model.Synthesize())
                Assert.AreNotEqual(ViewEventKind.Effect, e.Kind);
        }

        [Test]
        public void StageEventsAddUp()
        {
            ViewModel model = new ViewModel();
            model.Apply(Event("{\"t\":\"stage\",\"board\":{\"visible\":false,\"colors\":{\"slab\":\"#111111\"}},\"background\":\"#000000\"}"));
            model.Apply(Event("{\"t\":\"stage\",\"board\":{\"colors\":{\"line\":\"#222222\"}},\"camera\":{\"yaw\":30}}"));
            JsonValue stage = model.GetCustom("stage").Raw;
            Assert.IsFalse(stage["board"]["visible"].AsBool(true));
            Assert.AreEqual("#111111", stage["board"]["colors"]["slab"].AsString());
            Assert.AreEqual("#222222", stage["board"]["colors"]["line"].AsString());
            Assert.AreEqual("#000000", stage["background"].AsString());
            Assert.AreEqual(30, stage["camera"]["yaw"].AsInt());

            ViewModel copy = new ViewModel();
            foreach (ViewEvent e in model.Synthesize())
                copy.Apply(e);
            Assert.IsTrue(stage.StructurallyEquals(copy.GetCustom("stage").Raw));

            model.Apply(Event("{\"t\":\"reset\"}"));
            Assert.IsNull(model.GetCustom("stage"));
        }

        [Test]
        public void OtherCustomEventsStillReplaceEachOther()
        {
            ViewModel model = new ViewModel();
            model.Apply(Event("{\"t\":\"board\",\"columns\":9,\"rows\":9}"));
            model.Apply(Event("{\"t\":\"board\",\"columns\":5}"));
            Assert.AreEqual(0, model.GetCustom("board").Raw["rows"].AsInt());
        }

        [Test]
        public void MergingKeepsBothSidesAndCopies()
        {
            JsonValue a = JsonValue.Parse("{\"x\":1,\"o\":{\"p\":1,\"q\":[1,2]},\"k\":{\"z\":1}}");
            JsonValue b = JsonValue.Parse("{\"y\":2,\"o\":{\"q\":[3],\"r\":4},\"k\":5}");
            JsonValue merged = JsonMerge.Deep(a, b);
            Assert.IsTrue(merged.StructurallyEquals(JsonValue.Parse("{\"x\":1,\"y\":2,\"o\":{\"p\":1,\"q\":[3],\"r\":4},\"k\":5}")));
            merged["o"].Set("p", 9);
            Assert.AreEqual(1, a["o"]["p"].AsInt());
            Assert.IsTrue(JsonMerge.Deep(null, b).StructurallyEquals(b));
            Assert.IsTrue(JsonMerge.Deep(a, null).StructurallyEquals(a));
            Assert.AreEqual(5, JsonMerge.Deep(a, JsonValue.Of(5)).AsInt());
        }

        [Test]
        public void EventsWithoutEffectsShowAtOnce()
        {
            PlaybackQueue queue = new PlaybackQueue();
            List<ViewEvent> played = new List<ViewEvent>();
            queue.Played += played.Add;
            queue.Enqueue(Spawn(1));
            queue.Enqueue(Event("{\"t\":\"turn\",\"seat\":2}"));
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(1, queue.Display.ObjectCount);
            Assert.AreEqual(2, queue.Display.Turn);
            Assert.AreEqual(2, played.Count);
            queue.Enqueue(null);
            Assert.AreEqual(2, played.Count);
        }

        [Test]
        public void AnEffectHoldsBackWhatFollowsUntilItIsOver()
        {
            PlaybackQueue queue = new PlaybackQueue();
            List<ViewEvent> played = new List<ViewEvent>();
            queue.Played += played.Add;
            queue.Enqueue(Spawn(1));
            ViewEvent fx = Fx(1);
            queue.Enqueue(fx);
            queue.Enqueue(Spawn(2));
            Assert.IsTrue(queue.IsBusy);
            Assert.AreSame(fx, queue.ActiveEffect);
            Assert.AreEqual(0f, queue.Progress, 1e-6);
            Assert.AreEqual(1, queue.Display.ObjectCount);

            queue.Update(0.25);
            Assert.AreEqual(0.25f, queue.Progress, 1e-6);
            Assert.AreEqual(1, queue.Display.ObjectCount);
            queue.Update(-5);
            Assert.AreEqual(0.25f, queue.Progress, 1e-6);

            queue.Update(0.8);
            Assert.IsNull(queue.ActiveEffect);
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(2, queue.Display.ObjectCount);
            Assert.AreEqual(2, played.Count);
        }

        [Test]
        public void EffectsPlayOneAfterTheOtherAndFasterWhenManyWait()
        {
            PlaybackQueue queue = new PlaybackQueue();
            ViewEvent first = Fx(1);
            ViewEvent second = Fx(1);
            queue.Enqueue(first);
            Assert.AreEqual(1.0, queue.Speed, 1e-9);
            queue.Enqueue(second);
            queue.Enqueue(Spawn(1));
            Assert.AreEqual(1.5, queue.Speed, 1e-9);
            queue.Update(0.5);
            Assert.AreSame(first, queue.ActiveEffect);
            Assert.AreEqual(0.75f, queue.Progress, 1e-6);
            queue.Update(0.2);
            Assert.AreSame(second, queue.ActiveEffect);
            Assert.AreEqual(1.0, queue.Speed, 1e-9);
            Assert.AreEqual(0, queue.Display.ObjectCount);
            queue.Update(1.0);
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(1, queue.Display.ObjectCount);
        }

        [Test]
        public void EffectsWithoutDurationDoNotWait()
        {
            PlaybackQueue queue = new PlaybackQueue();
            queue.Enqueue(Fx(0));
            queue.Enqueue(Spawn(1));
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(1, queue.Display.ObjectCount);
        }

        [Test]
        public void ALongBacklogSkipsItsOldestEffects()
        {
            PlaybackQueue queue = new PlaybackQueue();
            int total = PlaybackQueue.MaxWaitingEffects + 20;
            for (int i = 0; i < total; i++)
            {
                queue.Enqueue(Fx(1));
                queue.Enqueue(Spawn(i + 1));
            }
            Assert.AreEqual(PlaybackQueue.MaxSpeed, queue.Speed, 1e-9);
            queue.Update(1.0);
            Assert.IsTrue(queue.IsBusy);
            Assert.GreaterOrEqual(queue.Display.ObjectCount, 20);
            for (int i = 0; i < 2000 && queue.IsBusy; i++)
                queue.Update(0.1);
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(total, queue.Display.ObjectCount);
        }

        [Test]
        public void AResetDropsWhatWasWaiting()
        {
            PlaybackQueue queue = new PlaybackQueue();
            queue.Enqueue(Spawn(1));
            queue.Enqueue(Fx(5));
            queue.Enqueue(Spawn(2));
            queue.Enqueue(Event("{\"t\":\"reset\"}"));
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(0, queue.Display.ObjectCount);
            queue.Enqueue(Spawn(3));
            Assert.IsNotNull(queue.Display.Get(3));
            queue.Update(10);
            Assert.AreEqual(1, queue.Display.ObjectCount);
        }

        [Test]
        public void FinishShowsEverythingAtOnce()
        {
            PlaybackQueue queue = new PlaybackQueue();
            queue.Enqueue(Fx(5));
            queue.Enqueue(Spawn(1));
            queue.Enqueue(Fx(5));
            queue.Enqueue(Spawn(2));
            queue.Finish();
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(2, queue.Display.ObjectCount);
            Assert.AreEqual(1.0, queue.Speed, 1e-9);
        }

        [Test]
        public void ResetCopiesAnotherModelWithoutPlaying()
        {
            ViewModel source = new ViewModel();
            source.Apply(Spawn(1));
            source.Apply(Event("{\"t\":\"turn\",\"seat\":1}"));
            source.Apply(Event("{\"t\":\"stage\",\"background\":\"#000000\"}"));

            PlaybackQueue queue = new PlaybackQueue();
            int played = 0;
            queue.Played += e => played++;
            queue.Enqueue(Fx(5));
            queue.Enqueue(Spawn(9));
            queue.Reset(source);
            Assert.IsFalse(queue.IsBusy);
            Assert.AreEqual(0, played);
            Assert.AreEqual(1, queue.Display.ObjectCount);
            Assert.AreEqual(1, queue.Display.Turn);
            Assert.IsNotNull(queue.Display.GetCustom("stage"));
            queue.Reset(null);
            Assert.AreEqual(0, queue.Display.ObjectCount);
        }

        [Test]
        public void AGameFeedsTheQueue()
        {
            using (GameSession session = new GameSession(TestSupport.LoadGomoku()))
            {
                PlaybackQueue queue = new PlaybackQueue();
                session.EventApplied += queue.Enqueue;
                session.NewGame(TestSupport.Options("{\"size\":9}"));
                TestSupport.PlaceOk(session, 1, 5, 5);
                Assert.IsFalse(queue.IsBusy);
                Assert.AreEqual(session.View.ObjectCount, queue.Display.ObjectCount);
                Assert.AreEqual(session.View.Turn, queue.Display.Turn);
                Assert.AreEqual(9, queue.Display.GetCustom("board").Raw["columns"].AsInt());
                Assert.IsTrue(session.Undo());
                Assert.AreEqual(0, queue.Display.ObjectCount);
            }
        }
    }
}
