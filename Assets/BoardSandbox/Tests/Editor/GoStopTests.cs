using System.Collections.Generic;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // Go-Stop (Mods/gostop). The rules themselves are checked by the mod's own selftest.luau on positions built by hand;
    // here the mod is driven the way the app drives it.
    [TestFixture]
    public class GoStopTests
    {
        private static GameSession Start(string options = null)
        {
            GameSession session = new GameSession(TestSupport.LoadMod("gostop"));
            session.NewGame(options == null ? null : TestSupport.Options(options));
            return session;
        }

        private static int CountKind(ViewModel view, string kind)
        {
            int count = 0;
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Kind == kind)
                    count++;
            }
            return count;
        }

        private static ViewObject FindButton(ViewModel view, string label)
        {
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Kind == "button" && piece.Props["visible"].AsBool(true) && piece.Props["label"].AsString() == label)
                    return piece;
            }
            return null;
        }

        // Plays the command the computer player of the mod gives until the game ends.
        private static void Finish(GameSession session, int effort, int seed)
        {
            for (int step = 0; step < 400 && !session.View.IsOver; step++)
            {
                int seat = session.View.Turn;
                JsonValue command = effort == 0 ? session.RandomCommand(seat, seed + step) : TestSupport.Think(session, seat, effort, seed + step);
                Assert.IsNotNull(command, "no command at step " + step);
                SubmitResult result = session.Submit(new InputPacket(seat, command, "test"));
                Assert.IsTrue(result.IsAccepted, "step " + step + ": " + command.ToJson() + " -> " + result);
            }
            Assert.IsTrue(session.View.IsOver, "the game did not end");
        }

        [Test]
        public void TheRulesPassTheirOwnChecks()
        {
            ModPackage mod = TestSupport.LoadMod("gostop");
            SessionOptions options = new SessionOptions();
            options.Vm.CallTimeoutMs = 60000;
            using (GameSession session = new GameSession(mod, options))
            {
                session.NewGame();
                JsonValue failures = session.Query("selftest");
                Assert.AreEqual("[]", failures.Count == 0 ? "[]" : failures.ToJson());
            }
        }

        [Test]
        public void AGameStartsByChoosingTwoOrThreePlayers()
        {
            using (GameSession session = Start())
            {
                Assert.AreEqual("choose", session.Query("state")["phase"].AsString());
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreEqual(0, CountKind(session.View, "card"));
                Assert.IsNotNull(FindButton(session.View, "2인 맞고 (7점)"));
                ViewObject three = FindButton(session.View, "3인 고스톱 (3점)");
                Assert.IsNotNull(three);

                Assert.AreEqual("NOT_STARTED", session.Submit(InputPacket.Action(1, "go")).Code);
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(InputPacket.Action(1, "players").With("count", 4)).Code);
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "click").With("id", three.Id)).IsAccepted);

                JsonValue state = session.Query("state");
                Assert.AreEqual(3, state["players"].AsInt());
                Assert.AreEqual(3, state["limit"].AsInt());
                Assert.AreEqual(50, CountKind(session.View, "card"));
                if (state["phase"].AsString() == "play")
                {
                    Assert.AreEqual(7, state["seats"][0]["hand"].AsInt());
                    Assert.AreEqual(1, session.View.Turn);
                }
                Assert.AreEqual("ALREADY_STARTED", session.Submit(InputPacket.Action(1, "players").With("count", 2)).Code);
            }
        }

        [Test]
        public void TheOptionSkipsTheChoiceAndTwoPlayersStopFromSeven()
        {
            using (GameSession session = Start("{\"players\":2,\"bonus\":0}"))
            {
                JsonValue state = session.Query("state");
                Assert.AreEqual(2, state["players"].AsInt());
                Assert.AreEqual(7, state["limit"].AsInt());
                Assert.AreEqual(48, CountKind(session.View, "card"));
                if (state["phase"].AsString() == "play")
                {
                    Assert.AreEqual(10, state["seats"][0]["hand"].AsInt());
                    Assert.AreEqual(10, state["seats"][1]["hand"].AsInt());
                    Assert.AreEqual(20, state["stock"].AsInt());
                }
            }
        }

        [Test]
        public void OnlyTheHandOfTheViewerIsShownAndCardsUseTheAtlas()
        {
            using (GameSession session = Start("{\"players\":2}"))
            {
                JsonValue back = null;
                int faceDown = 0;
                int total = 0;
                foreach (ViewObject piece in session.View.Objects)
                {
                    if (piece.Kind != "card")
                        continue;
                    total++;
                    JsonValue rect = piece.Props["image_rect"];
                    Assert.AreEqual(4, rect.Count);
                    // The back is the first cell of the last row of the atlas.
                    if (rect[1].AsDouble() > 0.85 && rect[0].AsDouble() < 0.01)
                    {
                        faceDown++;
                        back = rect;
                    }
                }
                Assert.AreEqual(50, total);
                Assert.IsNotNull(back);
                JsonValue state = session.Query("state");
                if (state["phase"].AsString() == "play")
                    Assert.AreEqual(state["stock"].AsInt() + state["seats"][1]["hand"].AsInt(), faceDown, "the stock and the other hand are face down");
            }
        }

        [Test]
        public void ClicksPlayCardsAndWholeGamesEnd()
        {
            for (int seed = 1; seed <= 6; seed++)
            {
                using (GameSession session = Start(seed % 2 == 0 ? "{\"players\":2}" : "{\"players\":3}"))
                {
                    if (!session.View.IsOver)
                    {
                        // The first move is a click on a card of the hand.
                        JsonValue hand = session.Query("hand");
                        Assert.Greater(hand.Count, 0);
                        string snapshot = session.CaptureSnapshot();
                        JsonValue cardObjects = JsonValue.Parse(snapshot)["mod"]["card_obj"];
                        int objectId = cardObjects[hand[0].AsInt() - 1].AsInt();
                        SubmitResult click = session.Submit(InputPacket.Action(1, "click").With("id", objectId));
                        Assert.IsTrue(click.IsAccepted, click.ToString());
                    }
                    Finish(session, 0, seed * 100);
                    JsonValue result = session.Query("state")["result"];
                    Assert.IsTrue(result.IsObject);
                    Assert.AreEqual(result["winner"].AsInt(), session.View.Over.Winner);
                }
            }
        }

        [Test]
        public void UndoGoesBackAndTheGameGoesOn()
        {
            using (GameSession session = Start("{\"players\":2}"))
            {
                if (session.View.IsOver)
                    return;
                for (int step = 0; step < 6 && !session.View.IsOver; step++)
                    Assert.IsTrue(session.Submit(new InputPacket(session.View.Turn, session.RandomCommand(session.View.Turn, step + 1), "test")).IsAccepted);
                string before = session.Query("state").ToJson();
                if (session.View.IsOver)
                    return;
                Assert.IsTrue(session.Submit(new InputPacket(session.View.Turn, session.RandomCommand(session.View.Turn, 99), "test")).IsAccepted);
                Assert.IsTrue(session.Undo());
                Assert.AreEqual(before, session.Query("state").ToJson());
                Finish(session, 0, 500);
            }
        }

        [Test]
        public void TheComputerPlayersFinishGamesAtEveryLevel()
        {
            foreach (int effort in new[] { 1, 60 })
            {
                using (GameSession session = Start("{\"players\":2}"))
                    Finish(session, effort, 7);
                using (GameSession session = Start("{\"players\":3}"))
                    Finish(session, effort, 11);
            }
        }

        [Test]
        public void TheComputerPlayerChoosesThePlayersWhenAsked()
        {
            using (GameSession session = Start())
            {
                JsonValue command = TestSupport.Think(session, 1, 1);
                Assert.AreEqual("players", command["action"].AsString());
                Assert.AreEqual(2, command["count"].AsInt());
            }
        }

        [Test]
        public void HeadlessSimulationsFinish()
        {
            ModPackage mod = TestSupport.LoadMod("gostop");
            SimulationResult result = SimulationRunner.Run(mod, new SimulationOptions { Games = 200, Seed = 3 });
            Assert.AreEqual(200, result.Games);
            Assert.AreEqual(0, result.Rejected);
            Assert.AreEqual(0, result.Aborted);
        }

        [Test]
        public void TheCreditsNameTheArtworkAndItsLicense()
        {
            using (GameSession session = Start())
            {
                JsonValue credits = session.Query("credits");
                StringAssert.Contains("Marcus Richert", credits["artwork"].AsString());
                Assert.AreEqual("https://commons.wikimedia.org/wiki/File:Hwatu_overview.svg", credits["source"].AsString());
                Assert.AreEqual("https://creativecommons.org/licenses/by-sa/4.0/", credits["license_url"].AsString());
                StringAssert.Contains("creativecommons.org/licenses/by-sa/4.0", session.Mod.Manifest.Description);
            }
        }
    }
}
