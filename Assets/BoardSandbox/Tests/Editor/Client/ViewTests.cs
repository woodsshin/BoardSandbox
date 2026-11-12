using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using NUnit.Framework;
using UnityEngine;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class ViewHintsTests
    {
        [Test]
        public void MissingHintsAreNeutral()
        {
            foreach (ViewHints hints in new[] { new ViewHints(null), new ViewHints(JsonValue.NewObject()) })
            {
                Assert.AreEqual("", hints.ClickAction);
                Assert.IsFalse(hints.CanClick);
                Assert.IsFalse(hints.ClickObjects);
                Assert.IsFalse(hints.CanDrag);
                Assert.AreEqual(0, hints.KeyCount);
                Assert.IsNull(hints.PreviewKind);
                Assert.IsNull(hints.KeyCommand("u", false));
                Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("stone"));
                Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("pawn"));
                Assert.AreEqual(PieceShape.Disc, hints.ShapeFor(null));
            }
        }

        [Test]
        public void EverySettingIsRead()
        {
            ViewHints hints = new ViewHints(JsonValue.Parse(
                "{\"click\":{\"action\":\"move\",\"preview\":\"pawn\",\"target\":\"Object\"},\"drag\":{\"action\":\"pull\"}," +
                "\"keys\":{\"U\":{\"action\":\"a\",\"n\":1},\"Shift + u\":{\"action\":\"b\"},\"x\":{\"n\":1},\"y\":5}," +
                "\"pieces\":{\"pawn\":{\"shape\":\"cube\"},\"king\":{\"shape\":\"Stone\"},\"coin\":{\"shape\":\"disc\"},\"die\":{\"shape\":\"box\"}}}"));
            Assert.AreEqual("move", hints.ClickAction);
            Assert.IsTrue(hints.CanClick);
            Assert.IsTrue(hints.ClickObjects);
            Assert.AreEqual("pull", hints.DragAction);
            Assert.IsTrue(hints.CanDrag);
            Assert.AreEqual("pawn", hints.PreviewKind);
            Assert.AreEqual(2, hints.KeyCount);
            Assert.AreEqual("a", hints.KeyCommand("U", false)["action"].AsString());
            Assert.AreEqual("b", hints.KeyCommand("u", true)["action"].AsString());
            Assert.IsNull(hints.KeyCommand("x", false));
            Assert.IsNull(hints.KeyCommand("y", false));
            Assert.IsNull(hints.KeyCommand("", false));
            Assert.AreEqual(PieceShape.Cube, hints.ShapeFor("pawn"));
            Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("king"));
            Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("coin"));
            Assert.AreEqual(PieceShape.Box, hints.ShapeFor("die"));
            Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("other"));
        }

        [Test]
        public void UnknownShapesFallBackToTheDefaultOfTheKind()
        {
            ViewHints hints = new ViewHints(JsonValue.Parse("{\"pieces\":{\"stone\":{\"shape\":\"banana\"},\"man\":{\"shape\":5}}}"));
            Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("stone"));
            Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("man"));
        }

        [Test]
        public void HintsThatAreNotAnObjectAreIgnored()
        {
            foreach (string json in new[] { "[1]", "5", "\"x\"", "null" })
                Assert.IsFalse(new ViewHints(JsonValue.Parse(json)).CanClick, json);
        }

        [Test]
        public void ClicksCanBeTurnedOffExplicitly()
        {
            Assert.IsFalse(new ViewHints(JsonValue.Parse("{\"click\":{\"action\":\"\"}}")).CanClick);
        }

        [Test]
        public void GomokuDeclaresItsHints()
        {
            ViewHints hints = new ViewHints(TestSupport.LoadGomoku().Manifest.Client);
            Assert.AreEqual("place_stone", hints.ClickAction);
            Assert.IsFalse(hints.ClickObjects);
            Assert.AreEqual("stone", hints.PreviewKind);
            Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("stone"));
            Assert.AreEqual(BoardStyle.Lines, StageSettings.Resolve(hints.Client, null).Board.Style);
        }
    }

    [TestFixture]
    public class ObjectLookTests
    {
        private static ModManifest Manifest()
        {
            return ModManifest.Parse("{\"id\":\"t\",\"name\":\"t\",\"api\":1,\"entry\":\"m.luau\",\"seats\":[{\"color\":\"#ff0000\"},{\"color\":\"#00ff00\"}]}");
        }

        private static ViewObject Piece(int owner, string props, string kind = "pawn")
        {
            return new ViewObject(1, kind, 3, 4, owner, JsonValue.Parse(props));
        }

        private static ObjectLook Look(string props, string client = "{}", int owner = 1, string kind = "pawn")
        {
            return ObjectLook.For(Piece(owner, props, kind), new ViewHints(JsonValue.Parse(client)), Manifest());
        }

        private static void AssertColor(Color32 color, int r, int g, int b)
        {
            Assert.AreEqual(r, (int)color.r);
            Assert.AreEqual(g, (int)color.g);
            Assert.AreEqual(b, (int)color.b);
            Assert.AreEqual(255, (int)color.a);
        }

        [Test]
        public void TheColorPropertyWins()
        {
            AssertColor(Look("{\"color\":\"#0000ff\"}", "{}", 2).Color, 0, 0, 255);
        }

        [Test]
        public void TheSeatColorIsTheFallback()
        {
            AssertColor(Look("{}").Color, 255, 0, 0);
            AssertColor(Look("{}", "{}", 2).Color, 0, 255, 0);
            AssertColor(Look("{\"color\":\"nonsense\"}", "{}", 2).Color, 0, 255, 0);
        }

        [Test]
        public void PiecesWithoutASeatAreNeutral()
        {
            AssertColor(Look("{}", "{}", 0).Color, 0x8a, 0x8a, 0x8a);
            AssertColor(Look("{}", "{}", 9).Color, 0x8a, 0x8a, 0x8a);
            AssertColor(ObjectLook.For(Piece(1, "{}"), new ViewHints(null), null).Color, 0x8a, 0x8a, 0x8a);
        }

        [Test]
        public void ColorNamesAreUnderstood()
        {
            Assert.IsTrue(Look("{\"color\":\"black\"}").Color.r < 64 && Look("{\"color\":\"white\"}").Color.r > 192);
        }

        [Test]
        public void TheScaleIsClamped()
        {
            Assert.AreEqual(1f, Look("{}").Size.x, 1e-6);
            Assert.AreEqual(1.5f, Look("{\"scale\":1.5}").Size.y, 1e-6);
            Assert.AreEqual(ObjectLook.MaxScale, Look("{\"scale\":10}").Size.z, 1e-6);
            Assert.AreEqual(ObjectLook.MinScale, Look("{\"scale\":0.01}").Size.x, 1e-6);
            Assert.AreEqual(ObjectLook.MinScale, Look("{\"scale\":-3}").Size.x, 1e-6);
            Assert.AreEqual(1f, Look("{\"scale\":\"big\"}").Size.x, 1e-6);
        }

        [Test]
        public void TheSizeIsANumberOrOnePerAxis()
        {
            GeometryAssert.Near(new Vector3(2f, 2f, 2f), Look("{\"size\":2}").Size, 1e-6f);
            GeometryAssert.Near(new Vector3(1f, 2f, 3f), Look("{\"size\":[1,2,3]}").Size, 1e-6f);
            GeometryAssert.Near(new Vector3(2f, 4f, 6f), Look("{\"size\":[1,2,3],\"scale\":2}").Size, 1e-6f);
            GeometryAssert.Near(new Vector3(ObjectLook.MinSize, ObjectLook.MaxSize, 1f), Look("{\"size\":[-1,1e9,1]}").Size, 1e-6f);
            GeometryAssert.Near(Vector3.one, Look("{\"size\":[1,2]}").Size, 1e-6f);
        }

        [Test]
        public void TheKindSuppliesWhatThePieceDoesNotSay()
        {
            string client = "{\"pieces\":{\"pawn\":{\"shape\":\"cube\",\"color\":\"#0000ff\",\"size\":2,\"gloss\":0.2,\"shadow\":false}}}";
            ObjectLook fromKind = Look("{}", client);
            Assert.AreEqual(PieceShape.Cube, fromKind.Shape);
            AssertColor(fromKind.Color, 0, 0, 255);
            Assert.AreEqual(2f, fromKind.Size.x, 1e-6);
            Assert.AreEqual(0.2f, fromKind.Gloss, 1e-6);
            Assert.IsFalse(fromKind.Shadow);

            ObjectLook own = Look("{\"shape\":\"sphere\",\"color\":\"#00ff00\",\"size\":3,\"shadow\":true}", client);
            Assert.AreEqual(PieceShape.Sphere, own.Shape);
            AssertColor(own.Color, 0, 255, 0);
            Assert.AreEqual(3f, own.Size.x, 1e-6);
            Assert.IsTrue(own.Shadow);
            Assert.AreEqual(PieceShape.Disc, Look("{}", client, 1, "rook").Shape);
        }

        [Test]
        public void PiecesOfTheBoardStandOnTheirFootAndCastAShade()
        {
            ObjectLook look = Look("{}");
            Assert.IsFalse(look.Free);
            Assert.IsFalse(look.Centered);
            Assert.IsTrue(look.Shadow);
            Assert.IsTrue(look.Visible);
            Assert.IsTrue(look.Pick);
            Assert.IsNull(look.Stickers);
            Assert.AreEqual(0f, look.Tween, 1e-6);
            Assert.IsFalse(look.Transparent);
        }

        [Test]
        public void APositionMakesAnObjectFreeAndFlipsZForTheScene()
        {
            ObjectLook look = Look("{\"pos\":[1,2,3],\"shape\":\"box\",\"tween\":0.5,\"pick\":false,\"visible\":false}");
            Assert.IsTrue(look.Free);
            Assert.IsTrue(look.Centered);
            Assert.IsFalse(look.Shadow);
            Assert.IsFalse(look.Pick);
            Assert.IsFalse(look.Visible);
            Assert.AreEqual(0.5f, look.Tween, 1e-6);
            GeometryAssert.Near(new Vector3(1f, 2f, -3f), look.Position, 1e-6f);
            GeometryAssert.Near(look.Position, look.Center, 1e-6f);

            ObjectLook standing = Look("{\"pos\":[1,2,3],\"shape\":\"box\",\"anchor\":\"bottom\",\"size\":2}");
            Assert.IsFalse(standing.Centered);
            GeometryAssert.Near(new Vector3(1f, 3f, -3f), standing.Center, 1e-6f);
            Assert.IsFalse(Look("{\"pos\":[1,2]}").Free);
            Assert.IsFalse(Look("{\"pos\":\"here\"}").Free);
        }

        [Test]
        public void ARotationTurnsTheObjectInTheScene()
        {
            // A quarter turn around y takes the x axis of a mod to its -z axis, which is +z in the scene.
            ObjectLook look = Look("{\"pos\":[0,0,0],\"rot\":[0,90,0]}");
            GeometryAssert.Near(new Vector3(0f, 0f, 1f), look.Rotation.Rotate(new Vector3(1f, 0f, 0f)), 1e-5f);
            ObjectLook matrix = Look("{\"pos\":[0,0,0],\"rot\":[0,0,1, 0,1,0, -1,0,0]}");
            GeometryAssert.Near(new Vector3(0f, 0f, 1f), matrix.Rotation.Rotate(new Vector3(1f, 0f, 0f)), 1e-5f);
            GeometryAssert.Near(new Vector3(1f, 0f, 0f), Look("{\"rot\":[1,2]}").Rotation.Rotate(new Vector3(1f, 0f, 0f)), 1e-6f);
        }

        [Test]
        public void StickersAreNamedBySideAndTheFrontLooksAtTheViewer()
        {
            ObjectLook look = Look("{\"shape\":\"box\",\"color\":\"#000000\",\"faces\":{\"px\":\"#ff0000\",\"pz\":\"#00ff00\",\"ny\":\"#0000ff\",\"nz\":\"junk\"}}");
            Assert.IsNotNull(look.Stickers);
            AssertColor(look.Stickers[0].Value, 255, 0, 0);
            Assert.IsNull(look.Stickers[1]);
            Assert.IsNull(look.Stickers[2]);
            AssertColor(look.Stickers[3].Value, 0, 0, 255);
            Assert.IsNull(look.Stickers[4]);
            AssertColor(look.Stickers[5].Value, 0, 255, 0);

            MeshBuilder mesh = look.BuildMesh();
            GeometryAssert.Oriented(mesh);
            bool found = false;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Color32 c = mesh.ColorList[i];
                if (c.g == 255 && c.r == 0)
                {
                    found = true;
                    GeometryAssert.Near(new Vector3(0f, 0f, -1f), mesh.NormalList[i], 1e-5f);
                    Assert.Less(mesh.VertexList[i].z, -0.5f);
                }
            }
            Assert.IsTrue(found);
            Assert.IsNull(Look("{\"shape\":\"sphere\",\"faces\":{\"px\":\"#ff0000\"}}").Stickers);
            Assert.IsNull(Look("{\"shape\":\"box\",\"faces\":{\"up\":\"#ff0000\"}}").Stickers);
        }

        [Test]
        public void ObjectsThatLookAlikeShareAMesh()
        {
            Assert.AreEqual(Look("{\"color\":\"#ff0000\"}").MeshKey, Look("{\"color\":\"#00ff00\",\"size\":2}").MeshKey);
            Assert.AreNotEqual(Look("{\"shape\":\"box\"}").MeshKey, Look("{\"shape\":\"cube\"}").MeshKey);
            Assert.AreNotEqual(Look("{\"shape\":\"box\"}").MeshKey, Look("{\"shape\":\"box\",\"pos\":[0,0,0]}").MeshKey);
            string faces = "{\"shape\":\"box\",\"faces\":{\"px\":\"#ff0000\"}}";
            Assert.AreEqual(Look(faces).MeshKey, Look(faces).MeshKey);
            Assert.AreNotEqual(Look(faces).MeshKey, Look("{\"shape\":\"box\",\"faces\":{\"nx\":\"#ff0000\"}}").MeshKey);
            Assert.AreNotEqual(Look(faces).MeshKey, Look("{\"shape\":\"box\",\"color\":\"#000000\",\"faces\":{\"px\":\"#ff0000\"}}").MeshKey);
        }

        [Test]
        public void AppearanceIgnoresWhereAnObjectIs()
        {
            ObjectLook a = Look("{\"pos\":[0,0,0],\"color\":\"#ff0000\"}");
            Assert.IsTrue(a.SameAppearance(Look("{\"pos\":[5,5,5],\"rot\":[0,90,0],\"size\":2,\"color\":\"#ff0000\"}")));
            Assert.IsFalse(a.SameAppearance(Look("{\"pos\":[0,0,0],\"color\":\"#ff0001\"}")));
            Assert.IsFalse(a.SameAppearance(Look("{\"pos\":[0,0,0],\"color\":\"#ff0000\",\"gloss\":0.1}")));
            Assert.IsFalse(a.SameAppearance(Look("{\"pos\":[0,0,0],\"color\":\"#ff0000\",\"visible\":false}")));
            Assert.IsFalse(a.SameAppearance(Look("{\"pos\":[0,0,0],\"color\":\"#ff0000\",\"shape\":\"box\"}")));
            Assert.IsFalse(a.SameAppearance(null));
        }

        [Test]
        public void AlphaMakesAnObjectSeeThrough()
        {
            ObjectLook look = Look("{\"color\":\"#ff0000\",\"alpha\":0.5}");
            Assert.IsTrue(look.Transparent);
            Assert.AreEqual(128, (int)look.Color.a);
            Assert.IsTrue(Look("{\"color\":\"#ff000080\"}").Transparent);
        }

        [Test]
        public void SeatColorsComeFromTheManifest()
        {
            AssertColor(ObjectLook.SeatColor(Manifest(), 1), 255, 0, 0);
            AssertColor(ObjectLook.SeatColor(Manifest(), 2), 0, 255, 0);
            AssertColor(ObjectLook.SeatColor(Manifest(), 3), 0x8a, 0x8a, 0x8a);
            AssertColor(ObjectLook.SeatColor(null, 1), 0x8a, 0x8a, 0x8a);
        }
    }

    [TestFixture]
    public class BoardStateTests
    {
        private static ViewEvent Event(string json)
        {
            return ViewEvent.Parse(JsonValue.Parse(json));
        }

        private static ViewModel Model(params string[] events)
        {
            ViewModel model = new ViewModel();
            foreach (string json in events)
                model.Apply(Event(json));
            return model;
        }

        [Test]
        public void ABoardEventDecidesTheSize()
        {
            ViewModel model = Model("{\"t\":\"board\",\"columns\":10,\"rows\":6}", "{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":3,\"y\":4}");
            BoardState state = BoardState.Read(model, StageSettings.Parse(JsonValue.Parse("{\"board\":{\"columns\":5,\"rows\":5}}")));
            Assert.AreEqual(10, state.Columns);
            Assert.AreEqual(6, state.Rows);
        }

        [Test]
        public void HintsDecideWhenThereIsNoBoardEvent()
        {
            BoardState state = BoardState.Read(Model(), StageSettings.Parse(JsonValue.Parse("{\"board\":{\"columns\":5,\"rows\":4}}")));
            Assert.AreEqual(5, state.Columns);
            Assert.AreEqual(4, state.Rows);
        }

        [Test]
        public void TheBoardGrowsToHoldItsPiecesWhenNothingSaysHowBigItIs()
        {
            Assert.AreEqual(BoardState.DefaultSize, BoardState.Read(Model(), new StageSettings()).Columns);
            Assert.AreEqual(BoardState.DefaultSize, BoardState.Read(Model(), new StageSettings()).Rows);

            ViewModel model = Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":12,\"y\":3}", "{\"t\":\"spawn\",\"id\":2,\"kind\":\"a\",\"x\":2.5,\"y\":9.5}");
            BoardState state = BoardState.Read(model, new StageSettings());
            Assert.AreEqual(12, state.Columns);
            Assert.AreEqual(10, state.Rows);
        }

        [Test]
        public void TheSizeIsLimited()
        {
            BoardState big = BoardState.Read(Model("{\"t\":\"board\",\"columns\":1000,\"rows\":65}"), new StageSettings());
            Assert.AreEqual(BoardLayout.MaxSize, big.Columns);
            Assert.AreEqual(BoardLayout.MaxSize, big.Rows);

            BoardState far = BoardState.Read(Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":1e300,\"y\":-1e300}"), new StageSettings());
            Assert.AreEqual(BoardLayout.MaxSize, far.Columns);
            Assert.AreEqual(BoardState.DefaultSize, far.Rows);
        }

        [Test]
        public void PiecesAreFoundByTheirNearestCell()
        {
            ViewModel model = Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":3,\"y\":4}", "{\"t\":\"spawn\",\"id\":2,\"kind\":\"a\",\"x\":5.4,\"y\":6.6}");
            BoardState state = BoardState.Read(model, new StageSettings());
            Assert.AreEqual(1, state.ObjectAt(3, 4).Id);
            Assert.AreEqual(2, state.ObjectAt(5, 7).Id);
            Assert.IsTrue(state.IsOccupied(3, 4));
            Assert.IsFalse(state.IsOccupied(4, 3));
            Assert.IsNull(state.ObjectAt(1, 1));
        }

        [Test]
        public void FreeObjectsAreNotOnTheBoard()
        {
            ViewModel model = Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":30,\"y\":2,\"props\":{\"pos\":[0,0,0]}}");
            BoardState state = BoardState.Read(model, new StageSettings());
            Assert.AreEqual(BoardState.DefaultSize, state.Columns);
            Assert.IsFalse(state.IsOccupied(30, 2));
        }

        [Test]
        public void TheNewerPieceWinsASharedCell()
        {
            ViewModel model = Model("{\"t\":\"spawn\",\"id\":7,\"kind\":\"a\",\"x\":2,\"y\":2}", "{\"t\":\"spawn\",\"id\":3,\"kind\":\"a\",\"x\":2,\"y\":2}");
            Assert.AreEqual(7, BoardState.Read(model, new StageSettings()).ObjectAt(2, 2).Id);
        }

        [TestCase(2.5, 3)]
        [TestCase(-0.5, -1)]
        [TestCase(2.4, 2)]
        [TestCase(1e300, 1000000)]
        [TestCase(-1e300, -1000000)]
        public void CellsRoundHalvesAwayFromZeroAndStayInRange(double coordinate, int expected)
        {
            Assert.AreEqual(expected, BoardState.Cell(coordinate));
        }

        [Test]
        public void TheLastMoveIsReadFromItsEvent()
        {
            BoardState none = BoardState.Read(Model(), new StageSettings());
            Assert.IsFalse(none.HasLastMove);
            BoardState state = BoardState.Read(Model("{\"t\":\"last_move\",\"x\":4,\"y\":7,\"seat\":1}"), new StageSettings());
            Assert.IsTrue(state.HasLastMove);
            Assert.AreEqual(4, state.LastX);
            Assert.AreEqual(7, state.LastY);
            Assert.IsFalse(BoardState.Read(Model("{\"t\":\"last_move\",\"x\":0,\"y\":7}"), new StageSettings()).HasLastMove);
        }

        [Test]
        public void AGomokuGameShowsItsBoardMovesAndWinningLine()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":9}")))
            {
                StageSettings hints = StageSettings.Resolve(session.Mod.Manifest.Client, null);
                BoardState fresh = BoardState.Read(session.View, hints);
                Assert.AreEqual(9, fresh.Columns);
                Assert.AreEqual(9, fresh.Rows);
                Assert.IsFalse(fresh.HasLastMove);
                Assert.AreEqual(0, fresh.WinningCells.Count);

                TestSupport.PlaceOk(session, 1, 5, 5);
                TestSupport.PlaceOk(session, 2, 4, 4);
                BoardState played = BoardState.Read(session.View, hints);
                Assert.AreEqual("stone", played.ObjectAt(5, 5).Kind);
                Assert.AreEqual(1, played.ObjectAt(5, 5).Owner);
                Assert.AreEqual(2, played.ObjectAt(4, 4).Owner);
                Assert.IsFalse(played.IsOccupied(1, 1));
                Assert.IsTrue(played.HasLastMove);
                Assert.AreEqual(4, played.LastX);
                Assert.AreEqual(4, played.LastY);
            }

            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":9}")))
            {
                for (int x = 1; x <= 4; x++)
                {
                    TestSupport.PlaceOk(session, 1, x, 5);
                    TestSupport.PlaceOk(session, 2, x, 1);
                }
                TestSupport.PlaceOk(session, 1, 5, 5);

                BoardState won = BoardState.Read(session.View, StageSettings.Resolve(session.Mod.Manifest.Client, null));
                Assert.AreEqual(5, won.WinningCells.Count);
                foreach (Point2 cell in won.WinningCells)
                {
                    Assert.AreEqual(5.0, cell.Y, 1e-9);
                    Assert.IsTrue(won.IsOccupied(BoardState.Cell(cell.X), BoardState.Cell(cell.Y)));
                }
            }
        }
    }
}
