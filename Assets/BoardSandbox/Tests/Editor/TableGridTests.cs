using System;
using BoardSandbox.Core.App;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // How the tables of a room share the screen, in what the AI console and the chat leave free.
    [TestFixture]
    public class TableGridTests
    {
        private const float Tolerance = 1e-5f;

        private struct Cell
        {
            public float X;
            public float Y;
            public float Width;
            public float Height;
        }

        private static Cell Of(int index, int count, float right = 0f, float bottom = 0f)
        {
            Cell cell = new Cell();
            TableGrid.Cell(index, count, right, bottom, out cell.X, out cell.Y, out cell.Width, out cell.Height);
            return cell;
        }

        private static void AreCell(Cell cell, float x, float y, float width, float height, string where)
        {
            Assert.AreEqual(x, cell.X, Tolerance, where + " x");
            Assert.AreEqual(y, cell.Y, Tolerance, where + " y");
            Assert.AreEqual(width, cell.Width, Tolerance, where + " width");
            Assert.AreEqual(height, cell.Height, Tolerance, where + " height");
        }

        [Test]
        public void TheTablesFillTheScreenInAGridWithTheFirstOneAtTheTopLeft()
        {
            AreCell(Of(0, 1), 0f, 0f, 1f, 1f, "one table");
            AreCell(Of(0, 2), 0f, 0f, 0.5f, 1f, "first of two");
            AreCell(Of(1, 2), 0.5f, 0f, 0.5f, 1f, "second of two");
            AreCell(Of(0, 4), 0f, 0.5f, 0.5f, 0.5f, "first of four");
            AreCell(Of(1, 4), 0.5f, 0.5f, 0.5f, 0.5f, "second of four");
            AreCell(Of(2, 4), 0f, 0f, 0.5f, 0.5f, "third of four");
            AreCell(Of(3, 4), 0.5f, 0f, 0.5f, 0.5f, "fourth of four");
            AreCell(Of(2, 3), 0f, 0f, 0.5f, 0.5f, "third of three: the grid has a place left over");
            AreCell(Of(4, 5), 1f / 3f, 0f, 1f / 3f, 0.5f, "fifth of five");
        }

        [Test]
        public void WithoutADockTheGridIsTheOneItWasBefore()
        {
            for (int count = 1; count <= 16; count++)
            {
                int columns = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling(count / (double)columns);
                foreach (float right in new[] { 0f, 0.25f, 0.36f })
                {
                    float free = 1f - right;
                    for (int i = 0; i < count; i++)
                    {
                        Cell was = new Cell { X = i % columns / (float)columns * free, Y = 1f - (i / columns + 1) / (float)rows, Width = free / columns, Height = 1f / rows };
                        AreCell(Of(i, count, right, 0f), was.X, was.Y, was.Width, was.Height, i + " of " + count + " right " + right);
                    }
                }
            }
        }

        [Test]
        public void TheTablesKeepOutOfTheDocksAndDoNotCoverOneAnother()
        {
            foreach (float right in new[] { 0f, 0.2f, 0.4f, 0.7f })
            {
                foreach (float bottom in new[] { 0f, 0.1f, 0.3f, 0.7f })
                {
                    for (int count = 1; count <= 16; count++)
                    {
                        string where = count + " tables, right " + right + ", bottom " + bottom;
                        int columns = (int)Math.Ceiling(Math.Sqrt(count));
                        int rows = (int)Math.Ceiling(count / (double)columns);
                        double area = 0;
                        for (int i = 0; i < count; i++)
                        {
                            Cell a = Of(i, count, right, bottom);
                            Assert.IsTrue(a.Width > 0f && a.Height > 0f, where);
                            Assert.IsTrue(a.X >= -Tolerance && a.X + a.Width <= 1f - right + Tolerance, "not under the column at the right, " + where);
                            Assert.IsTrue(a.Y >= bottom - Tolerance && a.Y + a.Height <= 1f + Tolerance, "not over the strip at the bottom, " + where);
                            area += a.Width * a.Height;
                            for (int j = i + 1; j < count; j++)
                            {
                                Cell b = Of(j, count, right, bottom);
                                float across = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                                float down = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                                Assert.IsFalse(across > Tolerance && down > Tolerance, "tables " + i + " and " + j + " overlap, " + where);
                            }
                        }
                        double whole = (1.0 - right) * (1.0 - bottom) * count / (columns * rows);
                        Assert.AreEqual(whole, area, 1e-4, "the tables use all there is of the screen, " + where);
                    }
                }
            }
        }

        [Test]
        public void ADockAtTheBottomMovesTheWholeGridUp()
        {
            Cell top = Of(0, 4, 0f, 0.25f);
            AreCell(top, 0f, 0.25f + 0.375f, 0.5f, 0.375f, "top left of four");
            Cell low = Of(2, 4, 0f, 0.25f);
            AreCell(low, 0f, 0.25f, 0.5f, 0.375f, "bottom left of four");
            AreCell(Of(0, 1, 0.2f, 0.3f), 0f, 0.3f, 0.8f, 0.7f, "one table in what is left");
        }

        [Test]
        public void OddNumbersGiveACellAndNoNumberThatIsNone()
        {
            AreCell(Of(0, 0), 0f, 0f, 1f, 1f, "no tables count as one");
            AreCell(Of(-3, 1), 0f, 0f, 1f, 1f, "an index below the first is the first");
            AreCell(Of(9, 2), 0.5f, 0f, 0.5f, 1f, "an index past the last is the last");
            Cell docked = Of(0, 1, float.NaN, float.NaN);
            AreCell(docked, 0f, 0f, 1f, 1f, "a dock that is no number is none");
            Cell all = Of(0, 1, 5f, 5f);
            Assert.IsFalse(float.IsNaN(all.X) || float.IsNaN(all.Y) || float.IsNaN(all.Width) || float.IsNaN(all.Height));
            Assert.AreEqual(0f, all.Width, Tolerance, "a dock of more than the whole screen leaves nothing");
            Assert.AreEqual(0f, all.Height, Tolerance);
        }
    }
}
