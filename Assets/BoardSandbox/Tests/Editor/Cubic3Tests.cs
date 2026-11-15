using System;
using System.Collections.Generic;
using System.IO;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class Cubic3Tests
    {
        private const string Solved = "UUUUUUUUURRRRRRRRRFFFFFFFFFDDDDDDDDDLLLLLLLLLBBBBBBBBB";

        private static GameSession Start(string options = "{\"scramble\":25,\"seed\":7}")
        {
            GameSession session = new GameSession(ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "cubic3")));
            session.NewGame(JsonValue.Parse(options));
            return session;
        }

        private static SubmitResult Alg(GameSession session, string moves)
        {
            return session.Submit(InputPacket.Action(1, "alg").With("moves", moves));
        }

        private static string Facelets(GameSession session)
        {
            return session.Query("state")["facelets"].AsString();
        }

        private static string Inverse(string scramble)
        {
            List<string> moves = new List<string>(scramble.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            moves.Reverse();
            for (int i = 0; i < moves.Count; i++)
            {
                string move = moves[i];
                moves[i] = move.EndsWith("2") ? move : move.EndsWith("'") ? move.Substring(0, 1) : move + "'";
            }
            return string.Join(" ", moves);
        }

        // The colors a view would show, worked out from the drawn objects alone.
        private static string CellsFromView(GameSession session)
        {
            JsonValue cubies = session.Query("cubies")["cubies"];
            Dictionary<int, JsonValue> homes = new Dictionary<int, JsonValue>();
            foreach (JsonValue cubie in cubies.Items)
                homes[cubie["id"].AsInt()] = cubie;
            Assert.AreEqual(26, homes.Count);
            foreach (ViewObject drawn in session.View.Objects)
            {
                JsonValue model = homes[drawn.Id];
                Assert.IsTrue(model["pos"].StructurallyEquals(drawn.Props["pos"]), "object " + drawn.Id + " is drawn somewhere else");
                Assert.IsTrue(model["rot"].StructurallyEquals(drawn.Props["rot"]), "object " + drawn.Id + " is drawn turned differently");
            }
            return session.Query("cubies")["cells"].AsString();
        }

        // The same cube in the same position: the colors named after the middles and the colors as they really are.
        private static void AssertSameCube(GameSession expected, GameSession actual, string message = null)
        {
            Assert.AreEqual(Facelets(expected), Facelets(actual), message);
            Assert.AreEqual(CellsFromView(expected), CellsFromView(actual), message);
        }

        [Test]
        public void TheGameStartsScrambledWithTwentySixCubies()
        {
            using (GameSession session = Start())
            {
                Assert.AreEqual(26, session.View.ObjectCount);
                Assert.AreEqual(1, session.View.Turn);
                Assert.AreNotEqual(Solved, Facelets(session));
                JsonValue stage = session.View.GetCustom("stage").Raw;
                Assert.IsFalse(stage["board"]["visible"].AsBool(true));
                Assert.IsFalse(stage["table"]["visible"].AsBool(true));
                Assert.AreEqual("orbit", stage["camera"]["left_drag"].AsString());

                int stickers = 0;
                foreach (ViewObject cubie in session.View.Objects)
                {
                    Assert.AreEqual("cubie", cubie.Kind);
                    Assert.AreEqual(3, cubie.Props["pos"].Count);
                    Assert.AreEqual(9, cubie.Props["rot"].Count);
                    stickers += cubie.Props["faces"].Count;
                }
                Assert.AreEqual(54, stickers);
                CellsFromView(session);
            }
        }

        [Test]
        public void UndoingTheScrambleSolvesTheCube()
        {
            using (GameSession session = Start())
            {
                string scramble = session.Query("state")["scramble"].AsString();
                Assert.AreEqual(25, scramble.Split(' ').Length);
                SubmitResult result = Alg(session, Inverse(scramble));
                Assert.IsTrue(result.IsAccepted, result.ToString());
                Assert.AreEqual(Solved, Facelets(session));
                Assert.IsTrue(session.View.IsOver);
                Assert.AreEqual(1, session.View.Over.Winner);
                Assert.AreEqual(0, session.View.Over.Cells.Count);
                Assert.AreEqual("GAME_OVER", Alg(session, "R").Code);

                foreach (ViewObject cubie in session.View.Objects)
                    Assert.IsTrue(cubie.Props["rot"].StructurallyEquals(JsonValue.Parse("[1,0,0,0,1,0,0,0,1]")));
            }
        }

        [Test]
        public void TheCubiesAgreeWithTheStickerTables()
        {
            // M, E and S turn like L, D and F. A middle layer carries centers away, and every color is named after the
            // face that has it in the middle now, so the stickers that stay behind take another name.
            Dictionary<string, string> byTheBook = new Dictionary<string, string>
            {
                { "U", "UUUUUUUUUBBBRRRRRRRRRFFFFFFDDDDDDDDDFFFLLLLLLLLLBBBBBB" },
                { "R", "UUFUUFUUFRRRRRRRRRFFDFFDFFDDDBDDBDDBLLLLLLLLLUBBUBBUBB" },
                { "F", "UUUUUULLLURRURRURRFFFFFFFFFRRRDDDDDDLLDLLDLLDBBBBBBBBB" },
                { "M", "FUFFUFFUFRRRRRRRRRDFDDFDDFDBDBBDBBDBLLLLLLLLLUBUUBUUBU" },
                { "E", "UUUUUUUUUBBBRRRBBBRRRFFFRRRDDDDDDDDDFFFLLLFFFLLLBBBLLL" },
                { "S", "RRRUUURRRDRDDRDDRDFFFFFFFFFLLLDDDLLLULUULUULUBBBBBBBBB" }
            };
            foreach (KeyValuePair<string, string> pair in byTheBook)
            {
                using (GameSession session = Start("{\"scramble\":1,\"seed\":3}"))
                {
                    string undo = Inverse(session.Query("state")["scramble"].AsString());
                    // One command, so that passing the solved state on the way does not end the game.
                    Assert.IsTrue(Alg(session, undo + " " + pair.Key).IsAccepted);
                    Assert.AreEqual(pair.Value, Facelets(session), pair.Key);
                    CellsFromView(session);
                }
            }
        }

        [Test]
        public void ThreeLayersTurnedTogetherTurnTheWholeCube()
        {
            // The three layers of an axis that turn the same way turn the cube as a whole: it stays solved and only
            // its faces change places. M follows L, E follows D and S follows F, and these turns would not add up
            // to a rotation if any of the three went the wrong way.
            string[][] rotations =
            {
                new[] { "R M' L'", "333333333222222222444444444666666666555555555111111111" },
                new[] { "U E' D'", "111111111666666666222222222444444444333333333555555555" },
                new[] { "F S B'", "555555555111111111333333333222222222444444444666666666" },
                new[] { "R' M L", "666666666222222222111111111333333333555555555444444444" },
                new[] { "U' E D", "111111111333333333555555555444444444666666666222222222" },
                new[] { "F' S' B", "222222222444444444333333333555555555111111111666666666" }
            };
            foreach (string[] rotation in rotations)
            {
                using (GameSession session = Start("{\"scramble\":1,\"seed\":3}"))
                {
                    string undo = Inverse(session.Query("state")["scramble"].AsString());
                    Assert.IsTrue(Alg(session, undo + " " + rotation[0]).IsAccepted, rotation[0]);
                    Assert.IsTrue(session.View.IsOver, rotation[0]);
                    Assert.AreEqual(Solved, Facelets(session), rotation[0]);
                    Assert.AreEqual(rotation[1], CellsFromView(session), rotation[0]);
                }
            }

            foreach (string wrong in new[] { "R M L'", "U E D'", "F S' B'", "R M' L" })
            {
                using (GameSession session = Start("{\"scramble\":1,\"seed\":3}"))
                {
                    string undo = Inverse(session.Query("state")["scramble"].AsString());
                    Assert.IsTrue(Alg(session, undo + " " + wrong).IsAccepted, wrong);
                    Assert.IsFalse(session.View.IsOver, wrong);
                }
            }
        }

        // Two ways to write down the same turns end in the same cube.
        private static void AssertEquivalent(string one, string other)
        {
            using (GameSession a = Start())
            using (GameSession b = Start())
            {
                Assert.IsTrue(Alg(a, one).IsAccepted, one);
                Assert.IsTrue(Alg(b, other).IsAccepted, other);
                AssertSameCube(a, b, one + " against " + other);
            }
        }

        [Test]
        public void AMiddleLayerTurnsAndTurnsBackLikeAFace()
        {
            foreach (string slice in new[] { "M", "E", "S" })
            {
                string back = slice + "'";
                string half = slice + "2";

                // Turns that add up to nothing leave the cube where it was.
                foreach (string nothing in new[]
                {
                    slice + " " + slice + " " + slice + " " + slice, slice + " " + back, back + " " + slice, half + " " + half,
                    half + " " + slice + " " + slice, back + " " + back + " " + half
                })
                {
                    using (GameSession session = Start())
                    {
                        string start = CellsFromView(session);
                        Assert.IsTrue(Alg(session, nothing).IsAccepted, nothing);
                        Assert.AreEqual(start, CellsFromView(session), nothing);
                    }
                }

                // Turns that are the same turn written differently.
                AssertEquivalent(half, slice + " " + slice);
                AssertEquivalent(half + "'", half);
                AssertEquivalent(back, slice + " " + slice + " " + slice);
                AssertEquivalent(back, half + " " + slice);
                AssertEquivalent(slice, half + " " + back);

                // And every one of them turns something, each in its own way.
                using (GameSession session = Start())
                {
                    HashSet<string> seen = new HashSet<string> { CellsFromView(session) };
                    foreach (string turn in new[] { slice, half, back })
                    {
                        using (GameSession other = Start())
                        {
                            Assert.IsTrue(Alg(other, turn).IsAccepted, turn);
                            Assert.IsTrue(seen.Add(CellsFromView(other)), turn);
                        }
                    }
                }
            }
        }

        [Test]
        public void OnlyFacesAndMiddleLayersAreMoves()
        {
            using (GameSession session = Start())
            {
                string before = Facelets(session);
                string beforeCells = CellsFromView(session);
                foreach (string bad in new[] { "m", "e", "s", "x", "M3", "Mw", "MM", "M''", "M'2'", "M0", "R X", "R M3", "R U Z'" })
                {
                    Assert.AreEqual("BAD_MOVE", Alg(session, bad).Code, bad);
                    Assert.AreEqual("BAD_MOVE", session.Submit(InputPacket.Action(1, "move").With("move", bad)).Code, bad);
                }
                Assert.AreEqual(before, Facelets(session));
                Assert.AreEqual(beforeCells, CellsFromView(session));

                foreach (string good in new[] { "M", "M'", "M2", "M2'", "M'2", "E", "E'", "E2", "S", "S'", "S2" })
                    Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("move", good)).IsAccepted, good);
            }
        }

        [Test]
        public void RandomTurnsKeepTheModelsInStep()
        {
            using (GameSession session = Start("{\"scramble\":40,\"seed\":11}"))
            using (GameSession replay = Start("{\"scramble\":40,\"seed\":11}"))
            {
                Random random = new Random(5);
                string[] names = { "U", "R", "F", "D", "L", "B", "M", "E", "S" };
                string[] suffixes = { "", "'", "2" };
                List<string> played = new List<string>();
                for (int i = 0; i < 150 && !session.View.IsOver; i++)
                {
                    string move = names[random.Next(names.Length)] + suffixes[random.Next(3)];
                    Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("move", move)).IsAccepted);
                    played.Add(move);
                    string cells = CellsFromView(session);
                    Assert.AreEqual(54, cells.Length);
                    foreach (char color in "123456")
                        Assert.AreEqual(9, cells.Split(color).Length - 1);
                }
                Assert.Greater(played.Count, 100);

                // The same turns sent as one command give the same cube.
                Assert.IsTrue(Alg(replay, string.Join(" ", played)).IsAccepted);
                AssertSameCube(session, replay);

                // The same turns through the sticker permutations of the model give the same cube.
                string before = Facelets(session);
                string beforeCells = CellsFromView(session);
                Assert.IsTrue(Alg(session, "R U R' U' R U R' U' R U R' U' R U R' U' R U R' U' R U R' U'").IsAccepted);
                Assert.AreEqual(before, Facelets(session));
                Assert.IsTrue(Alg(session, "R2 L2 U2 D2 F2 B2 R2 L2 U2 D2 F2 B2").IsAccepted);
                Assert.AreEqual(before, Facelets(session));
                Assert.IsTrue(Alg(session, "M M M M E E E E S S S S M2 M2 E2 E2 S2 S2").IsAccepted);
                Assert.AreEqual(before, Facelets(session));
                Assert.AreEqual(beforeCells, CellsFromView(session));
            }
        }

        [Test]
        public void EveryTurnIsShownAsAnEffectBeforeItsCubiesMove()
        {
            using (GameSession session = Start())
            {
                SubmitResult result = Alg(session, "R U2 F'");
                Assert.IsTrue(result.IsAccepted);
                List<ViewEvent> effects = new List<ViewEvent>();
                int sets = 0;
                foreach (ViewEvent e in result.Events)
                {
                    if (e.Kind == ViewEventKind.Effect)
                    {
                        Assert.AreEqual(effects.Count * 9, sets);
                        effects.Add(e);
                    }
                    else if (e.Kind == ViewEventKind.Set)
                    {
                        sets++;
                    }
                }
                Assert.AreEqual(3, effects.Count);
                Assert.AreEqual(27, sets);
                Assert.AreEqual("rotate", effects[0].Raw["kind"].AsString());
                Assert.AreEqual(9, effects[0].Raw["ids"].Count);
                Assert.IsTrue(effects[0].Raw["axis"].StructurallyEquals(JsonValue.Parse("[1,0,0]")));
                Assert.AreEqual(-90, effects[0].Raw["degrees"].AsInt());
                Assert.AreEqual(-180, effects[1].Raw["degrees"].AsInt());
                Assert.IsTrue(effects[1].Raw["axis"].StructurallyEquals(JsonValue.Parse("[0,1,0]")));
                Assert.AreEqual(90, effects[2].Raw["degrees"].AsInt());
                Assert.Greater(effects[1].Seconds, effects[0].Seconds);

                PlaybackQueue queue = new PlaybackQueue();
                queue.Reset(session.View);
                session.EventApplied += queue.Enqueue;
                Assert.IsTrue(Alg(session, "L D").IsAccepted);
                Assert.IsTrue(queue.IsBusy);
                for (int i = 0; i < 200 && queue.IsBusy; i++)
                    queue.Update(0.05);
                Assert.IsFalse(queue.IsBusy);
                foreach (ViewObject shown in queue.Display.Objects)
                    Assert.IsTrue(shown.Props.StructurallyEquals(session.View.Get(shown.Id).Props));
            }
        }

        [Test]
        public void ATurnOfAMiddleLayerIsShownAsAnEffectOfEightCubies()
        {
            using (GameSession session = Start())
            {
                // M follows L, E follows D and S follows F: the layers turn around the axes of those faces.
                SubmitResult result = Alg(session, "M E' S2");
                Assert.IsTrue(result.IsAccepted);
                List<ViewEvent> effects = new List<ViewEvent>();
                int sets = 0;
                foreach (ViewEvent e in result.Events)
                {
                    if (e.Kind == ViewEventKind.Effect)
                    {
                        Assert.AreEqual(effects.Count * 8, sets);
                        effects.Add(e);
                    }
                    else if (e.Kind == ViewEventKind.Set)
                    {
                        sets++;
                    }
                }
                Assert.AreEqual(3, effects.Count);
                Assert.AreEqual(24, sets);
                string[] axes = { "[-1,0,0]", "[0,-1,0]", "[0,0,1]" };
                int[] degrees = { -90, 90, -180 };
                for (int i = 0; i < 3; i++)
                {
                    Assert.AreEqual("rotate", effects[i].Raw["kind"].AsString());
                    Assert.AreEqual(8, effects[i].Raw["ids"].Count);
                    Assert.IsTrue(effects[i].Raw["axis"].StructurallyEquals(JsonValue.Parse(axes[i])), axes[i]);
                    Assert.AreEqual(degrees[i], effects[i].Raw["degrees"].AsInt());
                    HashSet<int> ids = new HashSet<int>();
                    foreach (JsonValue id in effects[i].Raw["ids"].Items)
                        ids.Add(id.AsInt());
                    Assert.AreEqual(8, ids.Count);
                }

                PlaybackQueue queue = new PlaybackQueue();
                queue.Reset(session.View);
                session.EventApplied += queue.Enqueue;
                Assert.IsTrue(Alg(session, "S M'").IsAccepted);
                Assert.IsTrue(queue.IsBusy);
                for (int i = 0; i < 200 && queue.IsBusy; i++)
                    queue.Update(0.05);
                Assert.IsFalse(queue.IsBusy);
                foreach (ViewObject shown in queue.Display.Objects)
                    Assert.IsTrue(shown.Props.StructurallyEquals(session.View.Get(shown.Id).Props));
            }
        }

        private static ViewObject CubieAt(GameSession session, int x, int y, int z)
        {
            JsonValue wanted = JsonValue.NewArray().Add(x).Add(y).Add(z);
            foreach (ViewObject cubie in session.View.Objects)
            {
                if (cubie.Props["pos"].StructurallyEquals(wanted))
                    return cubie;
            }
            throw new InvalidOperationException("no cubie there");
        }

        private static InputPacket Touch(string action, int id, string normal, string drag = null, int button = 0)
        {
            JsonValue command = JsonValue.NewObject().Set("action", action).Set("id", id).Set("normal", JsonValue.Parse(normal)).Set("button", button);
            if (drag != null)
                command.Set("drag", JsonValue.Parse(drag));
            return new InputPacket(1, command);
        }

        [Test]
        public void AClickTurnsTheTouchedFace()
        {
            using (GameSession a = Start())
            using (GameSession b = Start())
            {
                Assert.AreEqual(Facelets(a), Facelets(b));
                Assert.IsTrue(a.Submit(Touch("turn_face", CubieAt(a, 1, 1, 1).Id, "[0,1,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "U").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                Assert.IsTrue(a.Submit(Touch("turn_face", CubieAt(a, 1, 0, 1).Id, "[0.02,0,0.999]", null, 1)).IsAccepted);
                Assert.IsTrue(Alg(b, "F'").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                Assert.AreEqual("NO_FACE", a.Submit(Touch("turn_face", 999, "[0,1,0]")).Code);
                Assert.AreEqual("NO_FACE", a.Submit(Touch("turn_face", CubieAt(a, 1, 1, 1).Id, "[0.7,0.7,0]")).Code);
                Assert.AreEqual("NO_FACE", a.Submit(new InputPacket(1, JsonValue.NewObject().Set("action", "turn_face"))).Code);
                Assert.AreEqual(Facelets(b), Facelets(a));
            }
        }

        [Test]
        public void ADragTurnsTheLayerTheStickerMovesIn()
        {
            using (GameSession a = Start())
            using (GameSession b = Start())
            {
                // The top sticker of the right layer pulled towards the viewer: the right face turns counter-clockwise.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 0).Id, "[0,1,0]", "[0,0,1]")).IsAccepted);
                Assert.IsTrue(Alg(b, "R'").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                // Pushed away instead: clockwise.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 0).Id, "[0,1,0]", "[0.1,0,-0.99]")).IsAccepted);
                Assert.IsTrue(Alg(b, "R").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                // A front sticker of the top layer pulled to the left: the top face turns clockwise.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 0, 1, 1).Id, "[0,0,1]", "[-1,0,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "U").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                // A front sticker of the left layer pulled down: the left face turns clockwise.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, -1, 0, 1).Id, "[0,0,1]", "[0,-1,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "L").IsAccepted);
                Assert.AreEqual(Facelets(b), Facelets(a));

                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 1).Id, "[0,0,1]", "[0,0,1]")).Code);
                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 1).Id, "[0,0,1]")).Code);
                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 0, 0, 1).Id, "[0,0,1]", "[0,0,1]")).Code);
                AssertSameCube(b, a);
            }
        }

        [Test]
        public void ADragTurnsAMiddleLayerToo()
        {
            using (GameSession a = Start())
            using (GameSession b = Start())
            {
                // The middle row, between the top and the bottom: a front sticker pulled to the right moves the way
                // the bottom face moves its front row, which is E, and pulled to the left the other way round. A left
                // sticker pulled towards the viewer goes the same way as the front one pulled to the right.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 0, 0, 1).Id, "[0,0,1]", "[1,0,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "E").IsAccepted);
                AssertSameCube(b, a, "E");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 1, 0, 1).Id, "[0,0,1]", "[-1,0,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "E'").IsAccepted);
                AssertSameCube(b, a, "E'");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, -1, 0, 0).Id, "[-1,0,0]", "[0,0,1]")).IsAccepted);
                Assert.IsTrue(Alg(b, "E").IsAccepted);
                AssertSameCube(b, a, "E from the left");

                // The middle column, between the left and the right face, turns like the left face: the top sticker
                // pulled towards the viewer is M, the front sticker pulled up is the other way, and so is a back
                // sticker pulled down.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 0, 1, 0).Id, "[0,1,0]", "[0,0,1]")).IsAccepted);
                Assert.IsTrue(Alg(b, "M").IsAccepted);
                AssertSameCube(b, a, "M");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 0, 1, 1).Id, "[0,0,1]", "[0,1,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "M'").IsAccepted);
                AssertSameCube(b, a, "M'");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 0, -1, -1).Id, "[0,0,-1]", "[0,-1,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "M'").IsAccepted);
                AssertSameCube(b, a, "M' from behind");

                // The middle slab, between the front and the back face, turns like the front face: the top sticker
                // pulled to the right is S, the right sticker pulled up the other way, and a bottom sticker pulled to
                // the left goes with the top one pulled to the right.
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 0).Id, "[0,1,0]", "[1,0,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "S").IsAccepted);
                AssertSameCube(b, a, "S");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, 1, 0, 0).Id, "[1,0,0]", "[0,1,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "S'").IsAccepted);
                AssertSameCube(b, a, "S'");
                Assert.IsTrue(a.Submit(Touch("turn_drag", CubieAt(a, -1, -1, 0).Id, "[0,-1,0]", "[-1,0,0]")).IsAccepted);
                Assert.IsTrue(Alg(b, "S").IsAccepted);
                AssertSameCube(b, a, "S from below");
            }
        }

        [Test]
        public void UndoAndSnapshotsBringTheCubiesBack()
        {
            using (GameSession session = Start())
            {
                string start = Facelets(session);
                string startCells = CellsFromView(session);
                Assert.IsTrue(Alg(session, "R M U E F S").IsAccepted);
                string later = Facelets(session);
                string laterCells = CellsFromView(session);
                string snapshot = session.CaptureSnapshot();
                Assert.IsTrue(session.Undo());
                Assert.AreEqual(start, Facelets(session));
                Assert.AreEqual(startCells, CellsFromView(session));
                Assert.IsFalse(session.View.GetCustom("stage").Raw["board"]["visible"].AsBool(true));
                Assert.IsTrue(session.Redo());
                Assert.AreEqual(later, Facelets(session));
                CellsFromView(session);

                Assert.IsTrue(Alg(session, "D2 M2").IsAccepted);
                session.LoadSnapshot(snapshot);
                Assert.AreEqual(later, Facelets(session));
                Assert.AreEqual(laterCells, CellsFromView(session));
                Assert.AreEqual(6, session.Query("state")["moves"].AsInt());
                Assert.IsTrue(Alg(session, "S' F' E' U' M' R'").IsAccepted);
                Assert.AreEqual(start, Facelets(session));
                Assert.AreEqual(startCells, CellsFromView(session));
            }
        }

        [Test]
        public void BrokenSnapshotsAreRefused()
        {
            using (GameSession session = Start())
            {
                string before = Facelets(session);
                JsonValue snapshot = JsonValue.Parse(session.CaptureSnapshot());
                JsonValue game = null;
                foreach (KeyValuePair<string, JsonValue> member in snapshot.Members)
                {
                    if (member.Value.IsObject && member.Value["cubies"].IsArray)
                        game = member.Value;
                }
                Assert.IsNotNull(game, "the snapshot has no part with the cubies in it");
                JsonValue first = game["cubies"][0];
                JsonValue second = game["cubies"][1];

                JsonValue original = first["pos"].DeepClone();
                first.Set("pos", second["pos"].DeepClone());
                Assert.Throws<GameSessionException>(() => session.LoadSnapshot(snapshot.ToJson()));
                first.Set("pos", original);

                JsonValue rot = first["rot"].DeepClone();
                first.Set("rot", JsonValue.Parse("[1,0,0,0,1,0,0,0,-1]"));
                Assert.Throws<GameSessionException>(() => session.LoadSnapshot(snapshot.ToJson()));
                first.Set("rot", rot);

                session.LoadSnapshot(snapshot.ToJson());
                Assert.AreEqual(before, Facelets(session));
            }
        }

        [Test]
        public void TheConfigDescribesHowTheCubeIsPlayed()
        {
            ModManifest manifest = ModLoader.LoadDirectory(Path.Combine(TestSupport.ModsRoot, "cubic3")).Manifest;
            Assert.AreEqual("object", manifest.Client["click"]["target"].AsString());
            Assert.AreEqual("turn_drag", manifest.Client["drag"]["action"].AsString());
            Assert.AreEqual(18, manifest.Client["keys"].Count);
            foreach (string letter in new[] { "u", "r", "f", "d", "l", "b", "m", "e", "s" })
            {
                Assert.AreEqual(letter.ToUpperInvariant(), manifest.Client["keys"][letter]["move"].AsString(), letter);
                Assert.AreEqual(letter.ToUpperInvariant() + "'", manifest.Client["keys"]["shift+" + letter]["move"].AsString(), letter);
            }
            Assert.AreEqual("box", manifest.Client["pieces"]["cubie"]["shape"].AsString());
            using (GameSession session = Start())
            {
                foreach (KeyValuePair<string, JsonValue> key in manifest.Client["keys"].Members)
                    Assert.IsTrue(session.Submit(new InputPacket(1, key.Value.DeepClone())).IsAccepted || session.View.IsOver, key.Key);
                Assert.AreEqual(27, session.LegalCommands(1).Count);
                HashSet<string> legal = new HashSet<string>();
                foreach (JsonValue command in session.LegalCommands(1))
                    legal.Add(command["move"].AsString());
                Assert.AreEqual(27, legal.Count);
                foreach (string name in new[] { "M", "M'", "M2", "E", "E'", "E2", "S", "S'", "S2" })
                    Assert.IsTrue(legal.Contains(name), name);
                Assert.IsNotNull(session.RandomCommand(1, 4));
                Assert.AreEqual(12, session.Query("net")["rows"].Count);
            }
        }
    }
}
