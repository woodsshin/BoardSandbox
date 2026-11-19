using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Net;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // What is said in a room while the chat is put away: the lines of the others come up as notices and are counted.
    [TestFixture]
    public class ChatWatchTests
    {
        private double _now;

        private GameHost Host()
        {
            _now = 0;
            return new GameHost(new ModCatalog(new[] { TestSupport.ModsRoot }), new GameHostOptions { Clock = () => _now });
        }

        private static void Pump(params NetRoom[] rooms)
        {
            for (int round = 0; round < 4; round++)
            {
                foreach (NetRoom room in rooms)
                    room.Update();
            }
        }

        private static List<string> Texts(ToastQueue queue, double now)
        {
            List<string> texts = new List<string>();
            foreach (Toast toast in queue.Active(now))
                texts.Add(toast.Text);
            return texts;
        }

        // Three people in a room of a game for two: the host Ann, and Bob and Cy, who are the ones that watch the chat here.
        private sealed class Scene
        {
            public GameHost Host;
            public NetHostRoom Ann;
            public NetClientRoom Bob;
            public NetClientRoom Cy;
        }

        private Scene Room()
        {
            GameHost host = Host();
            Assert.IsTrue(host.SelectMod("gomoku"));
            LoopbackNetwork network = new LoopbackNetwork();
            Scene scene = new Scene { Host = host };
            scene.Ann = new NetHostRoom(host, network.Host("Ann"));
            Assert.IsTrue(host.StartNewGame());
            scene.Bob = new NetClientRoom(host.Mod, network.Join("Bob"));
            scene.Cy = new NetClientRoom(host.Mod, network.Join("Cy"));
            Pump(scene.Ann, scene.Bob, scene.Cy);
            return scene;
        }

        [Test]
        public void LinesAreNoNewsWhileTheChatIsShown()
        {
            Scene room = Room();
            using (room.Host)
            {
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(room.Bob, true, notices, 0.0);
                room.Ann.Say("hello", 1.0);
                room.Cy.Say("hi", 1.0);
                Pump(room.Ann, room.Bob, room.Cy);
                watch.Update(room.Bob, true, notices, 1.0);
                Assert.AreEqual(0, watch.Unread);
                Assert.AreEqual(0, Texts(notices, 1.0).Count);

                // Nor are they when the chat goes away afterwards: what was read stays read.
                watch.Update(room.Bob, false, notices, 2.0);
                Assert.AreEqual(0, watch.Unread);
                Assert.AreEqual(0, Texts(notices, 2.0).Count);
                room.Ann.Dispose();
            }
        }

        [Test]
        public void WhatTheOthersSayWhileTheChatIsAwayComesUpAndIsCounted()
        {
            Scene room = Room();
            using (room.Host)
            {
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(room.Bob, false, notices, 0.0);
                room.Ann.Say("hello", 1.0);
                room.Cy.Say("hi", 1.0);
                Pump(room.Ann, room.Bob, room.Cy);
                watch.Update(room.Bob, false, notices, 1.0);
                Assert.AreEqual(2, watch.Unread);
                AssertTwo(Texts(notices, 1.0), "Ann: hello", "Cy: hi");

                // Nothing new, nothing more to show or to count.
                watch.Update(room.Bob, false, notices, 1.5);
                Assert.AreEqual(2, watch.Unread);
                Assert.AreEqual(2, Texts(notices, 1.5).Count);

                room.Ann.Say("one more", 2.0);
                Pump(room.Ann, room.Bob, room.Cy);
                watch.Update(room.Bob, false, notices, 2.0);
                Assert.AreEqual(3, watch.Unread);
                Assert.AreEqual(3, Texts(notices, 2.0).Count);
                Assert.AreEqual("Ann: one more", Texts(notices, 2.0)[2]);

                // With the chat back they are read, and the count starts again.
                watch.Update(room.Bob, true, notices, 3.0);
                Assert.AreEqual(0, watch.Unread);
                watch.Update(room.Bob, false, notices, 3.5);
                Assert.AreEqual(0, watch.Unread);
                room.Ann.Dispose();
            }
        }

        private static void AssertTwo(List<string> actual, string first, string second)
        {
            Assert.AreEqual(2, actual.Count);
            Assert.AreEqual(first, actual[0]);
            Assert.AreEqual(second, actual[1]);
        }

        [Test]
        public void OnlyTheNewestFewComeUpButAllOfThemAreCounted()
        {
            Scene room = Room();
            using (room.Host)
            {
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(room.Bob, false, notices, 0.0);
                for (int i = 1; i <= 5; i++)
                    room.Ann.Say("line " + i, i);
                Pump(room.Ann, room.Bob, room.Cy);
                watch.Update(room.Bob, false, notices, 6.0);
                Assert.AreEqual(5, watch.Unread);
                List<string> texts = Texts(notices, 6.0);
                Assert.AreEqual(ChatWatch.MaxNotices, texts.Count);
                Assert.AreEqual("Ann: line 3", texts[0]);
                Assert.AreEqual("Ann: line 5", texts[2]);

                // The lines of the player here are not among the ones that count, and not among the newest either.
                ToastQueue more = new ToastQueue(10);
                room.Ann.Say("a", 20.0);
                room.Ann.Say("b", 21.0);
                Pump(room.Ann, room.Bob, room.Cy);
                room.Bob.Say("mine", 22.0);
                Pump(room.Ann, room.Bob, room.Cy);
                room.Ann.Say("c", 23.0);
                room.Ann.Say("d", 24.0);
                Pump(room.Ann, room.Bob, room.Cy);
                int before = watch.Unread;
                watch.Update(room.Bob, false, more, 25.0);
                Assert.AreEqual(before + 4, watch.Unread);
                List<string> newest = Texts(more, 25.0);
                Assert.AreEqual(ChatWatch.MaxNotices, newest.Count);
                Assert.AreEqual("Ann: b", newest[0]);
                Assert.AreEqual("Ann: c", newest[1]);
                Assert.AreEqual("Ann: d", newest[2]);
                room.Ann.Dispose();
            }
        }

        [Test]
        public void TheOwnLinesOfThePlayerAreNoNews()
        {
            Scene room = Room();
            using (room.Host)
            {
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(room.Bob, false, notices, 0.0);
                room.Bob.Say("talking to myself", 1.0);
                Pump(room.Ann, room.Bob, room.Cy);
                Assert.AreEqual(1, room.Bob.Chat.Count, "the line came back to its player");
                watch.Update(room.Bob, false, notices, 1.0);
                Assert.AreEqual(0, watch.Unread);
                Assert.AreEqual(0, Texts(notices, 1.0).Count);
                room.Ann.Dispose();
            }
        }

        [Test]
        public void WhatWasSaidBeforeTheWatchBeganAndInAnotherRoomIsNoNews()
        {
            Scene first = Room();
            using (first.Host)
            {
                first.Ann.Say("before", 1.0);
                Pump(first.Ann, first.Bob, first.Cy);
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(first.Bob, false, notices, 2.0);
                Assert.AreEqual(0, watch.Unread, "the lines that were there when the player came are not news");
                Assert.AreEqual(0, Texts(notices, 2.0).Count);
                first.Ann.Say("after", 3.0);
                Pump(first.Ann, first.Bob, first.Cy);
                watch.Update(first.Bob, false, notices, 4.0);
                Assert.AreEqual(1, watch.Unread);

                // Another room starts from nothing, whatever the count was.
                Scene second = Room();
                using (second.Host)
                {
                    second.Ann.Say("old", 1.0);
                    Pump(second.Ann, second.Bob, second.Cy);
                    watch.Update(second.Bob, false, notices, 5.0);
                    Assert.AreEqual(0, watch.Unread);
                    second.Ann.Say("new", 6.0);
                    Pump(second.Ann, second.Bob, second.Cy);
                    watch.Update(second.Bob, false, notices, 7.0);
                    Assert.AreEqual(1, watch.Unread);
                    second.Ann.Dispose();
                }

                // And with no room there is nothing to say.
                watch.Update(null, false, notices, 8.0);
                Assert.AreEqual(0, watch.Unread);
                watch.Update(null, true, null, 9.0);
                first.Ann.Dispose();
            }
        }

        [Test]
        public void AWatchWithoutAPlaceForNoticesStillCounts()
        {
            Scene room = Room();
            using (room.Host)
            {
                ChatWatch watch = new ChatWatch();
                watch.Update(room.Bob, false, null, 0.0);
                room.Ann.Say("hello", 1.0);
                Pump(room.Ann, room.Bob, room.Cy);
                watch.Update(room.Bob, false, null, 1.0);
                Assert.AreEqual(1, watch.Unread);
                room.Ann.Dispose();
            }
        }

        [Test]
        public void ALongNameIsShortenedInTheNotice()
        {
            GameHost host = Host();
            using (host)
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom ann = new NetHostRoom(host, network.Host("A name that is as long as a name may be"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(ann, bob);
                ChatWatch watch = new ChatWatch();
                ToastQueue notices = new ToastQueue(10);
                watch.Update(bob, false, notices, 0.0);
                ann.Say("hello", 1.0);
                Pump(ann, bob);
                watch.Update(bob, false, notices, 1.0);
                List<string> texts = Texts(notices, 1.0);
                Assert.AreEqual(1, texts.Count);
                Assert.IsTrue(texts[0].EndsWith(": hello"));
                Assert.AreEqual(ChatWatch.MaxNameLength + ": hello".Length, texts[0].Length);
                StringAssert.Contains("…", texts[0]);
                ann.Dispose();
            }
        }
    }
}
