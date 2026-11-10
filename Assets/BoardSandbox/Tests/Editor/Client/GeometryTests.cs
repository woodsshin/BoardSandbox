using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using NUnit.Framework;
using UnityEngine;

namespace BoardSandbox.Tests
{
    internal static class GeometryAssert
    {
        public static void Near(Vector3 expected, Vector3 actual, float tolerance, string message = "")
        {
            Assert.IsTrue((expected - actual).magnitude <= tolerance, message + " expected " + expected + " but was " + actual);
        }

        public static void Oriented(MeshBuilder mesh)
        {
            List<Vector3> v = mesh.VertexList;
            List<Vector3> n = mesh.NormalList;
            List<int> t = mesh.TriangleList;
            Assert.AreEqual(0, t.Count % 3);
            for (int i = 0; i < t.Count; i += 3)
            {
                Vector3 face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                Vector3 hint = n[t[i]] + n[t[i + 1]] + n[t[i + 2]];
                Assert.Greater(Vector3.Dot(face, hint), 0f, "triangle " + i / 3 + " faces against its normals");
            }
        }

        // Every edge is shared by exactly two triangles that run along it in opposite directions.
        public static void Closed(MeshBuilder mesh)
        {
            List<Vector3> v = mesh.VertexList;
            List<int> t = mesh.TriangleList;
            Dictionary<string, int> directed = new Dictionary<string, int>();
            for (int i = 0; i < t.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    string from = Key(v[t[i + k]]);
                    string to = Key(v[t[i + (k + 1) % 3]]);
                    if (from == to)
                        continue;
                    string edge = from + ">" + to;
                    directed.TryGetValue(edge, out int count);
                    directed[edge] = count + 1;
                }
            }
            foreach (KeyValuePair<string, int> pair in directed)
            {
                Assert.AreEqual(1, pair.Value, "edge " + pair.Key + " is used more than once in the same direction");
                string[] ends = pair.Key.Split('>');
                Assert.IsTrue(directed.ContainsKey(ends[1] + ">" + ends[0]), "edge " + pair.Key + " has no partner");
            }
        }

        // For a convex solid every face points away from a point inside it.
        public static void Outward(MeshBuilder mesh, Vector3 inside)
        {
            List<Vector3> v = mesh.VertexList;
            List<int> t = mesh.TriangleList;
            for (int i = 0; i < t.Count; i += 3)
            {
                Vector3 a = v[t[i]];
                Vector3 b = v[t[i + 1]];
                Vector3 c = v[t[i + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a);
                Vector3 centroid = (a + b + c) / 3f;
                Assert.Greater(Vector3.Dot(face, centroid - inside), 0f, "triangle " + i / 3 + " points into the solid");
            }
        }

        public static void Bounds(MeshBuilder mesh, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (Vector3 p in mesh.VertexList)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        private static string Key(Vector3 p)
        {
            return (long)Math.Round(p.x * 10000.0) + "," + (long)Math.Round(p.y * 10000.0) + "," + (long)Math.Round(p.z * 10000.0);
        }
    }

    [TestFixture]
    public class BoardLayoutTests
    {
        [Test]
        public void OddBoardsAreCenteredOnACoordinate()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            GeometryAssert.Near(Vector3.zero, layout.CellToWorld(8, 8), 1e-5f);
            GeometryAssert.Near(new Vector3(-7f, 0f, 7f), layout.CellToWorld(1, 1), 1e-5f);
            GeometryAssert.Near(new Vector3(7f, 0f, -7f), layout.CellToWorld(15, 15), 1e-5f);
        }

        [Test]
        public void EvenBoardsAreCenteredBetweenCoordinates()
        {
            BoardLayout layout = new BoardLayout(8, 8, BoardStyle.Checker);
            GeometryAssert.Near(Vector3.zero, layout.CellToWorld(4.5, 4.5), 1e-5f);
            GeometryAssert.Near(new Vector3(-3.5f, 0f, 3.5f), layout.CellToWorld(1, 1), 1e-5f);
        }

        [Test]
        public void ColumnOneIsLeftAndRowOneIsFarAway()
        {
            BoardLayout layout = new BoardLayout(9, 7);
            Assert.Less(layout.CellToWorld(1, 4).x, layout.CellToWorld(9, 4).x);
            Assert.Greater(layout.CellToWorld(5, 1).z, layout.CellToWorld(5, 7).z);
        }

        [Test]
        public void CellSizeScalesTheSpacing()
        {
            BoardLayout layout = new BoardLayout(15, 15, BoardStyle.Lines, 2f);
            Assert.AreEqual(2f, layout.CellToWorld(6, 3).x - layout.CellToWorld(5, 3).x, 1e-5);
            Assert.AreEqual(2f, layout.CellToWorld(6, 3).z - layout.CellToWorld(6, 4).z, 1e-5);
            Assert.AreEqual(2f, layout.CellSize, 1e-6);
        }

        [Test]
        public void HeightIsCarriedThrough()
        {
            BoardLayout layout = new BoardLayout(5, 5);
            Assert.AreEqual(0.75f, layout.CellToWorld(2, 2, 0.75f).y, 1e-6);
        }

        [Test]
        public void WorldToCellInvertsCellToWorld()
        {
            BoardLayout layout = new BoardLayout(19, 11, BoardStyle.Lines, 1.5f);
            double[] xs = { 1, 2.5, 9.25, 19 };
            double[] ys = { 1, 3.75, 6, 11 };
            foreach (double x in xs)
            {
                foreach (double y in ys)
                {
                    Vector2 back = layout.WorldToCell(layout.CellToWorld(x, y));
                    Assert.AreEqual(x, back.x, 1e-4, "x of " + x + "," + y);
                    Assert.AreEqual(y, back.y, 1e-4, "y of " + x + "," + y);
                }
            }
        }

        [Test]
        public void SizesAreClamped()
        {
            BoardLayout small = new BoardLayout(0, -5);
            Assert.AreEqual(1, small.Columns);
            Assert.AreEqual(1, small.Rows);
            BoardLayout large = new BoardLayout(1000, 65);
            Assert.AreEqual(BoardLayout.MaxSize, large.Columns);
            Assert.AreEqual(BoardLayout.MaxSize, large.Rows);
            Assert.IsTrue(new BoardLayout(3, 3, BoardStyle.Lines, 0f).CellSize > 0f);
        }

        [Test]
        public void ContainsMatchesTheCoordinateRange()
        {
            BoardLayout layout = new BoardLayout(6, 4);
            Assert.IsTrue(layout.Contains(1, 1));
            Assert.IsTrue(layout.Contains(6, 4));
            Assert.IsFalse(layout.Contains(0, 1));
            Assert.IsFalse(layout.Contains(7, 1));
            Assert.IsFalse(layout.Contains(1, 5));
            Assert.IsFalse(layout.Contains(-1, -1));
        }

        [Test]
        public void ARayFromAbovePicksTheCellUnderIt()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            int[] cells = { 1, 2, 8, 14, 15 };
            foreach (int cx in cells)
            {
                foreach (int cy in cells)
                {
                    Vector3 center = layout.CellToWorld(cx, cy);
                    Ray ray = new Ray(center + new Vector3(0.3f, 10f, -0.2f), Vector3.down);
                    Assert.IsTrue(layout.TryPick(ray, out int x, out int y), cx + "," + cy);
                    Assert.AreEqual(cx, x);
                    Assert.AreEqual(cy, y);
                }
            }
        }

        [Test]
        public void ARayPicksTheNearestCoordinate()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            Vector3 center = layout.CellToWorld(5, 5);
            Assert.IsTrue(layout.TryPick(new Ray(center + new Vector3(0.6f, 10f, 0f), Vector3.down), out int x, out int y));
            Assert.AreEqual(6, x);
            Assert.AreEqual(5, y);
            Assert.IsTrue(layout.TryPick(new Ray(center + new Vector3(0f, 10f, 0.6f), Vector3.down), out x, out y));
            Assert.AreEqual(5, x);
            Assert.AreEqual(4, y);
        }

        [Test]
        public void ARayPastTheEdgePicksNothing()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            Vector3 corner = layout.CellToWorld(15, 15);
            Assert.IsTrue(layout.TryPick(new Ray(corner + new Vector3(0.4f, 10f, -0.4f), Vector3.down), out int x, out int y));
            Assert.AreEqual(15, x);
            Assert.AreEqual(15, y);
            Assert.IsFalse(layout.TryPick(new Ray(corner + new Vector3(0.6f, 10f, 0f), Vector3.down), out x, out y));
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);
        }

        [Test]
        public void RaysThatMissTheTablePickNothing()
        {
            BoardLayout layout = new BoardLayout(9, 9);
            Assert.IsFalse(layout.TryPick(new Ray(new Vector3(0f, 5f, 0f), Vector3.up), out _, out _));
            Assert.IsFalse(layout.TryPick(new Ray(new Vector3(0f, 5f, 0f), Vector3.right), out _, out _));
        }

        [Test]
        public void ARayAtAnAnglePicksWhereItMeetsTheTable()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            Vector3 target = layout.CellToWorld(10, 4);
            Vector3 origin = new Vector3(-3f, 8f, -12f);
            Ray ray = new Ray(origin, (target - origin).normalized);
            Assert.IsTrue(layout.TryPick(ray, out int x, out int y));
            Assert.AreEqual(10, x);
            Assert.AreEqual(4, y);
        }

        [Test]
        public void SlabCoversTheCoordinatesWithAMargin()
        {
            BoardLayout lines = new BoardLayout(15, 15);
            Assert.AreEqual(7.8f, lines.SlabHalfWidth, 1e-5);
            Assert.AreEqual(7.8f, lines.SlabHalfDepth, 1e-5);
            BoardLayout checker = new BoardLayout(8, 8, BoardStyle.Checker);
            Assert.AreEqual(4.4f, checker.SlabHalfWidth, 1e-5);
        }

        [TestCase(40f, 1.78f, 0f, 55f)]
        [TestCase(40f, 1.0f, 0f, 90f)]
        [TestCase(60f, 0.5f, 0f, 35f)]
        [TestCase(30f, 2.4f, 37f, 60f)]
        [TestCase(60f, 1.33f, 215f, 20f)]
        public void FitDistanceKeepsTheWholeBoardInView(float fov, float aspect, float yaw, float pitch)
        {
            BoardLayout[] layouts =
            {
                new BoardLayout(15, 15),
                new BoardLayout(19, 19),
                new BoardLayout(8, 8, BoardStyle.Checker),
                new BoardLayout(30, 9),
                new BoardLayout(9, 30)
            };
            foreach (BoardLayout layout in layouts)
            {
                foreach (float padding in new[] { 1f, 1.15f })
                {
                    float distance = layout.FitDistance(fov, aspect, yaw, pitch, padding);
                    float widest = WidestRatio(layout, distance, fov, aspect, yaw, pitch);
                    Assert.AreEqual(1f / padding, widest, 2e-3, layout.Columns + "x" + layout.Rows + " padding " + padding);
                }
            }
        }

        [Test]
        public void FitDistanceGrowsWithTheBoard()
        {
            float small = new BoardLayout(9, 9).FitDistance(40f, 1.78f, 0f, 55f);
            float large = new BoardLayout(19, 19).FitDistance(40f, 1.78f, 0f, 55f);
            Assert.Greater(large, small);
        }

        [Test]
        public void NarrowScreensNeedMoreDistance()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            Assert.Greater(layout.FitDistance(40f, 0.5f, 0f, 55f), layout.FitDistance(40f, 1.78f, 0f, 55f));
        }

        // The largest share of the half screen that any corner of the slab and the pieces on it takes.
        private static float WidestRatio(BoardLayout layout, float distance, float fov, float aspect, float yaw, float pitch)
        {
            float tanY = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * aspect;
            CameraMath.Basis(yaw, pitch, out Vector3 right, out Vector3 up, out Vector3 forward);
            Vector3 camera = CameraMath.Position(Vector3.zero, yaw, pitch, distance);
            float widest = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? -layout.SlabHalfWidth : layout.SlabHalfWidth,
                    (i & 2) == 0 ? -layout.Thickness : layout.CellSize * 0.5f,
                    (i & 4) == 0 ? -layout.SlabHalfDepth : layout.SlabHalfDepth);
                Vector3 view = corner - camera;
                float z = Vector3.Dot(view, forward);
                Assert.Greater(z, 0f, "a corner is behind the camera");
                widest = Mathf.Max(widest, Mathf.Abs(Vector3.Dot(view, right)) / (z * tanX));
                widest = Mathf.Max(widest, Mathf.Abs(Vector3.Dot(view, up)) / (z * tanY));
            }
            return widest;
        }
    }

    [TestFixture]
    public class CameraMathTests
    {
        [TestCase(0f, 45f)]
        [TestCase(90f, 30f)]
        [TestCase(213f, 70f)]
        [TestCase(-40f, 12f)]
        public void DirectionIsAUnitVectorAboveTheTable(float yaw, float pitch)
        {
            Vector3 direction = CameraMath.Direction(yaw, pitch);
            Assert.AreEqual(1f, direction.magnitude, 1e-5);
            Assert.Greater(direction.y, 0f);
        }

        [Test]
        public void YawZeroPutsTheCameraOnTheNearSide()
        {
            Vector3 direction = CameraMath.Direction(0f, 45f);
            Assert.Less(direction.z, 0f);
            Assert.AreEqual(0f, direction.x, 1e-6);
            GeometryAssert.Near(new Vector3(0f, 1f, 0f), CameraMath.Direction(0f, 90f), 1e-5f);
            GeometryAssert.Near(new Vector3(1f, 0f, 0f), CameraMath.Direction(90f, 0f), 1e-5f);
        }

        [Test]
        public void PositionIsTheDistanceAwayFromTheTarget()
        {
            Vector3 target = new Vector3(2f, 0f, -3f);
            Vector3 position = CameraMath.Position(target, 120f, 40f, 17f);
            Assert.AreEqual(17f, (position - target).magnitude, 1e-4);
        }

        [TestCase(0f, 45f)]
        [TestCase(75f, 20f)]
        [TestCase(200f, 88f)]
        public void BasisIsOrthonormalAndRightSideUp(float yaw, float pitch)
        {
            CameraMath.Basis(yaw, pitch, out Vector3 right, out Vector3 up, out Vector3 forward);
            Assert.AreEqual(1f, right.magnitude, 1e-5);
            Assert.AreEqual(1f, up.magnitude, 1e-5);
            Assert.AreEqual(1f, forward.magnitude, 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(right, up), 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(right, forward), 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(up, forward), 1e-5);
            Assert.Greater(up.y, 0f);
            GeometryAssert.Near(-CameraMath.Direction(yaw, pitch), forward, 1e-5f);
            GeometryAssert.Near(up, Vector3.Cross(forward, right), 1e-5f);
        }

        [Test]
        public void GroundAxesPointRightAndAway()
        {
            CameraMath.GroundAxes(0f, out Vector3 right, out Vector3 forward);
            GeometryAssert.Near(Vector3.right, right, 1e-6f);
            GeometryAssert.Near(Vector3.forward, forward, 1e-6f);
            CameraMath.GroundAxes(90f, out right, out forward);
            GeometryAssert.Near(Vector3.forward, right, 1e-5f);
            GeometryAssert.Near(Vector3.left, forward, 1e-5f);
        }

        [Test]
        public void PitchStaysInRange()
        {
            Assert.AreEqual(CameraMath.MinPitch, CameraMath.ClampPitch(-20f), 1e-6);
            Assert.AreEqual(CameraMath.MaxPitch, CameraMath.ClampPitch(120f), 1e-6);
            Assert.AreEqual(45f, CameraMath.ClampPitch(45f), 1e-6);
        }

        [TestCase(-10f, 350f)]
        [TestCase(370f, 10f)]
        [TestCase(720f, 0f)]
        [TestCase(0f, 0f)]
        [TestCase(359.5f, 359.5f)]
        public void YawWrapsIntoOneTurn(float input, float expected)
        {
            Assert.AreEqual(expected, CameraMath.WrapDegrees(input), 1e-4);
        }

        [Test]
        public void DraggingTheMouseMovesTheTableWithIt()
        {
            Vector3 right = CameraMath.PanOffset(0f, 90f, new Vector2(10f, 0f), 10f, 60f, 1000f);
            Assert.Less(right.x, 0f);
            Assert.AreEqual(0f, right.z, 1e-6);
            Vector3 down = CameraMath.PanOffset(0f, 90f, new Vector2(0f, 10f), 10f, 60f, 1000f);
            Assert.Greater(down.z, 0f);
            Assert.AreEqual(0f, down.x, 1e-6);
            Assert.AreEqual(0f, down.y, 1e-6);
        }

        [Test]
        public void PanFollowsTheCursorAtTheTargetDepth()
        {
            float unitsPerPixel = 2f * 10f * Mathf.Tan(30f * Mathf.Deg2Rad) / 1000f;
            Vector3 flat = CameraMath.PanOffset(0f, 90f, new Vector2(0f, 100f), 10f, 60f, 1000f);
            Assert.AreEqual(100f * unitsPerPixel, flat.z, 1e-4);
            Vector3 tilted = CameraMath.PanOffset(0f, 30f, new Vector2(0f, 100f), 10f, 60f, 1000f);
            Assert.AreEqual(200f * unitsPerPixel, tilted.z, 1e-4);
        }

        [Test]
        public void PanRotatesWithTheCamera()
        {
            Vector3 offset = CameraMath.PanOffset(90f, 90f, new Vector2(10f, 0f), 10f, 60f, 1000f);
            Assert.Less(offset.z, 0f);
            Assert.AreEqual(0f, offset.x, 1e-5);
        }

        [Test]
        public void ZoomChangesTheDistanceByATenthPerNotch()
        {
            Assert.AreEqual(11f, CameraMath.Zoom(10f, 1f, 1f, 100f), 1e-4);
            Assert.AreEqual(10f / 1.1f, CameraMath.Zoom(10f, -1f, 1f, 100f), 1e-4);
            Assert.AreEqual(10f, CameraMath.Zoom(10f, 0f, 1f, 100f), 1e-6);
            Assert.AreEqual(100f, CameraMath.Zoom(10f, 100f, 1f, 100f), 1e-6);
            Assert.AreEqual(1f, CameraMath.Zoom(10f, -100f, 1f, 100f), 1e-6);
        }

        [Test]
        public void DampingDoesNotDependOnTheFrameRate()
        {
            float once = CameraMath.Damp(0f, 1f, 5f, 1f);
            float twice = CameraMath.Damp(CameraMath.Damp(0f, 1f, 5f, 0.5f), 1f, 5f, 0.5f);
            float many = 0f;
            for (int i = 0; i < 100; i++)
                many = CameraMath.Damp(many, 1f, 5f, 0.01f);
            Assert.AreEqual(once, twice, 1e-5);
            Assert.AreEqual(once, many, 1e-4);
            Assert.AreEqual(1f - Mathf.Exp(-5f), once, 1e-5);
        }

        [Test]
        public void DampingStandsStillWithoutTimeOrSpeed()
        {
            Assert.AreEqual(3f, CameraMath.Damp(3f, 9f, 5f, 0f), 1e-6);
            Assert.AreEqual(3f, CameraMath.Damp(3f, 9f, 0f, 1f), 1e-6);
            GeometryAssert.Near(new Vector3(1f, 2f, 3f), CameraMath.Damp(new Vector3(1f, 2f, 3f), Vector3.zero, 5f, 0f), 1e-6f);
            GeometryAssert.Near(new Vector3(0.5f, 0.5f, 0.5f), CameraMath.Damp(Vector3.zero, Vector3.one, 1f, Mathf.Log(2f)), 1e-5f);
        }

        [Test]
        public void AnglesTakeTheShortWayRound()
        {
            float step = CameraMath.DampAngle(350f, 10f, 1f, Mathf.Log(2f));
            Assert.AreEqual(0f, CameraMath.WrapDegrees(step), 1e-3);
            float back = CameraMath.DampAngle(10f, 350f, 1f, Mathf.Log(2f));
            Assert.AreEqual(0f, CameraMath.WrapDegrees(back), 1e-3);
        }
    }

    [TestFixture]
    public class ColorParserTests
    {
        private static void AssertColor(string text, int r, int g, int b, int a)
        {
            Assert.IsTrue(ColorParser.TryParse(text, out Color32 color), text);
            Assert.AreEqual(r, (int)color.r, text + " r");
            Assert.AreEqual(g, (int)color.g, text + " g");
            Assert.AreEqual(b, (int)color.b, text + " b");
            Assert.AreEqual(a, (int)color.a, text + " a");
        }

        [Test]
        public void ParsesTheHexForms()
        {
            AssertColor("#fff", 255, 255, 255, 255);
            AssertColor("#f00", 255, 0, 0, 255);
            AssertColor("#f008", 255, 0, 0, 136);
            AssertColor("#112233", 17, 34, 51, 255);
            AssertColor("#11223344", 17, 34, 51, 68);
            AssertColor("#AbCdEf", 171, 205, 239, 255);
            AssertColor("#161616", 22, 22, 22, 255);
            AssertColor("  #f3efe6 ", 243, 239, 230, 255);
        }

        [Test]
        public void ParsesNamesIgnoringCase()
        {
            Assert.IsTrue(ColorParser.TryParse("red", out Color32 red));
            Assert.IsTrue(red.r > red.g && red.r > red.b);
            Assert.IsTrue(ColorParser.TryParse("RED", out Color32 shouting));
            Assert.AreEqual((int)red.r, (int)shouting.r);
            Assert.IsTrue(ColorParser.TryParse("Grey", out Color32 grey));
            Assert.IsTrue(ColorParser.TryParse("gray", out Color32 gray));
            Assert.AreEqual((int)grey.g, (int)gray.g);
            Assert.IsTrue(ColorParser.TryParse("black", out Color32 black));
            Assert.IsTrue(black.r < 64 && black.g < 64 && black.b < 64);
            Assert.IsTrue(ColorParser.TryParse("white", out Color32 white));
            Assert.IsTrue(white.r > 192 && white.g > 192 && white.b > 192);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("#")]
        [TestCase("#1")]
        [TestCase("#12")]
        [TestCase("#12345")]
        [TestCase("#1234567")]
        [TestCase("#123456789")]
        [TestCase("#ggg")]
        [TestCase("#12345g")]
        [TestCase("# 123")]
        [TestCase("#+123")]
        [TestCase("#-1-2")]
        [TestCase("123456")]
        [TestCase("notacolor")]
        public void RejectsMalformedText(string text)
        {
            Assert.IsFalse(ColorParser.TryParse(text, out Color32 color), text ?? "null");
            Assert.AreEqual(255, (int)color.r);
            Assert.AreEqual(255, (int)color.g);
            Assert.AreEqual(255, (int)color.b);
            Assert.AreEqual(255, (int)color.a);
        }

        [Test]
        public void ParseFallsBackWhenTheTextIsNotAColor()
        {
            Color32 fallback = new Color32(1, 2, 3, 4);
            Color32 result = ColorParser.Parse("nope", fallback);
            Assert.AreEqual(1, (int)result.r);
            Assert.AreEqual(4, (int)result.a);
            Assert.AreEqual(17, (int)ColorParser.Parse("#112233", fallback).r);
        }
    }

    [TestFixture]
    public class MeshBuilderTests
    {
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        [Test]
        public void AQuadFacesTheSideOfItsNormalWhicheverWayRound()
        {
            Vector3 a = new Vector3(0f, 0f, 0f);
            Vector3 b = new Vector3(0f, 0f, 1f);
            Vector3 c = new Vector3(1f, 0f, 1f);
            Vector3 d = new Vector3(1f, 0f, 0f);
            MeshBuilder up = new MeshBuilder();
            up.AddQuad(a, b, c, d, Vector3.up, White);
            MeshBuilder down = new MeshBuilder();
            down.AddQuad(a, b, c, d, Vector3.down, White);
            Assert.AreEqual(2, up.TriangleCount);
            Assert.AreEqual(2, down.TriangleCount);
            GeometryAssert.Oriented(up);
            GeometryAssert.Oriented(down);
            Assert.Greater(FaceNormal(up, 0).y, 0f);
            Assert.Less(FaceNormal(down, 0).y, 0f);
        }

        [Test]
        public void TheFrontOfATriangleIsWhereTheCrossProductPoints()
        {
            MeshBuilder mesh = new MeshBuilder();
            int a = mesh.AddVertex(new Vector3(0f, 0f, 0f), Vector3.back, White);
            int b = mesh.AddVertex(new Vector3(0f, 1f, 0f), Vector3.back, White);
            int c = mesh.AddVertex(new Vector3(1f, 0f, 0f), Vector3.back, White);
            mesh.AddTriangle(a, b, c);
            mesh.AddTriangle(a, c, b);
            Assert.AreEqual(2, mesh.TriangleCount);
            GeometryAssert.Near(Vector3.back, FaceNormal(mesh, 0).normalized, 1e-5f);
            GeometryAssert.Near(Vector3.back, FaceNormal(mesh, 1).normalized, 1e-5f);
        }

        [Test]
        public void TrianglesWithoutAreaAreDropped()
        {
            MeshBuilder mesh = new MeshBuilder();
            int a = mesh.AddVertex(new Vector3(0f, 0f, 0f), Vector3.up, White);
            int b = mesh.AddVertex(new Vector3(1f, 0f, 0f), Vector3.up, White);
            int c = mesh.AddVertex(new Vector3(2f, 0f, 0f), Vector3.up, White);
            int d = mesh.AddVertex(new Vector3(0f, 0f, 0f), Vector3.up, White);
            mesh.AddTriangle(a, b, c);
            mesh.AddTriangle(a, d, b);
            Assert.AreEqual(0, mesh.TriangleCount);
            Assert.AreEqual(4, mesh.VertexCount);
        }

        [Test]
        public void NormalsAreStoredAsUnitVectors()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddVertex(Vector3.zero, new Vector3(0f, 5f, 0f), White);
            Assert.AreEqual(1f, mesh.NormalList[0].magnitude, 1e-6);
            Assert.AreEqual(1, mesh.ColorList.Count);
        }

        [Test]
        public void ABoxIsAClosedOutwardFacingSolid()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddBox(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f), White);
            Assert.AreEqual(24, mesh.VertexCount);
            Assert.AreEqual(12, mesh.TriangleCount);
            GeometryAssert.Oriented(mesh);
            GeometryAssert.Closed(mesh);
            GeometryAssert.Outward(mesh, new Vector3(1f, 2f, 3f));
            GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
            GeometryAssert.Near(new Vector3(0f, 0f, 0f), min, 1e-5f);
            GeometryAssert.Near(new Vector3(2f, 4f, 6f), max, 1e-5f);
        }

        [Test]
        public void ADiscFacesTheChosenWay()
        {
            MeshBuilder up = new MeshBuilder();
            up.AddDisc(new Vector3(0f, 1f, 0f), 2f, 24, White);
            MeshBuilder down = new MeshBuilder();
            down.AddDisc(new Vector3(0f, 1f, 0f), 2f, 24, White, false);
            Assert.AreEqual(24, up.TriangleCount);
            GeometryAssert.Oriented(up);
            GeometryAssert.Oriented(down);
            for (int i = 0; i < 24; i++)
            {
                Assert.Greater(FaceNormal(up, i).y, 0f);
                Assert.Less(FaceNormal(down, i).y, 0f);
            }
            GeometryAssert.Bounds(up, out Vector3 min, out Vector3 max);
            Assert.AreEqual(-2f, min.x, 1e-5);
            Assert.AreEqual(2f, max.z, 1e-5);
            Assert.AreEqual(1f, min.y, 1e-6);
        }

        [Test]
        public void ARingLeavesTheMiddleOpen()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddRing(Vector3.zero, 1f, 2f, 32, White);
            Assert.AreEqual(64, mesh.TriangleCount);
            GeometryAssert.Oriented(mesh);
            for (int i = 0; i < mesh.TriangleCount; i++)
                Assert.Greater(FaceNormal(mesh, i).y, 0f);
            foreach (Vector3 p in mesh.VertexList)
            {
                float radius = new Vector2(p.x, p.z).magnitude;
                Assert.IsTrue(radius > 0.999f && radius < 2.001f, "radius " + radius);
            }
        }

        [Test]
        public void ACylinderIsAClosedOutwardFacingSolid()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddCylinder(new Vector3(1f, 0f, 1f), 0.5f, 2f, 16, White);
            GeometryAssert.Oriented(mesh);
            GeometryAssert.Closed(mesh);
            GeometryAssert.Outward(mesh, new Vector3(1f, 1f, 1f));
            GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
            GeometryAssert.Near(new Vector3(0.5f, 0f, 0.5f), min, 1e-5f);
            GeometryAssert.Near(new Vector3(1.5f, 2f, 1.5f), max, 1e-5f);
        }

        [Test]
        public void AStoneIsAClosedOutwardFacingSolidStandingOnItsBase()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddStone(new Vector3(3f, 0f, -1f), 0.5f, 0.3f, 24, 10, White);
            GeometryAssert.Oriented(mesh);
            GeometryAssert.Closed(mesh);
            GeometryAssert.Outward(mesh, new Vector3(3f, 0.15f, -1f));
            GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
            Assert.AreEqual(0f, min.y, 1e-5);
            Assert.AreEqual(0.3f, max.y, 1e-5);
            Assert.AreEqual(2.5f, min.x, 1e-4);
            Assert.AreEqual(3.5f, max.x, 1e-4);
        }

        [Test]
        public void StoneNormalsFollowTheSurface()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddStone(Vector3.zero, 0.5f, 0.3f, 24, 10, White);
            Vector3 center = new Vector3(0f, 0.15f, 0f);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3 p = mesh.VertexList[i] - center;
                Vector3 expected = new Vector3(p.x / (0.5f * 0.5f), p.y / (0.15f * 0.15f), p.z / (0.5f * 0.5f)).normalized;
                GeometryAssert.Near(expected, mesh.NormalList[i], 1e-3f, "vertex " + i);
            }
        }

        [Test]
        public void SegmentCountsHaveALowerLimit()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddDisc(Vector3.zero, 1f, 1, White);
            Assert.AreEqual(6, mesh.TriangleCount);
            MeshBuilder stone = new MeshBuilder();
            stone.AddStone(Vector3.zero, 1f, 0.5f, 2, 1, White);
            GeometryAssert.Closed(stone);
        }

        [Test]
        public void IndicesStayWithinTheVertexList()
        {
            MeshBuilder mesh = new MeshBuilder();
            mesh.AddBox(Vector3.zero, Vector3.one, White);
            mesh.AddCylinder(Vector3.zero, 1f, 1f, 12, White);
            mesh.AddStone(Vector3.zero, 1f, 0.4f, 12, 6, White);
            foreach (int index in mesh.TriangleList)
                Assert.IsTrue(index >= 0 && index < mesh.VertexCount);
            Assert.AreEqual(mesh.VertexCount, mesh.NormalList.Count);
            Assert.AreEqual(mesh.VertexCount, mesh.ColorList.Count);
        }

        private static Vector3 FaceNormal(MeshBuilder mesh, int triangle)
        {
            List<int> t = mesh.TriangleList;
            List<Vector3> v = mesh.VertexList;
            Vector3 a = v[t[triangle * 3]];
            return Vector3.Cross(v[t[triangle * 3 + 1]] - a, v[t[triangle * 3 + 2]] - a);
        }
    }

    [TestFixture]
    public class BoardShapesTests
    {
        [TestCase(PieceShape.Stone)]
        [TestCase(PieceShape.Disc)]
        [TestCase(PieceShape.Cube)]
        public void PiecesAreClosedSolidsStandingOnTheOrigin(PieceShape shape)
        {
            MeshBuilder mesh = BoardShapes.Piece(shape);
            GeometryAssert.Oriented(mesh);
            GeometryAssert.Closed(mesh);
            float height = BoardShapes.PieceHeight(shape);
            GeometryAssert.Outward(mesh, new Vector3(0f, height * 0.5f, 0f));
            GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
            Assert.AreEqual(0f, min.y, 1e-4);
            Assert.AreEqual(height, max.y, 1e-4);
        }

        [TestCase(PieceShape.Stone)]
        [TestCase(PieceShape.Disc)]
        [TestCase(PieceShape.Cube)]
        public void PiecesFitInsideOneCell(PieceShape shape)
        {
            GeometryAssert.Bounds(BoardShapes.Piece(shape), out Vector3 min, out Vector3 max);
            Assert.IsTrue(min.x > -0.5f && max.x < 0.5f);
            Assert.IsTrue(min.z > -0.5f && max.z < 0.5f);
        }

        [Test]
        public void PiecesAreWhiteSoTheMaterialDecidesTheColor()
        {
            foreach (Color32 color in BoardShapes.Piece(PieceShape.Stone).ColorList)
            {
                Assert.AreEqual(255, (int)color.r);
                Assert.AreEqual(255, (int)color.g);
                Assert.AreEqual(255, (int)color.b);
            }
        }

        [Test]
        public void MarkerAndHighlightLieFlatAndFaceUp()
        {
            foreach (MeshBuilder mesh in new[] { BoardShapes.Marker(), BoardShapes.Highlight() })
            {
                GeometryAssert.Oriented(mesh);
                GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
                Assert.AreEqual(0f, min.y, 1e-6);
                Assert.AreEqual(0f, max.y, 1e-6);
                for (int i = 0; i < mesh.TriangleCount; i++)
                {
                    Vector3 a = mesh.VertexList[mesh.TriangleList[i * 3]];
                    Vector3 face = Vector3.Cross(mesh.VertexList[mesh.TriangleList[i * 3 + 1]] - a, mesh.VertexList[mesh.TriangleList[i * 3 + 2]] - a);
                    Assert.Greater(face.y, 0f);
                }
            }
        }

        [Test]
        public void StarPointsOfCommonBoards()
        {
            AssertPoints(BoardShapes.StarPoints(19, 19), "4,4 4,16 16,4 16,16 10,10");
            AssertPoints(BoardShapes.StarPoints(15, 15), "4,4 4,12 12,4 12,12 8,8");
            AssertPoints(BoardShapes.StarPoints(13, 13), "4,4 4,10 10,4 10,10 7,7");
            AssertPoints(BoardShapes.StarPoints(9, 9), "3,3 3,7 7,3 7,7 5,5");
            AssertPoints(BoardShapes.StarPoints(19, 9), "4,3 4,7 16,3 16,7 10,5");
        }

        [TestCase(8, 8)]
        [TestCase(7, 7)]
        [TestCase(19, 8)]
        [TestCase(3, 3)]
        public void BoardsWithoutACenterHaveNoStarPoints(int columns, int rows)
        {
            Assert.AreEqual(0, BoardShapes.StarPoints(columns, rows).Count);
        }

        [Test]
        public void LineBoardsDrawEveryLineAndStar()
        {
            BoardLayout layout = new BoardLayout(15, 15);
            MeshBuilder mesh = BoardShapes.Board(layout);
            Assert.AreEqual(12 + 2 * (15 + 15) + 20 * 5, mesh.TriangleCount);
            GeometryAssert.Oriented(mesh);
        }

        [Test]
        public void CheckerBoardsDrawEverySquare()
        {
            BoardLayout layout = new BoardLayout(8, 8, BoardStyle.Checker);
            MeshBuilder mesh = BoardShapes.Board(layout);
            Assert.AreEqual(12 + 2 * 64, mesh.TriangleCount);
            GeometryAssert.Oriented(mesh);
            HashSet<int> shades = new HashSet<int>();
            foreach (Color32 color in mesh.ColorList)
                shades.Add(color.r * 65536 + color.g * 256 + color.b);
            Assert.AreEqual(3, shades.Count);
        }

        [TestCase(BoardStyle.Lines)]
        [TestCase(BoardStyle.Checker)]
        public void TheBoardStaysWithinItsSlabAndRestsOnTheTable(BoardStyle style)
        {
            BoardLayout layout = new BoardLayout(11, 7, style, 1.25f);
            MeshBuilder mesh = BoardShapes.Board(layout);
            GeometryAssert.Bounds(mesh, out Vector3 min, out Vector3 max);
            Assert.AreEqual(-layout.SlabHalfWidth, min.x, 1e-4);
            Assert.AreEqual(layout.SlabHalfWidth, max.x, 1e-4);
            Assert.AreEqual(-layout.SlabHalfDepth, min.z, 1e-4);
            Assert.AreEqual(layout.SlabHalfDepth, max.z, 1e-4);
            Assert.AreEqual(-layout.Thickness, min.y, 1e-5);
            Assert.IsTrue(max.y > 0f && max.y < 0.02f * layout.CellSize + 0.02f, "surface decoration height " + max.y);
        }

        [Test]
        public void LinesRunThroughTheCoordinates()
        {
            BoardLayout layout = new BoardLayout(9, 9);
            MeshBuilder mesh = BoardShapes.Board(layout);
            foreach (int column in new[] { 1, 5, 9 })
            {
                float x = layout.CellToWorld(column, 1).x;
                bool found = false;
                foreach (Vector3 p in mesh.VertexList)
                {
                    if (p.y > 0f && p.y < 0.006f && Mathf.Abs(p.x - x) < layout.CellSize * 0.05f)
                    {
                        found = true;
                        break;
                    }
                }
                Assert.IsTrue(found, "no line at column " + column);
            }
        }

        [Test]
        public void PiecesRestAboveTheLines()
        {
            Assert.Greater(BoardShapes.RestHeight, 0.008f);
        }

        private static void AssertPoints(List<Vector2Int> actual, string expected)
        {
            List<string> text = new List<string>();
            foreach (Vector2Int p in actual)
                text.Add(p.x + "," + p.y);
            Assert.AreEqual(expected, string.Join(" ", text));
        }
    }
}
