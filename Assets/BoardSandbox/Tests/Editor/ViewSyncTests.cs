using System;
using System.Collections.Generic;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Net;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // How the players of a game with tables of their own see each other's tables: the way their owners look at them.
    [TestFixture]
    public class ViewSyncTests
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

        private static ViewPose Turned(float degrees, float zoom = 1f)
        {
            double half = degrees * Math.PI / 360.0;
            return ViewPose.Trackball(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half), 0f, 55f, zoom);
        }

        private static bool Same(ViewPose a, ViewPose b)
        {
            if (a == null || b == null)
                return a == b;
            return a.HasTurn == b.HasTurn
                && Math.Abs(a.TurnX - b.TurnX) < 1e-3f && Math.Abs(a.TurnY - b.TurnY) < 1e-3f && Math.Abs(a.TurnZ - b.TurnZ) < 1e-3f && Math.Abs(a.TurnW - b.TurnW) < 1e-3f
                && Math.Abs(a.Yaw - b.Yaw) < 0.01f && Math.Abs(a.Pitch - b.Pitch) < 0.01f && Math.Abs(a.Zoom - b.Zoom) < 1e-3f;
        }

        private static bool SendRaw(INetTransport from, string text)
        {
            return from.Send(from.HostId, Encoding.UTF8.GetBytes(text));
        }

        private static string Hello(GameHost host, string name)
        {
            return "{\"t\":\"hello\",\"proto\":1,\"mod\":\"" + host.Mod.Manifest.Id + "\",\"hash\":\"" + host.Mod.ContentHash + "\",\"name\":\"" + name + "\"}";
        }

        private static string ViewText(string extra)
        {
            return "{\"t\":\"view\"," + extra + "}";
        }

        [Test]
        public void AViewGoesThroughAMessageAndComesBackTheSame()
        {
            ViewPose turned = ViewPose.Trackball(0.1f, 0.7f, -0.2f, 0.6f, 12.5f, 55f, 0.8f);
            Assert.IsTrue(turned.HasTurn);
            double length = Math.Sqrt(turned.TurnX * turned.TurnX + turned.TurnY * turned.TurnY + turned.TurnZ * turned.TurnZ + turned.TurnW * turned.TurnW);
            Assert.IsTrue(Math.Abs(length - 1.0) < 1e-5, "the turn is a unit quaternion");

            JsonValue message = JsonValue.NewObject().Set("t", "view");
            turned.WriteTo(message);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse(message.ToJson()), out ViewPose back));
            Assert.IsTrue(Same(turned, back));
            Assert.IsTrue(turned.Near(back));

            ViewPose circling = ViewPose.Orbit(300f, 40f, 1.5f);
            JsonValue plain = JsonValue.NewObject();
            circling.WriteTo(plain);
            Assert.IsFalse(plain.Has("turn"));
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse(plain.ToJson()), out ViewPose again));
            Assert.IsFalse(again.HasTurn);
            Assert.IsTrue(Same(circling, again));
        }

        [Test]
        public void AViewIsNotTrustedBeyondItsRange()
        {
            Assert.IsFalse(ViewPose.TryRead(null, out _));
            Assert.IsFalse(ViewPose.TryRead(JsonValue.NewArray(), out _));
            Assert.IsFalse(ViewPose.TryRead(JsonValue.Parse("{\"yaw\":1}"), out _), "a view without a zoom is none");
            Assert.IsFalse(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":\"far\"}"), out _));

            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":1e9,\"yaw\":1e9,\"pitch\":-1e9}"), out ViewPose huge));
            Assert.AreEqual(ViewPose.MaxZoom, huge.Zoom);
            Assert.AreEqual(ViewPose.MaxAngle, huge.Yaw);
            Assert.AreEqual(-ViewPose.MaxAngle, huge.Pitch);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":0}"), out ViewPose none));
            Assert.AreEqual(ViewPose.MinZoom, none.Zoom);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":-3}"), out ViewPose negative));
            Assert.AreEqual(ViewPose.MinZoom, negative.Zoom);

            // A turn that is no quaternion is left out; one that is not of unit length is made one.
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":1,\"turn\":[0,0,0,0]}"), out ViewPose zero));
            Assert.IsFalse(zero.HasTurn);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":1,\"turn\":[0,0,0]}"), out ViewPose three));
            Assert.IsFalse(three.HasTurn);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":1,\"turn\":[0,\"x\",0,1]}"), out ViewPose text));
            Assert.IsFalse(text.HasTurn);
            Assert.IsTrue(ViewPose.TryRead(JsonValue.Parse("{\"zoom\":1,\"turn\":[0,0,0,4]}"), out ViewPose scaled));
            Assert.IsTrue(scaled.HasTurn);
            Assert.IsTrue(Math.Abs(scaled.TurnW - 1f) < 1e-6f);
            Assert.IsFalse(ViewPose.Trackball(float.NaN, 0f, 0f, 1f, 0f, 0f, 1f).HasTurn);
            Assert.AreEqual(1f, ViewPose.Orbit(float.NaN, float.PositiveInfinity, float.NaN).Zoom);
            Assert.AreEqual(0f, ViewPose.Orbit(float.NaN, float.PositiveInfinity, 1f).Yaw);
        }

        [Test]
        public void NearViewsAreTheOnesNobodyCouldTellApart()
        {
            ViewPose start = Turned(10f, 1f);
            Assert.IsTrue(start.Near(start));
            Assert.IsTrue(start.Near(Turned(10.1f, 1.001f)));
            Assert.IsFalse(start.Near(Turned(11f, 1f)));
            Assert.IsFalse(start.Near(Turned(10f, 1.01f)));
            Assert.IsFalse(start.Near(null));
            Assert.IsFalse(start.Near(ViewPose.Orbit(0f, 55f, 1f)), "a view that turns and one that does not are not alike");
            Assert.AreEqual(0f, start.TurnAngleTo(ViewPose.Orbit(0f, 55f, 1f)));
            Assert.IsTrue(Math.Abs(Turned(0f).TurnAngleTo(Turned(90f)) - 90f) < 0.01f);

            // The angles go round: just before the full circle and just after it are next to each other.
            Assert.IsTrue(ViewPose.Orbit(359.9f, 55f, 1f).Near(ViewPose.Orbit(0.1f, 55f, 1f)));
            Assert.IsFalse(ViewPose.Orbit(359f, 55f, 1f).Near(ViewPose.Orbit(1f, 55f, 1f)));
        }

        [Test]
        public void EveryPlayerSeesTheOthersTablesTheWayTheirOwnersLookAtThem()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Assert.IsFalse(bob.SharesViews, "before the host has let the guest in");
                Pump(room, bob, cy);
                Assert.IsTrue(room.SharesViews);
                Assert.IsTrue(bob.SharesViews);
                Assert.IsTrue(cy.SharesViews);
                Assert.IsNull(room.ViewOf(bob.LocalId));

                ViewPose fromBob = Turned(40f, 0.7f);
                bob.ShareView(fromBob, 1.0);
                Pump(room, bob, cy);
                Assert.IsTrue(Same(fromBob, room.ViewOf(bob.LocalId)));
                Assert.IsTrue(Same(fromBob, cy.ViewOf(bob.LocalId)));
                Assert.IsNull(bob.ViewOf(bob.LocalId), "the host does not send a player their own view back");
                Assert.IsNull(cy.ViewOf(cy.LocalId));

                ViewPose fromAnn = ViewPose.Orbit(30f, 50f, 1.4f);
                room.ShareView(fromAnn, 1.0);
                Pump(room, bob, cy);
                Assert.IsTrue(Same(fromAnn, bob.ViewOf(room.LocalId)));
                Assert.IsTrue(Same(fromAnn, cy.ViewOf(room.LocalId)));
                Assert.IsTrue(Same(fromBob, cy.ViewOf(bob.LocalId)), "what was shared before stays");

                // The owners of the tables are the people whose views these are.
                foreach (NetBoard board in cy.Boards)
                    Assert.IsNotNull(cy.FindPlayer(board.Owner));
                room.Dispose();
            }
        }

        [Test]
        public void ANewcomerSeesTheTablesTheWayTheyAreLookedAtFromTheStart()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                room.ShareView(Turned(20f, 0.9f), 1.0);
                bob.ShareView(Turned(-70f, 2f), 1.0);
                Pump(room, bob);

                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);
                Assert.IsTrue(Same(Turned(20f, 0.9f), cy.ViewOf(room.LocalId)));
                Assert.IsTrue(Same(Turned(-70f, 2f), cy.ViewOf(bob.LocalId)));
                Assert.IsNull(cy.ViewOf(cy.LocalId));
                Assert.IsNull(room.ViewOf(cy.LocalId));
                room.Dispose();
            }
        }

        [Test]
        public void AViewIsPassedOnAtMostTenTimesASecondAndNeverWhenNothingChanged()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);

                ViewPose first = Turned(30f);
                bob.ShareView(first, 0.0);
                Pump(room, bob);
                Assert.IsTrue(Same(first, room.ViewOf(bob.LocalId)));

                // Too soon after the last one: left out, and sent as soon as the time is up.
                ViewPose second = Turned(60f);
                bob.ShareView(second, 0.05);
                Pump(room, bob);
                Assert.IsTrue(Same(first, room.ViewOf(bob.LocalId)));
                bob.ShareView(second, NetRoom.ViewInterval + 0.01);
                Pump(room, bob);
                Assert.IsTrue(Same(second, room.ViewOf(bob.LocalId)));

                // The same view again, or one that nobody could tell from it, is not sent.
                bob.ShareView(Turned(60.1f), 5.0);
                Pump(room, bob);
                Assert.IsTrue(Same(second, room.ViewOf(bob.LocalId)));
                Assert.IsTrue(Math.Abs(room.ViewOf(bob.LocalId).TurnY - second.TurnY) < 2e-5f, "the host still has the one that was sent");
                Assert.IsTrue(Math.Abs(room.ViewOf(bob.LocalId).TurnY - Turned(60.1f).TurnY) > 5e-4f);
                room.Dispose();
            }
        }

        [Test]
        public void OfManyViewsThatComeInAtOnceTheNewestIsPassedOn()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                INetTransport eve = network.Join("Eve");
                Pump(room, cy);
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, cy);
                Assert.AreEqual(3, room.Players.Count);

                // Two views arrive in one go: the second one wins, and it is the only one that goes on.
                SendRaw(eve, ViewText("\"zoom\":1.1,\"yaw\":10,\"pitch\":50"));
                SendRaw(eve, ViewText("\"zoom\":1.2,\"yaw\":20,\"pitch\":50"));
                Pump(room, cy);
                Assert.IsTrue(Same(ViewPose.Orbit(20f, 50f, 1.2f), cy.ViewOf(room.Players[2].Id)));

                // Another one right behind it waits for its turn, and is not lost.
                _now = 0.01;
                SendRaw(eve, ViewText("\"zoom\":1.3,\"yaw\":30,\"pitch\":50"));
                Pump(room, cy);
                Assert.IsTrue(Same(ViewPose.Orbit(20f, 50f, 1.2f), cy.ViewOf(room.Players[2].Id)));
                Assert.IsTrue(Same(ViewPose.Orbit(30f, 50f, 1.3f), room.ViewOf(room.Players[2].Id)), "the host has it already");
                _now = 0.2;
                Pump(room, cy);
                Assert.IsTrue(Same(ViewPose.Orbit(30f, 50f, 1.3f), cy.ViewOf(room.Players[2].Id)));
                room.Dispose();
            }
        }

        [Test]
        public void AViewThatIsNoGoodOrComesFromNobodyIsIgnored()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                INetTransport eve = network.Join("Eve");
                Pump(room, cy);

                // Eve has not asked to join yet.
                SendRaw(eve, ViewText("\"zoom\":1.1,\"yaw\":10,\"pitch\":50"));
                Pump(room, cy);
                Assert.AreEqual(2, room.Players.Count);
                Assert.IsNull(room.ViewOf(eve.LocalId));
                Assert.IsNull(cy.ViewOf(eve.LocalId));

                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, cy);
                Assert.AreEqual(3, room.Players.Count);
                SendRaw(eve, ViewText("\"yaw\":10"));
                SendRaw(eve, ViewText("\"zoom\":\"near\""));
                SendRaw(eve, "{\"t\":\"view\",\"zoom\":[1]}");
                Pump(room, cy);
                Assert.IsNull(room.ViewOf(eve.LocalId));
                Assert.IsNull(cy.ViewOf(eve.LocalId));

                // Whatever Eve says about whose view it is, it is hers.
                SendRaw(eve, ViewText("\"from\":\"" + room.LocalId + "\",\"zoom\":3,\"yaw\":10,\"pitch\":50"));
                Pump(room, cy);
                Assert.IsNull(room.ViewOf(room.LocalId));
                Assert.IsTrue(Same(ViewPose.Orbit(10f, 50f, 3f), room.ViewOf(eve.LocalId)));
                Assert.IsNull(cy.ViewOf(room.LocalId));
                room.Dispose();
            }
        }

        [Test]
        public void APlayerWhoLeavesTakesTheViewAway()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                NetClientRoom cy = new NetClientRoom(host.Mod, network.Join("Cy"));
                Pump(room, bob, cy);
                ulong bobId = bob.LocalId;
                bob.ShareView(Turned(40f), 1.0);
                Pump(room, bob, cy);
                Assert.IsNotNull(cy.ViewOf(bobId));

                bob.Dispose();
                Pump(room, cy);
                Assert.IsNull(room.ViewOf(bobId));
                Assert.IsNull(cy.ViewOf(bobId));
                Assert.AreEqual(2, room.Players.Count);
                room.Dispose();
            }
        }

        [Test]
        public void AGameWithOneSharedTableHasNoViewsToShare()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("gomoku"));
                LoopbackNetwork network = new LoopbackNetwork();
                NetHostRoom room = new NetHostRoom(host, network.Host("Ann"));
                Assert.IsTrue(host.StartNewGame());
                NetClientRoom bob = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(room, bob);
                Assert.IsFalse(room.SharesViews);
                Assert.IsFalse(bob.SharesViews);

                bob.ShareView(Turned(40f), 1.0);
                room.ShareView(Turned(40f), 1.0);
                Pump(room, bob);
                Assert.IsNull(room.ViewOf(bob.LocalId));
                Assert.IsNull(bob.ViewOf(room.LocalId));

                // Even a guest that sends one anyway is not heard.
                INetTransport eve = network.Join("Eve");
                SendRaw(eve, Hello(host, "Eve"));
                Pump(room, bob);
                SendRaw(eve, ViewText("\"zoom\":1.1,\"yaw\":10,\"pitch\":50"));
                Pump(room, bob);
                Assert.IsNull(room.ViewOf(eve.LocalId));
                Assert.IsNull(bob.ViewOf(eve.LocalId));
                room.Dispose();
            }
        }

        [Test]
        public void AGuestSharesViewsOnlyWithAHostThatPassesThemOn()
        {
            using (GameHost host = Host())
            {
                Assert.IsTrue(host.SelectMod("cubic3"));
                LoopbackNetwork network = new LoopbackNetwork();

                // A host of an older version: nothing but the plain welcome, and what it does not know it does not answer.
                INetTransport older = network.Host("Ann");
                NetClientRoom guest = new NetClientRoom(host.Mod, network.Join("Bob"));
                Pump(guest);
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"proto\":1,\"you\":\"" + guest.LocalId + "\"}"));
                Pump(guest);
                Assert.IsTrue(guest.Joined);
                Assert.IsFalse(guest.SharesViews);
                while (older.Poll(out NetEvent ignored))
                {
                }
                guest.ShareView(Turned(40f), 1.0);
                bool sent = false;
                while (older.Poll(out NetEvent e))
                    sent |= e.Kind == NetEventKind.Data && Encoding.UTF8.GetString(e.Data).Contains("\"view\"");
                Assert.IsFalse(sent, "nothing is sent to a host that would not know what to do with it");

                // A host that says it does.
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"welcome\",\"proto\":1,\"you\":\"" + guest.LocalId + "\",\"view\":true}"));
                Pump(guest);
                Assert.IsTrue(guest.SharesViews);
                guest.ShareView(Turned(40f), 2.0);
                while (older.Poll(out NetEvent e))
                    sent |= e.Kind == NetEventKind.Data && Encoding.UTF8.GetString(e.Data).Contains("\"view\"");
                Assert.IsTrue(sent);

                // The host's views are the ones of the others; one that names the guest itself is not taken.
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"view\",\"from\":\"" + guest.LocalId + "\",\"zoom\":2,\"yaw\":0,\"pitch\":0}"));
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"view\",\"from\":\"" + older.LocalId + "\",\"zoom\":2,\"yaw\":0,\"pitch\":0}"));
                older.Send(guest.LocalId, Encoding.UTF8.GetBytes("{\"t\":\"view\",\"from\":\"not a number\",\"zoom\":2,\"yaw\":0,\"pitch\":0}"));
                Pump(guest);
                Assert.IsNull(guest.ViewOf(guest.LocalId));
                Assert.IsNotNull(guest.ViewOf(older.LocalId));
                Assert.AreEqual(2f, guest.ViewOf(older.LocalId).Zoom);
                guest.Dispose();
            }
        }
    }
}
