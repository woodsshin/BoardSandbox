using System;
using System.Collections.Generic;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Net;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // What the players of a room say to each other: the host passes it on, cleaned and under the name it has of the sender.
    [TestFixture]
    public class ChatTests
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

        private static bool SendRaw(INetTransport from, string text)
        {
            return from.Send(from.HostId, Encoding.UTF8.GetBytes(text));
        }

        private static string Hello(GameHost host, string name)
        {
            return "{\"t\":\"hello\",\"proto\":1,\"mod\":\"" + host.Mod.Manifest.Id + "\",\"hash\":\"" + host.Mod.ContentHash + "\",\"name\":\"" + name + "\"}";
        }

        private static string ChatJson(string text)
        {
            return JsonValue.NewObject().Set("t", "chat").Set("text", text).ToJson();
        }

        // What has arrived at a raw transport, as the messages it was.
        private static List<JsonValue> Messages(INetTransport transport)
        {
            List<JsonValue> messages = new List<JsonValue>();
            while (transport.Poll(out NetEvent e))
            {
                if (e.Kind == NetEventKind.Data)
                    messages.Add(JsonValue.Parse(Encoding.UTF8.GetString(e.Data)));
            }
            return messages;
        }

        private static int CountOf(List<JsonValue> messages, string type)
        {
            int count = 0;
            foreach (JsonValue message in messages)
                count += message["t"].AsString() == type ? 1 : 0;
            return count;
        }

        private static List<string> Texts(NetRoom room)
        {
            List<string> texts = new List<string>();
            foreach (ChatLine line in room.Chat)
                texts.Add(line.Text);
            return texts;
        }

        private static void AreSameLines(NetRoom expected, NetRoom actual)
        {
            Assert.AreEqual(expected.Chat.Count, actual.Chat.Count);
            for (int i = 0; i < expected.Chat.Count; i++)
            {
                Assert.AreEqual(expected.Chat[i].Number, actual.Chat[i].Number);
                Assert.AreEqual(expected.Chat[i].From, actual.Chat[i].From);
                Assert.AreEqual(expected.Chat[i].Name, actual.Chat[i].Name);
                Assert.AreEqual(expected.Chat[i].Text, actual.Chat[i].Text);
            }
        }

        [Test]
        public void WhatIsSaidIsCleanedAndKeptShort()
        {
            Assert.AreEqual(string.Empty, ChatText.Clean(null));
            Assert.AreEqual(string.Empty, ChatText.Clean(string.Empty));
            Assert.AreEqual(string.Empty, ChatText.Clean("   \r\n\t  "));
            Assert.AreEqual("hello world", ChatText.Clean("  hello \r\n\t world  "), "line breaks and runs of blanks become one space");
            Assert.AreEqual("a b", ChatText.Clean("a\u3000\u00a0b"), "other blanks as well");
            Assert.AreEqual("abc", ChatText.Clean("a\u0001b\u007fc"), "control characters are left out");
            Assert.AreEqual("abcdef", ChatText.Clean("abc\u202edef"), "so are the marks that turn the text around");
            Assert.AreEqual("abc", ChatText.Clean("a\u200bb\ufeffc"), "and the blanks that cannot be seen");
            Assert.AreEqual("ab", ChatText.Clean("a\ud83db"), "half of a surrogate pair is no character");
            Assert.AreEqual("ab", ChatText.Clean("a\ude00b"));

            // Hangul, and a pair that is a whole character, are left as they are.
            Assert.AreEqual("안녕하세요 😀", ChatText.Clean("안녕하세요 😀"));

            string tooLong = ChatText.Clean(new string('a', 500));
            Assert.AreEqual(ChatText.MaxLength, tooLong.Length);
            Assert.AreEqual(199, ChatText.Clean(new string('a', 199) + "😀").Length, "a pair that does not fit is not cut in two");
            string fits = ChatText.Clean(new string('a', 198) + "😀");
            Assert.AreEqual(200, fits.Length);
            Assert.IsTrue(char.IsLowSurrogate(fits[199]));
            Assert.AreEqual(199, ChatText.Clean(new string('a', 199) + " b").Length, "no blank is left at the end after the cut");
        }

        [Test]
        public void ALineGoesThroughTheHostToEveryoneAndBackToItsSender()
        {
            foreach (string game in new[] { "gomoku", "cubic3" })
            {
                using (GameHost host = Host())
                {
                    Assert.IsTrue(host.SelectMod(game), game);
                    LoopbackNetwork network = new LoopbackNetwork();
                    NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                    Assert.IsTrue(host.StartNewGame());
                    NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                    NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                    Assert.IsTrue(room.CanChat, game);
                    Assert.IsFalse(bob.CanChat, "before the host has let the guest in");
                    Assert.IsFalse(bob.Say("too early", 0.0));
                    Pump(room, bob, cy);
                    Assert.IsTrue(bob.CanChat, game);
                    Assert.AreEqual(0, room.Chat.Count);
                    Assert.AreEqual(0, room.ChatTotal);

                    Assert.IsTrue(bob.Say("hi all", 1.0));
                    Assert.AreEqual(0, bob.Chat.Count, "the line is added when it comes back from the host, like everybody else's");
                    Pump(room, bob, cy);
                    foreach (NetRoom each in new NetRoom[] { room, bob, cy })
                    {
                        Assert.AreEqual(1, each.Chat.Count, game);
                        Assert.AreEqual(1, each.Chat[0].Number);
                        Assert.AreEqual(bob.LocalId, each.Chat[0].From);
                        Assert.AreEqual("Bob", each.Chat[0].Name);
                        Assert.AreEqual("hi all", each.Chat[0].Text);
                    }

                    Assert.IsTrue(room.Say("hello Bob", 2.0));
                    Assert.AreEqual(2, room.Chat.Count, "the host's own line is there at once");
                    Pump(room, bob, cy);
                    Assert.IsTrue(cy.Say("and me", 3.0));
                    Pump(room, bob, cy);
                    Assert.AreEqual(3, room.ChatTotal);
                    Assert.AreEqual(3, cy.ChatTotal);
                    AreSameLines(room, bob);
                    AreSameLines(room, cy);
                    Assert.AreEqual(room.LocalId, room.Chat[1].From);
                    Assert.AreEqual("Ann", cy.Chat[1].Name);
                    Assert.AreEqual(cy.LocalId, bob.Chat[2].From);
                    Assert.AreEqual("Cy", bob.Chat[2].Name);
                    room.Dispose();
                }
            }
        }

        [Test]
        public void ThePeopleWhoWatchTalkToo()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);
                Assert.AreEqual(0, cy.LocalPlayer.Seat, "the third person has no seat in a game for two");
                Assert.IsTrue(cy.Say("nice move", 1.0));
                Pump(room, bob, cy);
                Assert.AreEqual("nice move", bob.Chat[0].Text);
                Assert.AreEqual("nice move", room.Chat[0].Text);
                room.Dispose();
            }
        }

        [Test]
        public void TheHostDecidesWhoSaidItAndUnderWhatName()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                INetTransport eve = network.Join("Eve");
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, bob);
                List<JsonValue> arrived = Messages(eve);
                JsonValue welcome = null;
                foreach (JsonValue message in arrived)
                {
                    if (message["t"].AsString() == "welcome")
                        welcome = message;
                }
                Assert.IsNotNull(welcome);
                Assert.IsTrue(welcome["chat"].AsBool(false), "the welcome says that this host passes the chat on");

                string asHost = "{\"t\":\"chat\",\"from\":\"" + room.LocalId + "\",\"name\":\"Ann\",\"text\":\"I am the host\"}";
                SendRaw(eve, asHost);
                Pump(room, bob);
                Assert.AreEqual(1, room.Chat.Count);
                Assert.AreEqual("Eve", room.Chat[0].Name);
                Assert.AreNotEqual(room.LocalId, room.Chat[0].From);
                Assert.AreEqual("Eve", bob.Chat[0].Name);
                Assert.AreEqual(room.Chat[0].From, bob.Chat[0].From);
                Assert.AreEqual("I am the host", bob.Chat[0].Text);

                // Somebody the host has not let in cannot talk.
                INetTransport outsider = network.Join("Mallory");
                SendRaw(outsider, ChatJson("let me in"));
                Pump(room, bob);
                Assert.AreEqual(1, room.Chat.Count);
                Assert.AreEqual(1, bob.Chat.Count);

                // What was said stays when the one who said it leaves.
                eve.Dispose();
                Pump(room, bob);
                Assert.AreEqual("Eve", room.Chat[0].Name, "what was said stays, under the name it was said");
                Assert.AreEqual("Eve", bob.Chat[0].Name);
                room.Dispose();
            }
        }

        [Test]
        public void TheHostCleansWhatIsSentBeforeItIsPassedOn()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                INetTransport eve = network.Join("Eve");
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, bob);

                SendRaw(eve, ChatJson("  two\r\nlines\u0001 and\u202e  turned  "));
                SendRaw(eve, ChatJson(new string('x', 5000)));
                Pump(room, bob);
                Assert.AreEqual(2, bob.Chat.Count);
                Assert.AreEqual("two lines and turned", bob.Chat[0].Text);
                Assert.AreEqual(2, room.Chat.Count);
                Assert.AreEqual("two lines and turned", room.Chat[0].Text);
                Assert.AreEqual(ChatText.MaxLength, bob.Chat[1].Text.Length);
                Assert.AreEqual(ChatText.MaxLength, room.Chat[1].Text.Length);
                room.Dispose();
            }
        }

        [Test]
        public void ALineThatIsNoGoodIsLeftOutAndCostsNothing()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                INetTransport eve = network.Join("Eve");
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, bob);

                SendRaw(eve, "{\"t\":\"chat\"}");
                SendRaw(eve, "{\"t\":\"chat\",\"text\":5}");
                SendRaw(eve, "{\"t\":\"chat\",\"text\":null}");
                SendRaw(eve, "{\"t\":\"chat\",\"text\":[\"a\"]}");
                SendRaw(eve, ChatJson("   "));
                SendRaw(eve, ChatJson("\u202e\u200b"));
                SendRaw(eve, "not json at all");
                Pump(room, bob);
                Assert.AreEqual(0, room.Chat.Count);
                Assert.AreEqual(0, bob.Chat.Count);

                // What the player here says without saying anything is not said, and is not counted against the player either.
                Assert.IsFalse(bob.Say(null, 1.0));
                Assert.IsFalse(bob.Say(string.Empty, 1.0));
                Assert.IsFalse(bob.Say("   \n ", 1.0));
                Assert.IsFalse(room.Say("\u202e", 1.0));
                for (int i = 0; i < ChatLimiter.Limit; i++)
                    Assert.IsTrue(bob.Say("line " + i, 1.0), "line " + i);
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit, room.Chat.Count);

                // The ones that were no good did not use up the lines the host takes from Eve.
                for (int i = 0; i < ChatLimiter.Limit; i++)
                    SendRaw(eve, ChatJson("eve " + i));
                Pump(room, bob);
                Assert.AreEqual(2 * ChatLimiter.Limit, room.Chat.Count);
                room.Dispose();
            }
        }

        [Test]
        public void APlayerWhoSaysTooMuchIsNotPassedOnAndIsToldOnce()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                INetTransport eve = network.Join("Eve");
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, bob);
                Messages(eve);

                _now = 10.0;
                for (int i = 0; i < 8; i++)
                    SendRaw(eve, ChatJson("spam " + i));
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit, room.Chat.Count, "the host takes " + ChatLimiter.Limit + " lines");
                Assert.AreEqual(ChatLimiter.Limit, bob.Chat.Count);
                List<JsonValue> told = Messages(eve);
                Assert.AreEqual(1, CountOf(told, "note"), "told once, not for every line");
                foreach (JsonValue message in told)
                {
                    if (message["t"].AsString() == "note")
                        StringAssert.Contains("too fast", message["text"].AsString());
                }

                // Somebody else is not held back by it.
                Assert.IsTrue(bob.Say("I am fine", 10.0));
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit + 1, room.Chat.Count);

                // A little later, still inside the window, and then right after it.
                _now = 10.0 + ChatLimiter.Window - 0.1;
                SendRaw(eve, ChatJson("early"));
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit + 1, room.Chat.Count);
                Assert.AreEqual(0, CountOf(Messages(eve), "note"), "not told again inside the window");
                _now = 10.0 + ChatLimiter.Window;
                SendRaw(eve, ChatJson("later"));
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit + 2, room.Chat.Count);
                Assert.AreEqual("later", bob.Chat[bob.Chat.Count - 1].Text);
                Assert.AreEqual("Eve", bob.Chat[bob.Chat.Count - 1].Name);
                room.Dispose();
            }
        }

        [Test]
        public void ThePlayerHereWhoSaysTooMuchIsToldAndNothingIsSent()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                List<string> notices = new List<string>();
                bob.Notice += (text, level) => notices.Add(text);
                room.Notice += (text, level) => notices.Add("host: " + text);
                Pump(room, bob);
                notices.Clear();

                // The host's clock is the one that counts for the lines it takes: it keeps time with the player here.
                _now = 1.0;
                for (int i = 0; i < ChatLimiter.Limit; i++)
                    Assert.IsTrue(bob.Say("line " + i, _now));
                Assert.IsFalse(bob.Say("one too many", 1.5));
                Assert.IsFalse(bob.Say("and another", 2.0));
                Assert.AreEqual(1, notices.Count, "told once");
                StringAssert.Contains("too fast", notices[0]);
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit, room.Chat.Count, "what was held back here never reached the host");
                Assert.AreEqual(1, notices.Count, "and the host had nothing to say to the player either");
                _now = 1.0 + ChatLimiter.Window;
                Assert.IsTrue(bob.Say("now", _now));
                Pump(room, bob);
                Assert.AreEqual(ChatLimiter.Limit + 1, room.Chat.Count);

                // The host is held to the same.
                for (int i = 0; i < ChatLimiter.Limit; i++)
                    Assert.IsTrue(room.Say("host " + i, 100.0));
                Assert.IsFalse(room.Say("too many", 101.0));
                room.Dispose();
            }
        }

        [Test]
        public void OnlyTheNewestLinesAreKept()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);

                int said = NetRoom.MaxChatLines + 50;
                for (int i = 1; i <= said; i++)
                {
                    Assert.IsTrue(room.Say("line " + i, i), "line " + i);
                    if (i % 20 == 0)
                        Pump(room, bob);
                }
                Pump(room, bob);
                foreach (NetRoom each in new NetRoom[] { room, bob })
                {
                    Assert.AreEqual(NetRoom.MaxChatLines, each.Chat.Count);
                    Assert.AreEqual(said, each.ChatTotal);
                    Assert.AreEqual(51, each.Chat[0].Number);
                    Assert.AreEqual("line 51", each.Chat[0].Text);
                    Assert.AreEqual(said, each.Chat[each.Chat.Count - 1].Number);
                    Assert.AreEqual("line " + said, each.Chat[each.Chat.Count - 1].Text);
                }
                room.Dispose();
            }
        }

        [Test]
        public void AGuestOfAnOlderHostCannotTalkAndIsToldWhy()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();

                // A host of an older version: the plain welcome, and what it does not know it does not answer.
                INetTransport older = network.Host("Ann");
                NetClientRoom guest = new NetClientRoom(host.Mod, network.Join("Bob"));
                List<string> notices = new List<string>();
                guest.Notice += (text, level) => notices.Add(text);
                Pump(guest);
                Assert.IsFalse(guest.CanChat);
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"proto\":1,\"you\":\"" + guest.LocalId + "\"}"));
                Pump(guest);
                Assert.IsTrue(guest.Joined);
                Assert.IsFalse(guest.CanChat);
                Assert.AreEqual(1, notices.Count);
                StringAssert.Contains("no chat", notices[0]);
                Messages(older);
                Assert.IsFalse(guest.Say("anybody there?", 1.0));
                Assert.AreEqual(0, CountOf(Messages(older), "chat"), "nothing is sent to a host that would not know what to do with it");

                // A host that says it passes the chat on.
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"proto\":1,\"you\":\"" + guest.LocalId + "\",\"chat\":true}"));
                Pump(guest);
                Assert.IsTrue(guest.CanChat);
                Assert.IsTrue(guest.Say("  anybody   there? ", 2.0));
                List<JsonValue> sent = Messages(older);
                Assert.AreEqual(1, CountOf(sent, "chat"));
                foreach (JsonValue message in sent)
                {
                    if (message["t"].AsString() != "chat")
                        continue;
                    Assert.AreEqual("anybody there?", message["text"].AsString(), "what goes out is cleaned already");
                    Assert.IsFalse(message.Has("from"), "a guest does not say who it is: the host knows");
                    Assert.IsFalse(message.Has("name"));
                }
                guest.Dispose();
            }
        }

        [Test]
        public void AGuestDoesNotTrustTheHostWithWhatIsDrawnOnItsScreen()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                INetTransport fake = network.Host("Ann");
                NetClientRoom guest = new NetClientRoom(host.Mod, network.Join("Bob"));
                INetTransport eve = network.Join("Eve");
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"proto\":1,\"you\":\"" + guest.LocalId + "\",\"chat\":true}"));
                Pump(guest);
                Assert.IsTrue(guest.CanChat);

                JsonValue ok = JsonValue.NewObject().Set("t", "chat").Set("from", "7").Set("name", "  Zed\u0001  ").Set("text", "a\r\nb\u202e" + new string('x', 400));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes(ok.ToJson()));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"from\":\"nobody\",\"name\":\"Zed\",\"text\":\"who?\"}"));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"name\":\"Zed\",\"text\":\"who?\"}"));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"from\":\"7\",\"name\":\"Zed\"}"));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"from\":\"7\",\"name\":\"Zed\",\"text\":\"  \"}"));
                fake.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"from\":\"8\",\"text\":\"no name\"}"));
                // Only the host can tell the guest what was said.
                eve.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"from\":\"9\",\"name\":\"Eve\",\"text\":\"psst\"}"));
                Pump(guest);

                Assert.AreEqual(2, guest.Chat.Count);
                Assert.AreEqual(7UL, guest.Chat[0].From);
                Assert.AreEqual("Zed", guest.Chat[0].Name);
                Assert.AreEqual("a b" + new string('x', ChatText.MaxLength - 3), guest.Chat[0].Text);
                Assert.AreEqual("Player", guest.Chat[1].Name, "a line without a name is by a player");
                Assert.AreEqual("no name", guest.Chat[1].Text);
                guest.Dispose();
            }
        }

        [Test]
        public void ARoomThatIsClosedSaysNothing()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.IsTrue(bob.Say("bye", 1.0));
                Pump(room, bob);
                room.Dispose();
                Assert.IsFalse(room.CanChat);
                Assert.IsFalse(room.Say("anybody?", 2.0));
                Pump(bob);
                Assert.IsTrue(bob.IsClosed);
                Assert.IsFalse(bob.CanChat);
                Assert.IsFalse(bob.Say("anybody?", 2.0));
                Assert.AreEqual(1, bob.Chat.Count, "what was said stays to be read");
            }
        }

        [Test]
        public void TheLimiterTakesAFewLinesInAWindowThatSlides()
        {
            // Times that a double holds exactly, so that the edges of the window are what is tested.
            ChatLimiter limiter = new ChatLimiter();
            for (int i = 0; i < ChatLimiter.Limit; i++)
                Assert.IsTrue(limiter.Allow(i * 0.25), "line " + i);
            Assert.IsFalse(limiter.Allow(2.0));
            Assert.IsFalse(limiter.Allow(ChatLimiter.Window - 0.125), "the first line is still inside the window");
            Assert.IsTrue(limiter.Allow(ChatLimiter.Window), "the first line has gone out of it");
            Assert.IsFalse(limiter.Allow(ChatLimiter.Window + 0.125), "the second line, from 0.25, is still in");
            Assert.IsTrue(limiter.Allow(ChatLimiter.Window + 0.25));
            Assert.IsTrue(limiter.Allow(ChatLimiter.Window + 0.5));
            Assert.IsTrue(limiter.Allow(ChatLimiter.Window + 0.75));
            Assert.IsTrue(limiter.Allow(ChatLimiter.Window + 1.0));
            Assert.IsFalse(limiter.Allow(ChatLimiter.Window + 1.5), "five lines again within one window");

            // A player who says a line now and then is never held back.
            ChatLimiter slow = new ChatLimiter();
            for (int i = 0; i < 100; i++)
                Assert.IsTrue(slow.Allow(i * 2.0), "line " + i);

            // The warning comes once in a window.
            ChatLimiter warned = new ChatLimiter();
            Assert.IsTrue(warned.ShouldWarn(10.0));
            Assert.IsFalse(warned.ShouldWarn(10.0));
            Assert.IsFalse(warned.ShouldWarn(10.0 + ChatLimiter.Window - 0.125));
            Assert.IsTrue(warned.ShouldWarn(10.0 + ChatLimiter.Window));
        }
    }
}
