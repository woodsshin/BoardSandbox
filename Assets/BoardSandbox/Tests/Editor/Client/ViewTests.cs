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
                Assert.AreEqual(BoardStyle.Lines, hints.Style);
                Assert.AreEqual(0, hints.Columns);
                Assert.AreEqual(0, hints.Rows);
                Assert.AreEqual("", hints.ClickAction);
                Assert.IsFalse(hints.CanClick);
                Assert.IsNull(hints.PreviewKind);
                Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("stone"));
                Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("pawn"));
                Assert.AreEqual(PieceShape.Disc, hints.ShapeFor(null));
            }
        }

        [Test]
        public void EverySettingIsRead()
        {
            ViewHints hints = new ViewHints(JsonValue.Parse(
                "{\"board\":{\"style\":\"CHECKER\",\"columns\":10,\"rows\":6},\"click\":{\"action\":\"move\",\"preview\":\"pawn\"}," +
                "\"pieces\":{\"pawn\":{\"shape\":\"cube\"},\"king\":{\"shape\":\"Stone\"},\"coin\":{\"shape\":\"disc\"}}}"));
            Assert.AreEqual(BoardStyle.Checker, hints.Style);
            Assert.AreEqual(10, hints.Columns);
            Assert.AreEqual(6, hints.Rows);
            Assert.AreEqual("move", hints.ClickAction);
            Assert.IsTrue(hints.CanClick);
            Assert.AreEqual("pawn", hints.PreviewKind);
            Assert.AreEqual(PieceShape.Cube, hints.ShapeFor("pawn"));
            Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("king"));
            Assert.AreEqual(PieceShape.Disc, hints.ShapeFor("coin"));
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
            {
                ViewHints hints = new ViewHints(JsonValue.Parse(json));
                Assert.IsFalse(hints.CanClick, json);
                Assert.AreEqual(BoardStyle.Lines, hints.Style, json);
            }
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
            Assert.AreEqual(BoardStyle.Lines, hints.Style);
            Assert.AreEqual("place_stone", hints.ClickAction);
            Assert.AreEqual("stone", hints.PreviewKind);
            Assert.AreEqual(PieceShape.Stone, hints.ShapeFor("stone"));
        }
    }

    [TestFixture]
    public class PieceStyleTests
    {
        private static ModManifest Manifest()
        {
            return ModManifest.Parse("{\"id\":\"t\",\"name\":\"t\",\"api\":1,\"entry\":\"m.luau\",\"seats\":[{\"color\":\"#ff0000\"},{\"color\":\"#00ff00\"}]}");
        }

        private static ViewObject Piece(int owner, string props, string kind = "pawn")
        {
            return new ViewObject(1, kind, 3, 4, owner, JsonValue.Parse(props));
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
            PieceStyle style = PieceStyle.For(Piece(2, "{\"color\":\"#0000ff\"}"), new ViewHints(null), Manifest());
            AssertColor(style.Color, 0, 0, 255);
        }

        [Test]
        public void TheSeatColorIsTheFallback()
        {
            AssertColor(PieceStyle.For(Piece(1, "{}"), new ViewHints(null), Manifest()).Color, 255, 0, 0);
            AssertColor(PieceStyle.For(Piece(2, "{}"), new ViewHints(null), Manifest()).Color, 0, 255, 0);
            AssertColor(PieceStyle.For(Piece(2, "{\"color\":\"nonsense\"}"), new ViewHints(null), Manifest()).Color, 0, 255, 0);
        }

        [Test]
        public void PiecesWithoutASeatAreNeutral()
        {
            AssertColor(PieceStyle.For(Piece(0, "{}"), new ViewHints(null), Manifest()).Color, 0x8a, 0x8a, 0x8a);
            AssertColor(PieceStyle.For(Piece(9, "{}"), new ViewHints(null), Manifest()).Color, 0x8a, 0x8a, 0x8a);
            AssertColor(PieceStyle.For(Piece(1, "{}"), new ViewHints(null), null).Color, 0x8a, 0x8a, 0x8a);
        }

        [Test]
        public void ColorNamesAreUnderstood()
        {
            PieceStyle black = PieceStyle.For(Piece(1, "{\"color\":\"black\"}"), new ViewHints(null), Manifest());
            PieceStyle white = PieceStyle.For(Piece(1, "{\"color\":\"white\"}"), new ViewHints(null), Manifest());
            Assert.IsTrue(black.Color.r < 64 && white.Color.r > 192);
        }

        [Test]
        public void TheScaleIsClamped()
        {
            Assert.AreEqual(1f, PieceStyle.For(Piece(1, "{}"), new ViewHints(null), null).Scale, 1e-6);
            Assert.AreEqual(1.5f, PieceStyle.For(Piece(1, "{\"scale\":1.5}"), new ViewHints(null), null).Scale, 1e-6);
            Assert.AreEqual(PieceStyle.MaxScale, PieceStyle.For(Piece(1, "{\"scale\":10}"), new ViewHints(null), null).Scale, 1e-6);
            Assert.AreEqual(PieceStyle.MinScale, PieceStyle.For(Piece(1, "{\"scale\":0.01}"), new ViewHints(null), null).Scale, 1e-6);
            Assert.AreEqual(PieceStyle.MinScale, PieceStyle.For(Piece(1, "{\"scale\":-3}"), new ViewHints(null), null).Scale, 1e-6);
            Assert.AreEqual(1f, PieceStyle.For(Piece(1, "{\"scale\":\"big\"}"), new ViewHints(null), null).Scale, 1e-6);
        }

        [Test]
        public void TheShapeComesFromTheHints()
        {
            ViewHints hints = new ViewHints(JsonValue.Parse("{\"pieces\":{\"pawn\":{\"shape\":\"cube\"}}}"));
            Assert.AreEqual(PieceShape.Cube, PieceStyle.For(Piece(1, "{}", "pawn"), hints, null).Shape);
            Assert.AreEqual(PieceShape.Disc, PieceStyle.For(Piece(1, "{}", "rook"), hints, null).Shape);
        }

        [Test]
        public void StylesCompareEveryField()
        {
            PieceStyle a = new PieceStyle(PieceShape.Disc, new Color32(1, 2, 3, 4), 1f);
            Assert.IsTrue(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(1, 2, 3, 4), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Cube, new Color32(1, 2, 3, 4), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(9, 2, 3, 4), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(1, 9, 3, 4), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(1, 2, 9, 4), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(1, 2, 3, 9), 1f)));
            Assert.IsFalse(a.Equals(new PieceStyle(PieceShape.Disc, new Color32(1, 2, 3, 4), 2f)));
        }

        [Test]
        public void SeatColorsComeFromTheManifest()
        {
            AssertColor(PieceStyle.SeatColor(Manifest(), 1), 255, 0, 0);
            AssertColor(PieceStyle.SeatColor(Manifest(), 2), 0, 255, 0);
            AssertColor(PieceStyle.SeatColor(Manifest(), 3), 0x8a, 0x8a, 0x8a);
            AssertColor(PieceStyle.SeatColor(null, 1), 0x8a, 0x8a, 0x8a);
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
            BoardState state = BoardState.Read(model, new ViewHints(JsonValue.Parse("{\"board\":{\"columns\":5,\"rows\":5}}")));
            Assert.AreEqual(10, state.Columns);
            Assert.AreEqual(6, state.Rows);
        }

        [Test]
        public void HintsDecideWhenThereIsNoBoardEvent()
        {
            BoardState state = BoardState.Read(Model(), new ViewHints(JsonValue.Parse("{\"board\":{\"columns\":5,\"rows\":4}}")));
            Assert.AreEqual(5, state.Columns);
            Assert.AreEqual(4, state.Rows);
        }

        [Test]
        public void TheBoardGrowsToHoldItsPiecesWhenNothingSaysHowBigItIs()
        {
            Assert.AreEqual(BoardState.DefaultSize, BoardState.Read(Model(), new ViewHints(null)).Columns);
            Assert.AreEqual(BoardState.DefaultSize, BoardState.Read(Model(), new ViewHints(null)).Rows);

            ViewModel model = Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":12,\"y\":3}", "{\"t\":\"spawn\",\"id\":2,\"kind\":\"a\",\"x\":2.5,\"y\":9.5}");
            BoardState state = BoardState.Read(model, new ViewHints(null));
            Assert.AreEqual(12, state.Columns);
            Assert.AreEqual(10, state.Rows);
        }

        [Test]
        public void TheSizeIsLimited()
        {
            BoardState big = BoardState.Read(Model("{\"t\":\"board\",\"columns\":1000,\"rows\":65}"), new ViewHints(null));
            Assert.AreEqual(BoardLayout.MaxSize, big.Columns);
            Assert.AreEqual(BoardLayout.MaxSize, big.Rows);

            BoardState far = BoardState.Read(Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":1e300,\"y\":-1e300}"), new ViewHints(null));
            Assert.AreEqual(BoardLayout.MaxSize, far.Columns);
            Assert.AreEqual(BoardState.DefaultSize, far.Rows);
        }

        [Test]
        public void PiecesAreFoundByTheirNearestCell()
        {
            ViewModel model = Model("{\"t\":\"spawn\",\"id\":1,\"kind\":\"a\",\"x\":3,\"y\":4}", "{\"t\":\"spawn\",\"id\":2,\"kind\":\"a\",\"x\":5.4,\"y\":6.6}");
            BoardState state = BoardState.Read(model, new ViewHints(null));
            Assert.AreEqual(1, state.ObjectAt(3, 4).Id);
            Assert.AreEqual(2, state.ObjectAt(5, 7).Id);
            Assert.IsTrue(state.IsOccupied(3, 4));
            Assert.IsFalse(state.IsOccupied(4, 3));
            Assert.IsNull(state.ObjectAt(1, 1));
        }

        [Test]
        public void TheNewerPieceWinsASharedCell()
        {
            ViewModel model = Model("{\"t\":\"spawn\",\"id\":7,\"kind\":\"a\",\"x\":2,\"y\":2}", "{\"t\":\"spawn\",\"id\":3,\"kind\":\"a\",\"x\":2,\"y\":2}");
            Assert.AreEqual(7, BoardState.Read(model, new ViewHints(null)).ObjectAt(2, 2).Id);
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
            BoardState none = BoardState.Read(Model(), new ViewHints(null));
            Assert.IsFalse(none.HasLastMove);
            BoardState state = BoardState.Read(Model("{\"t\":\"last_move\",\"x\":4,\"y\":7,\"seat\":1}"), new ViewHints(null));
            Assert.IsTrue(state.HasLastMove);
            Assert.AreEqual(4, state.LastX);
            Assert.AreEqual(7, state.LastY);
            Assert.IsFalse(BoardState.Read(Model("{\"t\":\"last_move\",\"x\":0,\"y\":7}"), new ViewHints(null)).HasLastMove);
        }

        [Test]
        public void AGomokuGameShowsItsBoardMovesAndWinningLine()
        {
            using (GameSession session = TestSupport.StartGomoku(TestSupport.Options("{\"size\":9}")))
            {
                ViewHints hints = new ViewHints(session.Mod.Manifest.Client);
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

                BoardState won = BoardState.Read(session.View, new ViewHints(session.Mod.Manifest.Client));
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
