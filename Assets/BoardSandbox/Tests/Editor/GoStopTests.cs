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

        // The button that the player of a seat sees with this label (seat 0: someone who plays no seat), or null.
        private static ViewObject FindButton(ViewModel view, int seat, string label)
        {
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Kind != "button")
                    continue;
                JsonValue seen = piece.PropsFor(seat);
                if (seen["visible"].AsBool(true) && seen["label"].AsString() == label)
                    return piece;
            }
            return null;
        }

        // The buttons that the player of a seat sees. A button that is not shown cannot be clicked, and one that is shown can.
        private static List<ViewObject> ShownButtons(ViewModel view, int seat)
        {
            List<ViewObject> shown = new List<ViewObject>();
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Kind != "button")
                    continue;
                JsonValue seen = piece.PropsFor(seat);
                bool visible = seen["visible"].AsBool(true);
                Assert.AreEqual(visible, seen["pick"].AsBool(true), "a button is clickable exactly when it is shown (seat " + seat + ")");
                if (visible)
                    shown.Add(piece);
            }
            return shown;
        }

        private static string Sorted(params string[] labels)
        {
            List<string> list = new List<string>(labels);
            list.Sort(string.CompareOrdinal);
            return string.Join(" | ", list);
        }

        // The labels of the buttons that the player of a seat sees, sorted and joined (see Sorted).
        private static string Buttons(ViewModel view, int seat)
        {
            List<string> labels = new List<string>();
            foreach (ViewObject piece in ShownButtons(view, seat))
                labels.Add(piece.PropsFor(seat)["label"].AsString());
            return Sorted(labels.ToArray());
        }

        // Of the screens of seats 0 to 3 (0 plays no seat, 3 plays none in a game of two), only the one of the player on turn shows the buttons.
        private static void AssertOnlyTurnSeesButtons(ViewModel view, int turn, params string[] expected)
        {
            for (int seat = 0; seat <= 3; seat++)
                Assert.AreEqual(seat == turn ? Sorted(expected) : string.Empty, Buttons(view, seat), "the buttons that seat " + seat + " sees, turn " + turn);
        }

        private static void AssertNoButtons(ViewModel view)
        {
            for (int seat = 0; seat <= 3; seat++)
                Assert.AreEqual(string.Empty, Buttons(view, seat), "the buttons that seat " + seat + " sees");
        }

        private static void AssertNobodySees(ViewModel view, params string[] labels)
        {
            for (int seat = 0; seat <= 3; seat++)
            {
                foreach (string label in labels)
                    Assert.IsNull(FindButton(view, seat, label), "seat " + seat + " sees " + label);
            }
        }

        // Plays random commands for whoever is on turn until the game is in the phase ("gostop", say); false when it ends first.
        private static bool PlayUntilPhase(GameSession session, string phase, int seed)
        {
            for (int step = 0; step < 400 && !session.View.IsOver; step++)
            {
                if (session.Query("state")["phase"].AsString() == phase)
                    return true;
                int seat = session.View.Turn;
                JsonValue command = session.RandomCommand(seat, seed + step);
                Assert.IsNotNull(command, "no command at step " + step);
                SubmitResult result = session.Submit(new InputPacket(seat, command, "test"));
                Assert.IsTrue(result.IsAccepted, "step " + step + ": " + command.ToJson() + " -> " + result);
            }
            return false;
        }

        // A game in which the player on turn has reached the score where go or stop is chosen. Random play gets there in most games
        // of two players and in more than half of those of three.
        private static GameSession StartAtGoOrStop(int players)
        {
            for (int attempt = 1; attempt <= 60; attempt++)
            {
                GameSession session = Start("{\"players\":" + players + "}");
                if (PlayUntilPhase(session, "gostop", attempt * 1000))
                    return session;
                session.Dispose();
            }
            Assert.Fail("no game of " + players + " players got to go or stop");
            return null;
        }

        // A game of two players in which the first player has clicked a card that asks something first, and the first of the two
        // buttons of the question is "흔들기" (three cards of a month, none on the floor) or "폭탄" (three in the hand, one on the floor).
        // About one deal in six has the first, one in twenty-five the second.
        private static GameSession StartAtQuestion(string firstButton)
        {
            for (int attempt = 0; attempt < 800; attempt++)
            {
                GameSession session = Start("{\"players\":2}");
                int seat = session.View.Turn;
                if (!session.View.IsOver && session.Query("state")["phase"].AsString() == "play")
                {
                    JsonValue hand = session.Query("hand");
                    int[] count = new int[13];
                    for (int i = 0; i < hand.Count; i++)
                    {
                        int id = hand[i].AsInt();
                        if (id <= 48)
                            count[(id - 1) / 4 + 1]++;
                    }
                    int card = 0;
                    for (int i = 0; i < hand.Count && card == 0; i++)
                    {
                        int id = hand[i].AsInt();
                        if (id <= 48 && count[(id - 1) / 4 + 1] >= 3)
                            card = id;
                    }
                    if (card != 0)
                    {
                        // Clicking a card asks first only where a bomb or a shake is possible; otherwise the card is played.
                        int objectId = JsonValue.Parse(session.CaptureSnapshot())["mod"]["card_obj"][card - 1].AsInt();
                        Assert.IsTrue(session.Submit(InputPacket.Action(seat, "click").With("id", objectId)).IsAccepted);
                        if (FindButton(session.View, seat, firstButton) != null)
                            return session;
                    }
                }
                session.Dispose();
            }
            Assert.Fail("no deal asked " + firstButton);
            return null;
        }

        // What the player on turn may be shown while a card is to be played: nothing, flipping only, or the question of a shake or a bomb.
        private static readonly HashSet<string> InPlay = new HashSet<string>
        {
            string.Empty, Sorted("뒤집기만"), Sorted("흔들기", "그냥 내기"), Sorted("폭탄", "한 장만"),
        };

        // Whatever the game is in, the buttons are on the screen of the player on turn and on no other, and they are the ones of the phase.
        private static void AssertButtonsAreForThePlayerOnTurn(GameSession session, string where)
        {
            int seat = session.View.Turn;
            string phase = session.Query("state")["phase"].AsString();
            string at = where + ", phase " + phase + ", turn " + seat;
            for (int viewer = 0; viewer <= 3; viewer++)
            {
                if (viewer != seat)
                    Assert.AreEqual(string.Empty, Buttons(session.View, viewer), at + ": the buttons that seat " + viewer + " sees");
            }
            string mine = Buttons(session.View, seat);
            if (phase == "gostop")
                Assert.AreEqual(Sorted("고", "스톱"), mine, at);
            else if (phase == "pick" || phase == "over")
                Assert.AreEqual(string.Empty, mine, at);
            else
                Assert.IsTrue(InPlay.Contains(mine), at + ": the buttons of the player on turn are " + mine);
        }

        // Plays a whole game the way a person does, with clicks: on a card of the hand (which asks first where a bomb or a shake is
        // possible), on the buttons that are shown (go or stop, the answer to such a question, flipping only), and now and then with
        // a command of the mod. At every step the buttons are on the screen of the player who has to answer and on no other.
        private static void PlayByClicks(GameSession session, int seed)
        {
            System.Random random = new System.Random(seed);
            for (int step = 0; step < 600 && !session.View.IsOver; step++)
            {
                int seat = session.View.Turn;
                string at = "step " + step + ", turn " + seat;
                AssertButtonsAreForThePlayerOnTurn(session, "step " + step);

                SubmitResult result;
                List<ViewObject> shown = ShownButtons(session.View, seat);
                if (shown.Count > 0 && random.Next(4) != 0)
                {
                    ViewObject button = shown[random.Next(shown.Count)];
                    result = session.Submit(InputPacket.Action(seat, "click").With("id", button.Id));
                }
                else
                {
                    JsonValue command = session.RandomCommand(seat, seed * 1000 + step);
                    Assert.IsNotNull(command, at + ": no command");
                    string action = command["action"].AsString();
                    JsonValue cardObject = (action == "play" || action == "pick") && random.Next(2) == 0
                        ? JsonValue.Parse(session.CaptureSnapshot())["mod"]["card_obj"][command["card"].AsInt() - 1]
                        : JsonValue.Null;
                    result = cardObject.IsNull
                        ? session.Submit(new InputPacket(seat, command, "test"))
                        : session.Submit(InputPacket.Action(seat, "click").With("id", cardObject.AsInt()));
                }
                Assert.IsTrue(result.IsAccepted, at + ": " + result);
            }
            Assert.IsTrue(session.View.IsOver, "the game did not end");
            AssertNoButtons(session.View);
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
                // The first player chooses, so that player alone is shown the two buttons.
                AssertOnlyTurnSeesButtons(session.View, 1, "2인 맞고 (7점)", "3인 고스톱 (3점)");
                ViewObject three = FindButton(session.View, 1, "3인 고스톱 (3점)");
                Assert.IsNotNull(three);

                Assert.AreEqual("NOT_STARTED", session.Submit(InputPacket.Action(1, "go")).Code);
                Assert.AreEqual("INVALID_ARGUMENT", session.Submit(InputPacket.Action(1, "players").With("count", 4)).Code);
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(2, "click").With("id", three.Id)).Code, "the second player cannot choose");
                Assert.IsTrue(session.Submit(InputPacket.Action(1, "click").With("id", three.Id)).IsAccepted);
                AssertNoButtons(session.View);

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

        // The back is the first cell of the last row of the atlas.
        private static bool IsBack(JsonValue props)
        {
            JsonValue rect = props["image_rect"];
            Assert.AreEqual(4, rect.Count);
            return rect[1].AsDouble() > 0.85 && rect[0].AsDouble() < 0.01;
        }

        private static int CountFaceDown(ViewModel view, int seat)
        {
            int count = 0;
            foreach (ViewObject piece in view.Objects)
            {
                if (piece.Kind == "card" && IsBack(piece.PropsFor(seat)))
                    count++;
            }
            return count;
        }

        [Test]
        public void EveryPlayerSeesTheOwnHandAtTheBottomAndTheOthersFaceDown()
        {
            using (GameSession session = Start("{\"players\":2}"))
            {
                JsonValue state = session.Query("state");
                if (state["phase"].AsString() != "play")
                    return;
                int stock = state["stock"].AsInt();
                int hand1 = state["seats"][0]["hand"].AsInt();
                int hand2 = state["seats"][1]["hand"].AsInt();
                Assert.AreEqual(stock + hand1 + hand2, CountFaceDown(session.View, 0), "someone without a seat sees no hand");
                Assert.AreEqual(stock + hand2, CountFaceDown(session.View, 1));
                Assert.AreEqual(stock + hand1, CountFaceDown(session.View, 2));
                Assert.AreEqual(stock + hand1 + hand2, CountFaceDown(session.View, 3), "a seat that does not play sees what a spectator sees");

                // The cards a player sees face up are the own hand, in the row nearest to that player.
                foreach (int seat in new[] { 1, 2 })
                {
                    int near = 0;
                    foreach (ViewObject piece in session.View.Objects)
                    {
                        if (piece.Kind != "card")
                            continue;
                        JsonValue seen = piece.PropsFor(seat);
                        if (seen["pos"][2].AsDouble() > 6 && !IsBack(seen))
                            near++;
                    }
                    Assert.AreEqual(seat == 1 ? hand1 : hand2, near, "seat " + seat);
                }

                // The lines of the players read "나" for the player who looks.
                int mine = 0;
                foreach (ViewObject piece in session.View.Objects)
                {
                    if (piece.Kind == "text" && piece.PropsFor(2)["label"].AsString().Contains("나"))
                    {
                        mine++;
                        Assert.AreEqual(2, piece.Owner);
                        Assert.Greater(piece.PropsFor(2)["pos"][2].AsDouble(), 0, "the own line is at the bottom");
                        StringAssert.Contains("Player 2", piece.Props["label"].AsString());
                    }
                }
                Assert.AreEqual(1, mine);
            }
        }

        [Test]
        public void WhatASeatSeesFollowsLaterChanges()
        {
            using (GameSession session = Start("{\"players\":2,\"open_hands\":true}"))
            {
                JsonValue state = session.Query("state");
                if (state["phase"].AsString() != "play")
                    return;
                Assert.AreEqual(state["stock"].AsInt(), CountFaceDown(session.View, 0), "open hands show every hand to everyone");
                int before = CountFaceDown(session.View, 2);
                Assert.IsTrue(session.Submit(new InputPacket(1, session.RandomCommand(1, 5), "test")).IsAccepted);
                JsonValue after = session.Query("state");
                if (after["phase"].AsString() == "play" && after["stock"].AsInt() != state["stock"].AsInt())
                    Assert.AreEqual(after["stock"].AsInt(), CountFaceDown(session.View, 2), "was " + before);
            }
        }

        [Test]
        public void GoAndStopAreShownOnlyToThePlayerWhoDecides()
        {
            foreach (int players in new[] { 2, 3 })
            {
                using (GameSession session = StartAtGoOrStop(players))
                {
                    JsonValue state = session.Query("state");
                    Assert.AreEqual("gostop", state["phase"].AsString());
                    int decider = state["turn"].AsInt();
                    Assert.AreEqual(decider, session.View.Turn);
                    AssertOnlyTurnSeesButtons(session.View, decider, "고", "스톱");
                }
            }
        }

        [Test]
        public void OnlyThePlayerWhoDecidesCanGoOrStop()
        {
            foreach (int players in new[] { 2, 3 })
            {
                using (GameSession session = StartAtGoOrStop(players))
                {
                    int decider = session.View.Turn;
                    ViewObject go = FindButton(session.View, decider, "고");
                    ViewObject stop = FindButton(session.View, decider, "스톱");
                    Assert.IsNotNull(go);
                    Assert.IsNotNull(stop);
                    string before = session.Query("state").ToJson();
                    for (int seat = 1; seat <= players; seat++)
                    {
                        if (seat == decider)
                            continue;
                        string who = "seat " + seat + " of " + players + ", the decider is " + decider;
                        Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(seat, "click").With("id", go.Id)).Code, who);
                        Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(seat, "click").With("id", stop.Id)).Code, who);
                        Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(seat, "go")).Code, who);
                        Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(seat, "stop")).Code, who);
                    }
                    Assert.AreEqual(before, session.Query("state").ToJson(), "the refusals changed nothing");
                    AssertOnlyTurnSeesButtons(session.View, decider, "고", "스톱");
                }
            }
        }

        [Test]
        public void AfterGoOrStopNobodySeesTheButtons()
        {
            using (GameSession session = StartAtGoOrStop(2))
            {
                int decider = session.View.Turn;
                int goes = session.Query("state")["seats"][decider - 1]["go"].AsInt();
                ViewObject go = FindButton(session.View, decider, "고");
                Assert.IsNotNull(go);
                SubmitResult result = session.Submit(InputPacket.Action(decider, "click").With("id", go.Id));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.AreEqual(goes + 1, session.Query("state")["seats"][decider - 1]["go"].AsInt());
                AssertNobodySees(session.View, "고", "스톱");
            }
            using (GameSession session = StartAtGoOrStop(3))
            {
                int decider = session.View.Turn;
                ViewObject stop = FindButton(session.View, decider, "스톱");
                Assert.IsNotNull(stop);
                SubmitResult result = session.Submit(InputPacket.Action(decider, "click").With("id", stop.Id));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.IsTrue(session.View.IsOver);
                AssertNoButtons(session.View);
            }
        }

        [Test]
        public void UndoAfterGoOrStopWasAskedGivesTheButtonsToTheRightPlayer()
        {
            using (GameSession session = StartAtGoOrStop(2))
            {
                int decider = session.View.Turn;
                AssertOnlyTurnSeesButtons(session.View, decider, "고", "스톱");
                Assert.IsTrue(session.Undo());
                AssertNobodySees(session.View, "고", "스톱");
                AssertButtonsAreForThePlayerOnTurn(session, "after the undo");
                Assert.AreNotEqual("gostop", session.Query("state")["phase"].AsString());
                // The game goes on from there, with the buttons where they belong.
                for (int step = 0; step < 400 && !session.View.IsOver; step++)
                {
                    int seat = session.View.Turn;
                    Assert.IsTrue(session.Submit(new InputPacket(seat, session.RandomCommand(seat, 900 + step), "test")).IsAccepted);
                    if (!session.View.IsOver)
                        AssertButtonsAreForThePlayerOnTurn(session, "step " + step + " after the undo");
                }
                Assert.IsTrue(session.View.IsOver);
            }
        }

        [Test]
        public void TheQuestionOfAShakeIsAskedToThePlayerOfTheCardAlone()
        {
            using (GameSession session = StartAtQuestion("흔들기"))
            {
                int seat = session.View.Turn;
                int other = seat == 1 ? 2 : 1;
                AssertOnlyTurnSeesButtons(session.View, seat, "흔들기", "그냥 내기");
                ViewObject shake = FindButton(session.View, seat, "흔들기");
                ViewObject plain = FindButton(session.View, seat, "그냥 내기");
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(other, "click").With("id", shake.Id)).Code);
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(other, "click").With("id", plain.Id)).Code);
                Assert.AreEqual(0, session.Query("state")["seats"][seat - 1]["shake"].AsInt());
                AssertOnlyTurnSeesButtons(session.View, seat, "흔들기", "그냥 내기");

                SubmitResult result = session.Submit(InputPacket.Action(seat, "click").With("id", shake.Id));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.AreEqual(1, session.Query("state")["seats"][seat - 1]["shake"].AsInt());
                AssertNobodySees(session.View, "흔들기", "그냥 내기");
            }
        }

        [Test]
        public void TheQuestionOfABombIsAskedToThePlayerOfTheCardAloneAndSoIsTheFlip()
        {
            using (GameSession session = StartAtQuestion("폭탄"))
            {
                int seat = session.View.Turn;
                int other = seat == 1 ? 2 : 1;
                AssertOnlyTurnSeesButtons(session.View, seat, "폭탄", "한 장만");
                ViewObject bomb = FindButton(session.View, seat, "폭탄");
                ViewObject single = FindButton(session.View, seat, "한 장만");
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(other, "click").With("id", bomb.Id)).Code);
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(other, "click").With("id", single.Id)).Code);
                Assert.AreEqual(0, session.Query("state")["seats"][seat - 1]["pass"].AsInt());
                AssertOnlyTurnSeesButtons(session.View, seat, "폭탄", "한 장만");

                SubmitResult result = session.Submit(InputPacket.Action(seat, "click").With("id", bomb.Id));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.AreEqual(2, session.Query("state")["seats"][seat - 1]["pass"].AsInt(), "a bomb leaves two turns to flip only");
                AssertNobodySees(session.View, "폭탄", "한 장만");

                // The other player moves, and then the one with the bomb is on turn again and may flip without playing a card.
                // Nobody sees the button of that before.
                for (int step = 0; step < 12 && !session.View.IsOver; step++)
                {
                    if (session.View.Turn == seat && session.Query("state")["phase"].AsString() == "play")
                        break;
                    AssertNobodySees(session.View, "뒤집기만");
                    int turn = session.View.Turn;
                    Assert.IsTrue(session.Submit(new InputPacket(turn, session.RandomCommand(turn, 70 + step), "test")).IsAccepted);
                }
                Assert.IsFalse(session.View.IsOver, "the game ended before the player with the bomb was on turn again");
                Assert.AreEqual(seat, session.View.Turn);
                AssertOnlyTurnSeesButtons(session.View, seat, "뒤집기만");

                ViewObject flip = FindButton(session.View, seat, "뒤집기만");
                Assert.AreEqual("NOT_YOUR_TURN", session.Submit(InputPacket.Action(other, "click").With("id", flip.Id)).Code);
                result = session.Submit(InputPacket.Action(seat, "click").With("id", flip.Id));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.AreEqual(1, session.Query("state")["seats"][seat - 1]["pass"].AsInt());
            }
        }

        [Test]
        public void WholeGamesPlayedWithClicksShowTheButtonsToThePlayerOnTurnAlone()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                using (GameSession session = Start(seed % 2 == 0 ? "{\"players\":2}" : "{\"players\":3}"))
                    PlayByClicks(session, seed);
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
