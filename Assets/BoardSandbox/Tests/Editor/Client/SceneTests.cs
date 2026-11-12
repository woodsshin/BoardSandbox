using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using NUnit.Framework;
using UnityEngine;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class Rot3Tests
    {
        private static readonly Vector3 X = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 Y = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 Z = new Vector3(0f, 0f, 1f);

        [Test]
        public void TurnsFollowTheRightHand()
        {
            GeometryAssert.Near(Y, Rot3.AxisAngle(Z, 90f).Rotate(X), 1e-6f);
            GeometryAssert.Near(Z, Rot3.AxisAngle(X, 90f).Rotate(Y), 1e-6f);
            GeometryAssert.Near(X, Rot3.AxisAngle(Y, 90f).Rotate(Z), 1e-6f);
            GeometryAssert.Near(X, Rot3.AxisAngle(Vector3.zero, 90f).Rotate(X), 1e-6f);
            GeometryAssert.Near(Y, Rot3.AxisAngle(Z * 5f, 90f).Rotate(X), 1e-6f);
        }

        [Test]
        public void ProductsApplyTheRightSideFirst()
        {
            Rot3 both = Rot3.AxisAngle(Z, 90f) * Rot3.AxisAngle(X, 90f);
            GeometryAssert.Near(Rot3.AxisAngle(Z, 90f).Rotate(Rot3.AxisAngle(X, 90f).Rotate(Y)), both.Rotate(Y), 1e-6f);
            Vector3 v = new Vector3(0.3f, -1.2f, 2f);
            GeometryAssert.Near(v, both.Unrotate(both.Rotate(v)), 1e-5f);
            GeometryAssert.Near(v, (both.Transposed * both).Rotate(v), 1e-5f);
        }

        [Test]
        public void EulerAnglesTurnAroundXThenYThenZ()
        {
            Rot3 euler = Rot3.Euler(90f, 90f, 0f);
            // y goes to z by the turn around x, then z goes to x by the turn around y.
            GeometryAssert.Near(X, euler.Rotate(Y), 1e-6f);
            GeometryAssert.Near(Rot3.AxisAngle(Z, 30f).Rotate(X), Rot3.Euler(0f, 0f, 30f).Rotate(X), 1e-6f);
        }

        [Test]
        public void RotationsAreReadFromThreeOrNineNumbers()
        {
            Assert.IsTrue(Rot3.TryRead(JsonValue.Parse("[0,0,90]"), out Rot3 euler));
            GeometryAssert.Near(Y, euler.Rotate(X), 1e-6f);
            Assert.IsTrue(Rot3.TryRead(JsonValue.Parse("[0,-1,0, 1,0,0, 0,0,1]"), out Rot3 matrix));
            GeometryAssert.Near(Y, matrix.Rotate(X), 1e-6f);
            Assert.IsTrue(Rot3.TryRead(JsonValue.Parse("[0,-2,0, 2,0,0, 0,0,2]"), out Rot3 scaled));
            GeometryAssert.Near(Y, scaled.Rotate(X), 1e-6f);

            foreach (string bad in new[] { "[1,2]", "[1,2,3,4]", "\"x\"", "null", "[0,0,\"a\"]", "[0,0,0, 0,0,0, 0,0,0]", "[1,0,0, 0,1,0, 0,0,-1]", "[1,0,0, 1,0,0, 0,0,1]" })
            {
                Assert.IsFalse(Rot3.TryRead(JsonValue.Parse(bad), out Rot3 rejected), bad);
                GeometryAssert.Near(X, rejected.Rotate(X), 1e-6f);
            }
            Assert.IsFalse(Rot3.TryRead(null, out _));
        }

        [Test]
        public void QuaternionsTurnVectorsLikeTheMatrix()
        {
            System.Random random = new System.Random(7);
            List<Rot3> samples = new List<Rot3> { Rot3.Identity, Rot3.AxisAngle(X, 180f), Rot3.AxisAngle(Y, 180f), Rot3.AxisAngle(Z, 180f), Rot3.AxisAngle(X, 90f) };
            for (int i = 0; i < 200; i++)
            {
                Vector3 axis = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                samples.Add(Rot3.AxisAngle(axis, (float)random.NextDouble() * 720f - 360f));
            }
            Vector3 v = new Vector3(0.7f, -0.2f, 1.3f);
            foreach (Rot3 rotation in samples)
            {
                Quaternion q = rotation.ToQuaternion();
                Assert.AreEqual(1f, Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w), 1e-4f);
                GeometryAssert.Near(rotation.Rotate(v), q * v, 1e-4f);
            }
        }

        [Test]
        public void TheSceneSeesModRotationsMirrored()
        {
            System.Random random = new System.Random(11);
            for (int i = 0; i < 50; i++)
            {
                Vector3 axis = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                float degrees = (float)random.NextDouble() * 360f - 180f;
                Rot3 mod = Rot3.AxisAngle(axis, degrees);
                Vector3 v = new Vector3(0.4f, 1.1f, -0.6f);
                GeometryAssert.Near(Frame.Flip(mod.Rotate(v)), Frame.ToScene(mod).Rotate(Frame.Flip(v)), 1e-5f);
                GeometryAssert.Near(Frame.Flip(mod.Rotate(v)), Frame.TurnToScene(axis, degrees).Rotate(Frame.Flip(v)), 1e-5f);
            }
        }

        [Test]
        public void ATurnEffectEndsWhereTheNewStateIs()
        {
            // A cubie at (1,1,1) turned a quarter clockwise around +y, as a mod would describe it.
            Vector3 axis = Y;
            Rot3 quarter = Rot3.AxisAngle(axis, -90f);
            Vector3 before = new Vector3(1f, 1f, 1f);
            Rot3 turnedBefore = Rot3.AxisAngle(X, 90f);
            Vector3 after = quarter.Rotate(before);
            Rot3 turnedAfter = quarter * turnedBefore;

            Rot3 effect = Frame.TurnToScene(axis, -90f);
            GeometryAssert.Near(Frame.Flip(after), effect.Rotate(Frame.Flip(before)), 1e-5f);
            Rot3 shown = effect * Frame.ToScene(turnedBefore);
            Rot3 wanted = Frame.ToScene(turnedAfter);
            foreach (Vector3 probe in new[] { X, Y, Z })
                GeometryAssert.Near(wanted.Rotate(probe), shown.Rotate(probe), 1e-5f);
        }

        [Test]
        public void VectorsAreReadStrictly()
        {
            Assert.IsTrue(Frame.TryReadVector(JsonValue.Parse("[1,2.5,-3]"), out Vector3 v));
            GeometryAssert.Near(new Vector3(1f, 2.5f, -3f), v, 1e-6f);
            Assert.IsTrue(Frame.TryReadVector(JsonValue.Parse("[1e30,0,0]"), out Vector3 far));
            Assert.AreEqual(Frame.Limit, far.x);
            foreach (string bad in new[] { "[1,2]", "[1,2,3,4]", "[1,\"2\",3]", "5", "null", "{}" })
                Assert.IsFalse(Frame.TryReadVector(JsonValue.Parse(bad), out _), bad);
            Assert.IsFalse(Frame.TryReadVector(null, out _));
        }
    }

    [TestFixture]
    public class ScenePickerTests
    {
        [Test]
        public void ARayMeetsTheNearSideOfABox()
        {
            Assert.IsTrue(ScenePicker.RayBox(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 1f), Vector3.zero, Rot3.Identity, new Vector3(1f, 2f, 3f), out float distance, out Vector3 normal));
            Assert.AreEqual(2f, distance, 1e-5f);
            GeometryAssert.Near(new Vector3(0f, 0f, -1f), normal, 1e-6f);

            Assert.IsTrue(ScenePicker.RayBox(new Vector3(0.5f, 9f, 0.5f), new Vector3(0f, -1f, 0f), Vector3.zero, Rot3.Identity, new Vector3(1f, 2f, 3f), out distance, out normal));
            Assert.AreEqual(7f, distance, 1e-5f);
            GeometryAssert.Near(new Vector3(0f, 1f, 0f), normal, 1e-6f);
        }

        [Test]
        public void MissesAndBoxesBehindTheRayAreNotHit()
        {
            Vector3 half = new Vector3(1f, 1f, 1f);
            Assert.IsFalse(ScenePicker.RayBox(new Vector3(3f, 0f, -5f), new Vector3(0f, 0f, 1f), Vector3.zero, Rot3.Identity, half, out _, out _));
            Assert.IsFalse(ScenePicker.RayBox(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, 1f), Vector3.zero, Rot3.Identity, half, out _, out _));
            Assert.IsFalse(ScenePicker.RayBox(Vector3.zero, new Vector3(0f, 0f, 1f), Vector3.zero, Rot3.Identity, half, out _, out _));
            Assert.IsFalse(ScenePicker.RayBox(new Vector3(0f, 2f, -5f), new Vector3(0f, 0f, 1f), Vector3.zero, Rot3.Identity, half, out _, out _));
        }

        [Test]
        public void TurnedAndMovedBoxesAreHitWhereTheyAre()
        {
            Rot3 turned = Rot3.AxisAngle(new Vector3(0f, 1f, 0f), 45f);
            Vector3 center = new Vector3(10f, 0f, 0f);
            Assert.IsTrue(ScenePicker.RayBox(new Vector3(10f, 0f, -5f), new Vector3(0f, 0f, 1f), center, turned, Vector3.one, out float distance, out Vector3 normal));
            Assert.AreEqual(5f - Mathf.Sqrt(2f), distance, 1e-4f);
            Assert.AreEqual(1f, normal.magnitude, 1e-5f);
            Assert.Less(normal.z, -0.7f);
            Assert.IsFalse(ScenePicker.RayBox(new Vector3(11.35f, 0f, -5f), new Vector3(0f, 0f, 1f), center, Rot3.Identity, Vector3.one, out _, out _));
            Assert.IsTrue(ScenePicker.RayBox(new Vector3(11.35f, 0f, -5f), new Vector3(0f, 0f, 1f), center, turned, Vector3.one, out _, out _));
        }

        [Test]
        public void EllipsoidsAreHitOnTheirSurface()
        {
            Assert.IsTrue(ScenePicker.RayEllipsoid(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f), Vector3.zero, Rot3.Identity, new Vector3(2f, 0.5f, 2f), out float distance, out Vector3 normal));
            Assert.AreEqual(4.5f, distance, 1e-5f);
            GeometryAssert.Near(new Vector3(0f, 1f, 0f), normal, 1e-5f);
            Assert.IsFalse(ScenePicker.RayEllipsoid(new Vector3(1.9f, 5f, 1.9f), new Vector3(0f, -1f, 0f), Vector3.zero, Rot3.Identity, new Vector3(2f, 0.5f, 2f), out _, out _));
            Assert.IsFalse(ScenePicker.RayEllipsoid(new Vector3(0f, -5f, 0f), new Vector3(0f, -1f, 0f), Vector3.zero, Rot3.Identity, new Vector3(2f, 0.5f, 2f), out _, out _));
            Assert.IsFalse(ScenePicker.RayEllipsoid(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f), Vector3.zero, Rot3.Identity, new Vector3(2f, 0f, 2f), out _, out _));
        }

        [Test]
        public void PlanesAreMetInFrontOfTheRayOnly()
        {
            Assert.IsTrue(ScenePicker.RayPlane(new Vector3(1f, 4f, 2f), new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), out Vector3 hit));
            GeometryAssert.Near(new Vector3(1f, 1f, 2f), hit, 1e-6f);
            Assert.IsFalse(ScenePicker.RayPlane(new Vector3(1f, 4f, 2f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), out _));
            Assert.IsFalse(ScenePicker.RayPlane(new Vector3(1f, 4f, 2f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), out _));
        }
    }

    [TestFixture]
    public class StageSettingsTests
    {
        private static void AssertColor(Color32 color, int r, int g, int b, int a = 255)
        {
            Assert.AreEqual(r, (int)color.r);
            Assert.AreEqual(g, (int)color.g);
            Assert.AreEqual(b, (int)color.b);
            Assert.AreEqual(a, (int)color.a);
        }

        [Test]
        public void NothingSaidMeansTheUsualTable()
        {
            foreach (StageSettings stage in new[] { new StageSettings(), StageSettings.Parse(null), StageSettings.Parse(JsonValue.Parse("[]")), StageSettings.Resolve(null, null), StageSettings.Parse(JsonValue.NewObject()) })
            {
                Assert.IsTrue(stage.Board.Visible);
                Assert.AreEqual(BoardStyle.Lines, stage.Board.Style);
                Assert.AreEqual(0, stage.Board.Columns);
                Assert.AreEqual(1f, stage.Board.Cell);
                AssertColor(stage.Board.Palette.Slab, 0xdc, 0xb0, 0x6b);
                AssertColor(stage.Board.Palette.Line, 0x2b, 0x1d, 0x10);
                Assert.IsTrue(stage.Board.Palette.Stars);
                Assert.IsTrue(stage.Table.Visible);
                AssertColor(stage.Table.Color, 0x27, 0x31, 0x3b);
                Assert.IsTrue(float.IsNaN(stage.Table.Y));
                Assert.AreEqual(55f, stage.Camera.Pitch);
                Assert.AreEqual(0f, stage.Camera.Yaw);
                Assert.AreEqual(40f, stage.Camera.Fov);
                Assert.AreEqual(DragMode.Pan, stage.Camera.LeftDrag);
                Assert.AreEqual(DragMode.Orbit, stage.Camera.RightDrag);
                Assert.IsTrue(stage.Camera.Zoom);
                GeometryAssert.Near(new Vector3(-0.35f, 0.85f, 0.4f), stage.Light.Direction, 1e-6f);
                Assert.AreEqual(0.45f, stage.Light.Ambient);
                Assert.AreEqual(0.65f, stage.Light.Intensity);
                AssertColor(stage.LastMove.Color, 0xe5, 0x3d, 0x2f);
                AssertColor(stage.Shadow.Color, 0, 0, 0, 80);
                Assert.IsTrue(stage.Win.Visible && stage.Cursor.Visible && stage.Preview.Visible);

                BoardLayout usual = new BoardLayout(15, 15);
                BoardLayout made = stage.Layout(15, 15);
                Assert.AreEqual(usual.Margin, made.Margin);
                Assert.AreEqual(usual.Thickness, made.Thickness);
            }
        }

        [Test]
        public void EverySettingIsRead()
        {
            StageSettings stage = StageSettings.Parse(JsonValue.Parse(@"{
                ""background"": ""#102030"",
                ""table"": { ""visible"": false, ""color"": ""#405060"", ""size"": 50, ""y"": -2 },
                ""board"": { ""visible"": false, ""style"": ""Checker"", ""columns"": 7, ""rows"": 6, ""cell"": 2, ""margin"": 0.5, ""thickness"": 1,
                    ""colors"": { ""slab"": ""#010101"", ""line"": ""#020202"", ""light"": ""#030303"", ""dark"": ""#040404"" },
                    ""lines"": { ""thin"": 0.1, ""thick"": 0.2 }, ""stars"": false },
                ""light"": { ""direction"": [0, 2, 0], ""color"": ""#ff8000"", ""ambient"": 0.2, ""intensity"": 1.5 },
                ""camera"": { ""yaw"": 30, ""pitch"": -20, ""min_pitch"": -80, ""max_pitch"": 80, ""fov"": 60, ""target"": [1, 2, 3], ""distance"": 12,
                    ""radius"": 4, ""min_distance"": 2, ""max_distance"": 40, ""left_drag"": ""orbit"", ""right_drag"": ""pan"", ""middle_drag"": ""none"", ""zoom"": false },
                ""markers"": { ""last_move"": { ""visible"": false, ""color"": ""#00ff00"", ""size"": 2 }, ""win"": { ""color"": ""#0000ff80"" },
                    ""cursor"": { ""visible"": false }, ""preview"": { ""color"": ""#ffffff40"" }, ""shadow"": { ""visible"": false } }
            }"));
            AssertColor(stage.Background, 0x10, 0x20, 0x30);
            Assert.IsFalse(stage.Table.Visible);
            AssertColor(stage.Table.Color, 0x40, 0x50, 0x60);
            Assert.AreEqual(50f, stage.Table.Size);
            Assert.AreEqual(-2f, stage.Table.Y);
            Assert.IsFalse(stage.Board.Visible);
            Assert.AreEqual(BoardStyle.Checker, stage.Board.Style);
            Assert.AreEqual(7, stage.Board.Columns);
            Assert.AreEqual(6, stage.Board.Rows);
            AssertColor(stage.Board.Palette.Slab, 1, 1, 1);
            AssertColor(stage.Board.Palette.Line, 2, 2, 2);
            AssertColor(stage.Board.Palette.Light, 3, 3, 3);
            AssertColor(stage.Board.Palette.Dark, 4, 4, 4);
            Assert.AreEqual(0.1f, stage.Board.Palette.ThinLine);
            Assert.AreEqual(0.2f, stage.Board.Palette.ThickLine);
            Assert.IsFalse(stage.Board.Palette.Stars);
            BoardLayout layout = stage.Layout(7, 6);
            Assert.AreEqual(2f, layout.CellSize);
            Assert.AreEqual(1f, layout.Margin, 1e-6f);
            Assert.AreEqual(2f, layout.Thickness, 1e-6f);
            GeometryAssert.Near(new Vector3(0f, 1f, 0f), stage.Light.Direction, 1e-6f);
            AssertColor(stage.Light.Color, 255, 128, 0);
            Assert.AreEqual(0.2f, stage.Light.Ambient);
            Assert.AreEqual(1.5f, stage.Light.Intensity);
            CameraSettings camera = stage.Camera;
            Assert.AreEqual(30f, camera.Yaw);
            Assert.AreEqual(-20f, camera.Pitch);
            Assert.AreEqual(-80f, camera.MinPitch);
            Assert.AreEqual(80f, camera.MaxPitch);
            Assert.AreEqual(60f, camera.Fov);
            GeometryAssert.Near(new Vector3(1f, 2f, 3f), camera.Target, 1e-6f);
            Assert.AreEqual(12f, camera.Distance);
            Assert.AreEqual(4f, camera.Radius);
            Assert.AreEqual(2f, camera.MinDistance);
            Assert.AreEqual(40f, camera.MaxDistance);
            Assert.AreEqual(DragMode.Orbit, camera.LeftDrag);
            Assert.AreEqual(DragMode.Pan, camera.RightDrag);
            Assert.AreEqual(DragMode.None, camera.MiddleDrag);
            Assert.IsFalse(camera.Zoom);
            Assert.IsFalse(stage.LastMove.Visible);
            AssertColor(stage.LastMove.Color, 0, 255, 0);
            Assert.AreEqual(2f, stage.LastMove.Size);
            AssertColor(stage.Win.Color, 0, 0, 255, 128);
            Assert.IsFalse(stage.Cursor.Visible);
            Assert.AreEqual(0x40, (int)stage.Preview.Color.a);
            Assert.IsFalse(stage.Shadow.Visible);
        }

        [Test]
        public void NonsenseKeepsTheUsualValues()
        {
            StageSettings stage = StageSettings.Parse(JsonValue.Parse(@"{
                ""background"": 5, ""table"": ""no"", ""board"": { ""cell"": ""big"", ""columns"": -3, ""colors"": { ""slab"": ""nope"" }, ""style"": 7 },
                ""light"": { ""direction"": [0, 0, 0], ""ambient"": -5, ""intensity"": 99 },
                ""camera"": { ""pitch"": 500, ""fov"": 1, ""left_drag"": ""fly"", ""target"": [1, 2] }, ""markers"": [] }"));
            AssertColor(stage.Background, 0x13, 0x16, 0x1a);
            Assert.IsTrue(stage.Table.Visible);
            Assert.AreEqual(1f, stage.Board.Cell);
            Assert.AreEqual(0, stage.Board.Columns);
            AssertColor(stage.Board.Palette.Slab, 0xdc, 0xb0, 0x6b);
            Assert.AreEqual(BoardStyle.Lines, stage.Board.Style);
            GeometryAssert.Near(new Vector3(-0.35f, 0.85f, 0.4f), stage.Light.Direction, 1e-6f);
            Assert.AreEqual(0f, stage.Light.Ambient);
            Assert.AreEqual(4f, stage.Light.Intensity);
            Assert.AreEqual(CameraMath.MaxPitch, stage.Camera.Pitch);
            Assert.AreEqual(10f, stage.Camera.Fov);
            Assert.AreEqual(DragMode.Pan, stage.Camera.LeftDrag);
            GeometryAssert.Near(Vector3.zero, stage.Camera.Target, 1e-6f);
            Assert.IsTrue(stage.LastMove.Visible);
        }

        [Test]
        public void TheHintsTheStageAndEventsLayerOverEachOther()
        {
            JsonValue client = JsonValue.Parse(@"{ ""board"": { ""style"": ""checker"", ""columns"": 8, ""rows"": 8 },
                ""stage"": { ""board"": { ""rows"": 10, ""colors"": { ""slab"": ""#111111"" } }, ""background"": ""#222222"" } }");
            StageSettings fromConfig = StageSettings.Resolve(client, null);
            Assert.AreEqual(BoardStyle.Checker, fromConfig.Board.Style);
            Assert.AreEqual(8, fromConfig.Board.Columns);
            Assert.AreEqual(10, fromConfig.Board.Rows);
            AssertColor(fromConfig.Board.Palette.Slab, 0x11, 0x11, 0x11);
            AssertColor(fromConfig.Background, 0x22, 0x22, 0x22);

            ViewEvent e = ViewEvent.Parse(JsonValue.Parse(@"{ ""t"": ""stage"", ""board"": { ""visible"": false, ""colors"": { ""line"": ""#333333"" } }, ""background"": ""#444444"" }"));
            StageSettings live = StageSettings.Resolve(client, e);
            Assert.IsFalse(live.Board.Visible);
            Assert.AreEqual(BoardStyle.Checker, live.Board.Style);
            Assert.AreEqual(10, live.Board.Rows);
            AssertColor(live.Board.Palette.Slab, 0x11, 0x11, 0x11);
            AssertColor(live.Board.Palette.Line, 0x33, 0x33, 0x33);
            AssertColor(live.Background, 0x44, 0x44, 0x44);
            Assert.IsFalse(live.Source.Has("t"));

            Assert.IsFalse(live.SameAs(fromConfig));
            Assert.IsTrue(live.SameAs(StageSettings.Resolve(client, e)));
            Assert.IsFalse(live.SameAs(null));
            Assert.IsTrue(fromConfig.Board.Visible);
        }

        [Test]
        public void TheBoardIsPaintedWithItsPalette()
        {
            StageSettings stage = StageSettings.Parse(JsonValue.Parse(@"{ ""board"": { ""colors"": { ""slab"": ""#ff0000"", ""line"": ""#00ff00"" }, ""stars"": false } }"));
            MeshBuilder mesh = BoardShapes.Board(stage.Layout(9, 9), stage.Board.Palette);
            GeometryAssert.Oriented(mesh);
            int red = 0;
            int green = 0;
            foreach (Color32 c in mesh.ColorList)
            {
                if (c.r == 255 && c.g == 0)
                    red++;
                else if (c.g == 255 && c.r == 0)
                    green++;
                else
                    Assert.Fail("unexpected color");
            }
            Assert.AreEqual(24, red);
            Assert.AreEqual(18 * 4, green);
            Assert.Greater(BoardShapes.Board(stage.Layout(9, 9)).VertexCount, mesh.VertexCount);
            AssertColor(BoardShapes.Table(5f, new Color32(1, 2, 3, 255)).ColorList[0], 1, 2, 3);
        }
    }

    [TestFixture]
    public class CameraPlanTests
    {
        [Test]
        public void TheBoardIsFramedAsBefore()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            CameraPlan plan = CameraPlan.Resolve(new CameraSettings(), layout, 16f / 9f);
            Assert.AreEqual(layout.FitDistance(40f, 16f / 9f, 0f, 55f), plan.Distance, 1e-4f);
            Assert.AreEqual(0f, plan.Yaw);
            Assert.AreEqual(55f, plan.Pitch);
            GeometryAssert.Near(Vector3.zero, plan.Target, 1e-6f);
            GeometryAssert.Near(new Vector3(layout.SlabHalfWidth, 0f, layout.SlabHalfDepth), plan.Reach, 1e-6f);
            Assert.AreEqual(2f, plan.MinDistance, 1e-6f);
            Assert.AreEqual(plan.Distance * 3f, plan.MaxDistance, 1e-4f);
            Assert.AreEqual(CameraMath.MinPitch, plan.MinPitch);
        }

        [Test]
        public void ARadiusFitsABall()
        {
            CameraSettings settings = new CameraSettings { Radius = 3f, Fov = 40f, Target = new Vector3(1f, 2f, 3f) };
            CameraPlan wide = CameraPlan.Resolve(settings, null, 2f);
            Assert.AreEqual(3f * 1.15f / Mathf.Sin(20f * Mathf.Deg2Rad), wide.Distance, 1e-3f);
            CameraPlan tall = CameraPlan.Resolve(settings, null, 0.5f);
            Assert.Greater(tall.Distance, wide.Distance);
            GeometryAssert.Near(new Vector3(1f, 2f, -3f), wide.Target, 1e-6f);
            GeometryAssert.Near(new Vector3(3f, 3f, 3f), wide.Reach, 1e-6f);
            Assert.LessOrEqual(wide.MinDistance, wide.Distance);
            Assert.GreaterOrEqual(wide.MaxDistance, wide.Distance);

            // The ball wins over a board that is there as well.
            Assert.AreEqual(wide.Distance, CameraPlan.Resolve(settings, new BoardLayout(19, 19), 2f).Distance, 1e-4f);
            Assert.Greater(CameraPlan.Resolve(new CameraSettings(), null, 2f).Distance, 0f);
        }

        [Test]
        public void AGivenDistanceIsKept()
        {
            CameraSettings settings = new CameraSettings { Distance = 12f, MinDistance = 20f, MaxDistance = 5f };
            CameraPlan plan = CameraPlan.Resolve(settings, new BoardLayout(9, 9), 1.5f);
            Assert.AreEqual(12f, plan.Distance);
            Assert.AreEqual(12f, plan.MinDistance);
            Assert.AreEqual(12f, plan.MaxDistance);
            Assert.AreEqual(12f, CameraPlan.Resolve(settings, null, 1.5f).Distance);
        }

        [Test]
        public void PitchLimitsCanReachBelowTheTable()
        {
            Assert.AreEqual(-80f, CameraMath.ClampPitch(-120f, -80f, 80f));
            Assert.AreEqual(80f, CameraMath.ClampPitch(120f, -80f, 80f));
            Assert.AreEqual(-CameraMath.PitchLimit, CameraMath.ClampPitch(-500f, -500f, 500f));
            Assert.AreEqual(10f, CameraMath.ClampPitch(0f, 10f, 5f));
            Assert.Less(CameraMath.Direction(0f, -30f).y, 0f);
        }
    }

    [TestFixture]
    public class ShapeTests
    {
        [TestCase(PieceShape.Stone)]
        [TestCase(PieceShape.Disc)]
        [TestCase(PieceShape.Cube)]
        [TestCase(PieceShape.Box)]
        [TestCase(PieceShape.Sphere)]
        [TestCase(PieceShape.Cylinder)]
        [TestCase(PieceShape.Cone)]
        public void EveryShapeIsASolidOfItsNaturalSize(PieceShape shape)
        {
            Vector3 size = BoardShapes.NaturalSize(shape);
            foreach (bool centered in new[] { false, true })
            {
                MeshBuilder mesh = BoardShapes.Piece(shape, centered);
                GeometryAssert.Oriented(mesh);
                GeometryAssert.Closed(mesh);
                Vector3 min = mesh.VertexList[0];
                Vector3 max = min;
                foreach (Vector3 v in mesh.VertexList)
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
                GeometryAssert.Near(size, max - min, 1e-3f, shape.ToString());
                Assert.AreEqual(centered ? -size.y * 0.5f : 0f, min.y, 1e-4f);
                GeometryAssert.Outward(mesh, new Vector3(0f, (min.y + max.y) * 0.5f, 0f));
            }
        }

        [Test]
        public void ShapeNamesAreRead()
        {
            foreach (PieceShape shape in Enum.GetValues(typeof(PieceShape)))
            {
                Assert.IsTrue(BoardShapes.TryParseShape(shape.ToString().ToUpperInvariant(), out PieceShape parsed));
                Assert.AreEqual(shape, parsed);
            }
            Assert.IsFalse(BoardShapes.TryParseShape("banana", out _));
            Assert.IsFalse(BoardShapes.TryParseShape(null, out _));
            Assert.IsTrue(BoardShapes.IsRound(PieceShape.Sphere) && BoardShapes.IsRound(PieceShape.Stone) && !BoardShapes.IsRound(PieceShape.Box));
        }

        [Test]
        public void StickersSitJustAboveTheirSides()
        {
            Color32 body = new Color32(10, 10, 10, 255);
            Color32?[] stickers = { new Color32(255, 0, 0, 255), null, null, new Color32(0, 255, 0, 255), null, null };
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddStickerBox(Vector3.zero, new Vector3(2f, 2f, 2f), body, stickers, 0.1f, 0.01f);
            GeometryAssert.Oriented(mesh);
            Assert.AreEqual(24 + 8, mesh.VertexCount);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Color32 c = mesh.ColorList[i];
                Vector3 v = mesh.VertexList[i];
                if (c.r == 255)
                {
                    Assert.AreEqual(1.01f, v.x, 1e-5f);
                    Assert.AreEqual(0.8f, Mathf.Abs(v.y), 1e-5f);
                    Assert.AreEqual(0.8f, Mathf.Abs(v.z), 1e-5f);
                }
                else if (c.g == 255)
                {
                    Assert.AreEqual(-1.01f, v.y, 1e-5f);
                }
            }
            MeshBuilder plain = new MeshBuilder();
            plain.AddStickerBox(Vector3.zero, Vector3.one, body, null, 0.1f, 0.01f);
            Assert.AreEqual(24, plain.VertexCount);
        }
    }
}
