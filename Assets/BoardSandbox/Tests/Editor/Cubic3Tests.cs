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
            Dictionary<string, string> byTheBook = new Dictionary<string, string>
            {
                { "U", "UUUUUUUUUBBBRRRRRRRRRFFFFFFDDDDDDDDDFFFLLLLLLLLLBBBBBB" },
                { "R", "UUFUUFUUFRRRRRRRRRFFDFFDFFDDDBDDBDDBLLLLLLLLLUBBUBBUBB" },
                { "F", "UUUUUULLLURRURRURRFFFFFFFFFRRRDDDDDDLLDLLDLLDBBBBBBBBB" }
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
        public void RandomTurnsKeepTheModelsInStep()
        {
            using (GameSession session = Start("{\"scramble\":40,\"seed\":11}"))
            {
                Random random = new Random(5);
                string[] names = { "U", "R", "F", "D", "L", "B" };
                string[] suffixes = { "", "'", "2" };
                List<string> played = new List<string>();
                for (int i = 0; i < 120 && !session.View.IsOver; i++)
                {
                    string move = names[random.Next(6)] + suffixes[random.Next(3)];
                    Assert.IsTrue(session.Submit(InputPacket.Action(1, "move").With("move", move)).IsAccepted);
                    played.Add(move);
                    string cells = CellsFromView(session);
                    Assert.AreEqual(54, cells.Length);
                    foreach (char color in "123456")
                        Assert.AreEqual(9, cells.Split(color).Length - 1);
                }
                Assert.Greater(played.Count, 50);

                // The same turns through the sticker permutations of the model give the same cube.
                string before = Facelets(session);
                Assert.IsTrue(Alg(session, "R U R' U' R U R' U' R U R' U' R U R' U' R U R' U' R U R' U'").IsAccepted);
                Assert.AreEqual(before, Facelets(session));
                Assert.IsTrue(Alg(session, "R2 L2 U2 D2 F2 B2 R2 L2 U2 D2 F2 B2").IsAccepted);
                Assert.AreEqual(before, Facelets(session));
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

                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 0, 0, 1).Id, "[0,0,1]", "[1,0,0]")).Code);
                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 1).Id, "[0,0,1]", "[0,0,1]")).Code);
                Assert.AreEqual("NO_TURN", a.Submit(Touch("turn_drag", CubieAt(a, 1, 1, 1).Id, "[0,0,1]")).Code);
                Assert.AreEqual(Facelets(b), Facelets(a));
            }
        }

        [Test]
        public void UndoAndSnapshotsBringTheCubiesBack()
        {
            using (GameSession session = Start())
            {
                string start = Facelets(session);
                string startCells = CellsFromView(session);
                Assert.IsTrue(Alg(session, "R U F").IsAccepted);
                string later = Facelets(session);
                string snapshot = session.CaptureSnapshot();
                Assert.IsTrue(session.Undo());
                Assert.AreEqual(start, Facelets(session));
                Assert.AreEqual(startCells, CellsFromView(session));
                Assert.IsFalse(session.View.GetCustom("stage").Raw["board"]["visible"].AsBool(true));
                Assert.IsTrue(session.Redo());
                Assert.AreEqual(later, Facelets(session));
                CellsFromView(session);

                Assert.IsTrue(Alg(session, "D2").IsAccepted);
                session.LoadSnapshot(snapshot);
                Assert.AreEqual(later, Facelets(session));
                Assert.AreEqual(3, session.Query("state")["moves"].AsInt());
                CellsFromView(session);
                Assert.IsTrue(Alg(session, "F' U' R'").IsAccepted);
                Assert.AreEqual(start, Facelets(session));
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
            Assert.AreEqual(12, manifest.Client["keys"].Count);
            Assert.AreEqual("box", manifest.Client["pieces"]["cubie"]["shape"].AsString());
            using (GameSession session = Start())
            {
                foreach (KeyValuePair<string, JsonValue> key in manifest.Client["keys"].Members)
                    Assert.IsTrue(session.Submit(new InputPacket(1, key.Value.DeepClone())).IsAccepted || session.View.IsOver, key.Key);
                Assert.AreEqual(18, session.LegalCommands(1).Count);
                Assert.IsNotNull(session.RandomCommand(1, 4));
                Assert.AreEqual(12, session.Query("net")["rows"].Count);
            }
        }
    }
}
