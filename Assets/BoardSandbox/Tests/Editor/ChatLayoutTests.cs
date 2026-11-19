using BoardSandbox.Core.App;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    // Where the chat of a room goes on the screen: beside the table on a wide screen, under it on a tall one.
    [TestFixture]
    public class ChatLayoutTests
    {
        private const float Margin = 12f;
        private const float Tolerance = 0.01f;

        private static ChatPlacement Place(float width, float height, float scale = 1f, bool available = true, float rightEdge = 0f, float reserved = 0f,
            bool folded = false)
        {
            return ChatLayout.Place(width, height, scale, Margin, available, rightEdge > 0f ? rightEdge : width, reserved, folded);
        }

        [Test]
        public void NothingIsShownWhereThereIsNothingToShow()
        {
            ChatPlacement none = Place(1920f, 1080f, 1f, false);
            Assert.AreEqual(ChatDock.None, none.Dock);
            Assert.AreEqual(0f, none.Width);
            Assert.AreEqual(0f, none.Height);
            Assert.AreEqual(0f, none.RightFraction);
            Assert.AreEqual(0f, none.BottomFraction);

            // A screen that is no screen gives nothing either, and no number that is not one.
            Assert.AreEqual(ChatDock.None, Place(0f, 1080f).Dock);
            Assert.AreEqual(ChatDock.None, Place(1920f, 0f).Dock);
            Assert.AreEqual(ChatDock.None, Place(-5f, 1080f).Dock);
            Assert.AreEqual(ChatDock.None, Place(float.NaN, 1080f).Dock);
            Assert.AreEqual(ChatDock.None, Place(1920f, float.NaN).Dock);
            Assert.AreEqual(ChatDock.None, Place(float.PositiveInfinity, 1080f).Dock);
            Assert.AreEqual(ChatDock.None, Place(1920f, 1080f, 0f).Dock);
            Assert.AreEqual(ChatDock.None, Place(1920f, 1080f, float.NaN).Dock);
            ChatPlacement odd = ChatLayout.Place(1080f, 2400f, 2.5f, float.NaN, true, float.NaN, float.NaN);
            Assert.AreEqual(ChatDock.Bottom, odd.Dock, "numbers that are not numbers are left out of the sum");
            Assert.IsFalse(float.IsNaN(odd.X) || float.IsNaN(odd.Y) || float.IsNaN(odd.Width) || float.IsNaN(odd.Height) || float.IsNaN(odd.BottomFraction));
        }

        [Test]
        public void OnAWideScreenTheChatIsAColumnAtTheRightEdge()
        {
            ChatPlacement chat = Place(1920f, 1080f);
            Assert.AreEqual(ChatDock.Right, chat.Dock);
            Assert.AreEqual(380f, chat.Width, Tolerance, "no wider than a column needs to be");
            Assert.AreEqual(1920f - 380f - Margin, chat.X, Tolerance);
            Assert.AreEqual(Margin, chat.Y, Tolerance);
            Assert.AreEqual(1080f - 2f * Margin, chat.Height, Tolerance);
            Assert.AreEqual((380f + 1.5f * Margin) / 1920f, chat.RightFraction, 1e-5f);
            Assert.AreEqual(0f, chat.BottomFraction);

            // On a smaller screen it takes its share, and not less than a column needs.
            ChatPlacement middle = Place(1200f, 800f);
            Assert.AreEqual(ChatDock.Right, middle.Dock);
            Assert.AreEqual(1200f * ChatLayout.ColumnShare, middle.Width, Tolerance);
            ChatPlacement small = Place(700f, 500f);
            Assert.AreEqual(ChatDock.Right, small.Dock);
            Assert.AreEqual(ChatLayout.ColumnMin, small.Width, Tolerance);
        }

        [Test]
        public void OnAPhoneHeldUprightTheChatIsAStripUnderTheTable()
        {
            ChatPlacement chat = Place(1080f, 2400f, 2.5f);
            Assert.AreEqual(ChatDock.Bottom, chat.Dock);
            Assert.AreEqual(Margin, chat.X, Tolerance);
            Assert.AreEqual(1080f - 2f * Margin, chat.Width, Tolerance, "all the width there is");
            Assert.AreEqual(2400f * ChatLayout.StripShare, chat.Height, Tolerance, "a share of the height, which the scale of the interface keeps within bounds");
            Assert.AreEqual(2400f - chat.Height - Margin, chat.Y, Tolerance);
            Assert.AreEqual((2400f - chat.Y + Margin * 0.5f) / 2400f, chat.BottomFraction, 1e-5f);
            Assert.AreEqual(0f, chat.RightFraction);

            // A tall screen of a computer: the strip does not grow with the height.
            ChatPlacement tall = Place(600f, 1600f);
            Assert.AreEqual(ChatDock.Bottom, tall.Dock);
            Assert.AreEqual(ChatLayout.StripMax, tall.Height, Tolerance);
            ChatPlacement short1 = Place(600f, 700f);
            Assert.AreEqual(ChatDock.Bottom, short1.Dock);
            Assert.AreEqual(700f * ChatLayout.StripShare, short1.Height, Tolerance);
        }

        [Test]
        public void OnAPhoneHeldSidewaysTheChatIsAColumnAgain()
        {
            ChatPlacement chat = Place(2400f, 1080f, 2.5f);
            Assert.AreEqual(ChatDock.Right, chat.Dock);
            Assert.AreEqual(650f, chat.Width, Tolerance);
            Assert.AreEqual(2400f - 650f - Margin, chat.X, Tolerance);
        }

        [Test]
        public void AColumnThatWouldLeaveTheTableTooLittleBecomesAStrip()
        {
            // A column of 260 and two margins: the table keeps half the width from 568 on.
            Assert.AreEqual(ChatDock.Right, Place(570f, 400f).Dock);
            ChatPlacement narrow = Place(560f, 400f);
            Assert.AreEqual(ChatDock.Bottom, narrow.Dock);
            Assert.AreEqual(560f - 2f * Margin, narrow.Width, Tolerance);

            // A screen as high as it is wide counts as a wide one.
            Assert.AreEqual(ChatDock.Right, Place(1000f, 1000f).Dock);
            Assert.AreEqual(ChatDock.Bottom, Place(1000f, 1001f).Dock);
        }

        [Test]
        public void WithTheAiConsoleAtTheRightTheChatStandsUnderTheTableBesideIt()
        {
            float edge = 1920f - 640f - Margin;
            ChatPlacement chat = Place(1920f, 1080f, 1f, true, edge);
            Assert.AreEqual(ChatDock.Bottom, chat.Dock);
            Assert.AreEqual(Margin, chat.X, Tolerance);
            Assert.AreEqual(edge - 2f * Margin, chat.Width, Tolerance);
            Assert.IsTrue(chat.X + chat.Width <= edge, "not over the console");
            Assert.AreEqual(1080f * ChatLayout.StripShare, chat.Height, Tolerance);
            Assert.AreEqual(0f, chat.RightFraction, "the console is what keeps the table out of the right part");

            // Where the console takes nearly all the width there is no room for the chat.
            Assert.AreEqual(ChatDock.None, Place(1080f, 2400f, 2.5f, true, 200f).Dock);
            Assert.AreEqual(ChatDock.Bottom, Place(1080f, 2400f, 2.5f, true, 1000f).Dock);
        }

        [Test]
        public void TheConsoleOfCommandsKeepsTheChatAboveIt()
        {
            float console = 240f * 2.5f + Margin;
            ChatPlacement chat = Place(1080f, 2400f, 2.5f, true, 0f, console);
            Assert.AreEqual(ChatDock.Bottom, chat.Dock);
            Assert.AreEqual(2400f - 2400f * ChatLayout.StripShare - Margin - console, chat.Y, Tolerance);
            Assert.AreEqual((2400f - chat.Y + Margin * 0.5f) / 2400f, chat.BottomFraction, 1e-5f);

            // The table is never pushed out of more than a share of the screen.
            ChatPlacement crowded = Place(1080f, 2400f, 2.5f, true, 0f, 2300f);
            Assert.AreEqual(ChatDock.Bottom, crowded.Dock);
            Assert.IsTrue(crowded.Height > 0f);
            Assert.AreEqual(0.7f, crowded.BottomFraction, 1e-5f);
            Assert.AreEqual(ChatDock.None, Place(1080f, 2400f, 2.5f, true, 0f, 2400f).Dock, "no room left at all");
        }

        [Test]
        public void FoldedAwayTheChatIsATabInTheCornerWhereItStood()
        {
            // Beside the table: at the top of where the column was, at its right end.
            ChatPlacement tab = Place(1920f, 1080f, 1f, true, 0f, 0f, true);
            Assert.AreEqual(ChatDock.Tab, tab.Dock);
            Assert.AreEqual(ChatLayout.TabWidth, tab.Width, Tolerance);
            Assert.AreEqual(ChatLayout.TabHeight, tab.Height, Tolerance);
            Assert.AreEqual(1920f - Margin - ChatLayout.TabWidth, tab.X, Tolerance);
            Assert.AreEqual(Margin, tab.Y, Tolerance);
            Assert.AreEqual(0f, tab.RightFraction, "a tab takes no room from the table");
            Assert.AreEqual(0f, tab.BottomFraction);

            // Under the table: at the bottom right, and as big as the interface is.
            ChatPlacement phone = Place(1080f, 2400f, 2.5f, true, 0f, 0f, true);
            Assert.AreEqual(ChatDock.Tab, phone.Dock);
            Assert.AreEqual(ChatLayout.TabWidth * 2.5f, phone.Width, Tolerance);
            Assert.AreEqual(ChatLayout.TabHeight * 2.5f, phone.Height, Tolerance);
            Assert.AreEqual(1080f - Margin - phone.Width, phone.X, Tolerance);
            Assert.AreEqual(2400f - Margin - phone.Height, phone.Y, Tolerance);
            Assert.AreEqual(0f, phone.RightFraction);
            Assert.AreEqual(0f, phone.BottomFraction);

            // Shown again, the window is back and the table gives up its room.
            Assert.AreEqual(ChatDock.Right, Place(1920f, 1080f, 1f, true, 0f, 0f, false).Dock);
        }

        [Test]
        public void TheTabStaysClearOfTheConsolesAndIsNotWhereThereIsNoChat()
        {
            float edge = 1920f - 640f - Margin;
            ChatPlacement besideConsole = Place(1920f, 1080f, 1f, true, edge, 0f, true);
            Assert.AreEqual(ChatDock.Tab, besideConsole.Dock);
            Assert.AreEqual(edge - Margin, besideConsole.X + besideConsole.Width, Tolerance, "left of the AI console");
            Assert.AreEqual(1080f - Margin - ChatLayout.TabHeight, besideConsole.Y, Tolerance);

            float console = 240f * 2.5f + Margin;
            ChatPlacement aboveConsole = Place(1080f, 2400f, 2.5f, true, 0f, console, true);
            Assert.AreEqual(ChatDock.Tab, aboveConsole.Dock);
            Assert.AreEqual(2400f - Margin - console, aboveConsole.Y + aboveConsole.Height, Tolerance, "above the console of commands");

            // Where the chat has no place its tab has none either, and nothing is shown for a screen or a room that is none.
            Assert.AreEqual(ChatDock.None, Place(1080f, 2400f, 2.5f, true, 200f, 0f, true).Dock);
            Assert.AreEqual(ChatDock.None, Place(1920f, 1080f, 1f, false, 0f, 0f, true).Dock);
            Assert.AreEqual(ChatDock.None, Place(0f, 1080f, 1f, true, 0f, 0f, true).Dock);
            Assert.AreEqual(ChatDock.None, Place(float.NaN, 1080f, 1f, true, 0f, 0f, true).Dock);
        }

        [Test]
        public void TheTabIsThereWheneverTheChatIsAndStaysWhereItDoesNotGetInTheWay()
        {
            float[] widths = { 320f, 480f, 568f, 640f, 800f, 1024f, 1080f, 1280f, 1920f, 2400f, 3840f };
            float[] heights = { 320f, 480f, 600f, 800f, 1080f, 1600f, 2400f };
            float[] scales = { 0.75f, 1f, 1.5f, 2f, 2.5f, 3f };
            float[] edges = { 0f, 0.3f, 0.5f, 0.8f };
            float[] reserved = { 0f, 100f, 250f, 600f };
            int tabs = 0;
            foreach (float width in widths)
            {
                foreach (float height in heights)
                {
                    foreach (float scale in scales)
                    {
                        foreach (float edgeShare in edges)
                        {
                            foreach (float below in reserved)
                            {
                                float edge = edgeShare <= 0f ? width : width * edgeShare;
                                string where = width + "x" + height + " scale " + scale + " edge " + edge + " reserved " + below;
                                ChatPlacement window = ChatLayout.Place(width, height, scale, Margin, true, edge, below, false);
                                ChatPlacement tab = ChatLayout.Place(width, height, scale, Margin, true, edge, below, true);
                                if (window.Dock == ChatDock.None)
                                {
                                    Assert.AreEqual(ChatDock.None, tab.Dock, where);
                                    continue;
                                }
                                tabs++;
                                Assert.AreEqual(ChatDock.Tab, tab.Dock, where);
                                Assert.AreEqual(0f, tab.RightFraction, where);
                                Assert.AreEqual(0f, tab.BottomFraction, where);
                                Assert.IsTrue(tab.Width > 0f && tab.Height > 0f, where);
                                Assert.IsTrue(tab.Width <= ChatLayout.TabWidth * scale + Tolerance && tab.Height <= ChatLayout.TabHeight * scale + Tolerance, where);
                                Assert.IsTrue(tab.X >= Margin - Tolerance && tab.Y >= Margin - Tolerance, where);
                                Assert.IsTrue(tab.X + tab.Width <= edge - Margin + Tolerance, "not over the console, " + where);
                                if (window.Dock == ChatDock.Right)
                                    Assert.AreEqual(Margin, tab.Y, Tolerance, "at the top of the column, " + where);
                                else
                                    Assert.IsTrue(tab.Y + tab.Height <= height - below - Margin + Tolerance, "above the console of commands, " + where);
                            }
                        }
                    }
                }
            }
            Assert.Greater(tabs, 500, "the sweep has to reach the cases it is about");
        }

        [Test]
        public void ThePlaceStaysOnTheScreenAndOutOfTheWayOnEveryScreen()
        {
            float[] widths = { 320f, 480f, 568f, 640f, 800f, 1024f, 1080f, 1280f, 1920f, 2400f, 3840f };
            float[] heights = { 320f, 480f, 600f, 800f, 1080f, 1600f, 2400f };
            float[] scales = { 0.75f, 1f, 1.5f, 2f, 2.5f, 3f };
            float[] edges = { 0f, 0.3f, 0.5f, 0.8f };
            float[] reserved = { 0f, 100f, 600f };
            int shown = 0;
            foreach (float width in widths)
            {
                foreach (float height in heights)
                {
                    foreach (float scale in scales)
                    {
                        foreach (float edgeShare in edges)
                        {
                            foreach (float below in reserved)
                            {
                                float edge = edgeShare <= 0f ? width : width * edgeShare;
                                string where = width + "x" + height + " scale " + scale + " edge " + edge + " reserved " + below;
                                ChatPlacement chat = ChatLayout.Place(width, height, scale, Margin, true, edge, below);
                                if (chat.Dock == ChatDock.None)
                                {
                                    Assert.AreEqual(0f, chat.RightFraction, where);
                                    Assert.AreEqual(0f, chat.BottomFraction, where);
                                    continue;
                                }
                                shown++;
                                Assert.IsTrue(chat.Width > 0f && chat.Height > 0f, where);
                                Assert.IsTrue(chat.X >= 0f && chat.Y >= 0f, where);
                                Assert.IsTrue(chat.X + chat.Width <= edge + Tolerance, "not over the console, " + where);
                                Assert.IsTrue(chat.Y + chat.Height <= height + Tolerance, where);
                                Assert.IsTrue(chat.RightFraction >= 0f && chat.RightFraction <= 0.7f, where);
                                Assert.IsTrue(chat.BottomFraction >= 0f && chat.BottomFraction <= 0.7f, where);
                                if (chat.Dock == ChatDock.Right)
                                {
                                    Assert.IsTrue(width >= height && edge >= width, "a column only on a wide screen with nothing at the right, " + where);
                                    Assert.AreEqual(0f, chat.BottomFraction, where);
                                    Assert.IsTrue(width - chat.Width - 2f * Margin >= width * ChatLayout.TableShare - Tolerance, "the table keeps its share, " + where);
                                    Assert.IsTrue(chat.Width >= ChatLayout.ColumnMin * scale - Tolerance && chat.Width <= ChatLayout.ColumnMax * scale + Tolerance, where);
                                    Assert.IsTrue(width * (1f - chat.RightFraction) <= chat.X + Tolerance, "the table ends before the column, " + where);
                                }
                                else
                                {
                                    Assert.AreEqual(0f, chat.RightFraction, where);
                                    Assert.IsTrue(chat.Y + chat.Height <= height - below + Tolerance, "above the console of commands, " + where);
                                    Assert.IsTrue(chat.Height <= height * 0.5f + Tolerance, where);
                                    if (chat.BottomFraction < 0.7f)
                                        Assert.IsTrue(height * (1f - chat.BottomFraction) <= chat.Y + Tolerance, "the table ends before the strip, " + where);
                                }
                            }
                        }
                    }
                }
            }
            Assert.Greater(shown, 500, "the sweep has to reach the cases it is about");
        }
    }
}
