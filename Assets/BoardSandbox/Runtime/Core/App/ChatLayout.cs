using System;

namespace BoardSandbox.Core.App
{
    public enum ChatDock
    {
        // Not shown: there is no room to talk in, or a list covers the screen.
        None,

        // A column at the right edge, beside the table.
        Right,

        // A strip along the bottom, under the table.
        Bottom,

        // Folded away: all that is left of the chat is a small tab over a corner of the table, which takes no room from it. The tab
        // is where the chat has its button for folding it, so that it is found again.
        Tab
    }

    // The part of the screen that the chat takes. Sizes are in the pixels of the GUI, the origin is the top left corner. A struct, as it is
    // worked out many times in a frame.
    public readonly struct ChatPlacement
    {
        public static readonly ChatPlacement None = new ChatPlacement(ChatDock.None, 0f, 0f, 0f, 0f, 0f, 0f);

        public ChatPlacement(ChatDock dock, float x, float y, float width, float height, float rightFraction, float bottomFraction)
        {
            Dock = dock;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            RightFraction = rightFraction;
            BottomFraction = bottomFraction;
        }

        public ChatDock Dock { get; }

        // The window of the chat, or its tab when it is folded away.
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        // How much of the width of the screen, from its right edge, and of the height, from its bottom edge, the table keeps out
        // of so that the chat does not cover it. Both are 0 where the chat is not, and where it is folded to a tab.
        public float RightFraction { get; }
        public float BottomFraction { get; }
    }

    // Where the chat of a room goes: in a column at the right edge of the screen, which is where there is room for it beside
    // the table, and in a strip along the bottom of a screen that is taller than it is wide (a phone held upright) or too narrow
    // to give up a column.
    public static class ChatLayout
    {
        // The sizes in design pixels, which the scale of the interface makes bigger on a screen with many pixels per inch.
        public const float ColumnMin = 260f;
        public const float ColumnMax = 380f;
        public const float StripMin = 150f;
        public const float StripMax = 260f;
        public const float StripMinWidth = 160f;

        // The tab that a chat folded away leaves.
        public const float TabWidth = 140f;
        public const float TabHeight = 34f;

        // The share of the screen that the column and the strip like to take.
        public const float ColumnShare = 0.24f;
        public const float StripShare = 0.24f;

        // The table keeps at least this share of the width of the screen beside a column, or the chat goes below it instead.
        public const float TableShare = 0.5f;

        // The table is never pushed out of more of the screen than this, and the strip is no taller than this share of it.
        private const float MostTaken = 0.7f;
        private const float StripMostOfHeight = 0.5f;

        // width and height: the screen. scale: the scale of the interface. margin: the gap kept around windows.
        // available: whether there is a chat to show, with nothing over the screen.
        // rightEdge: where a window that already stands at the right edge, the AI console, begins; the screen's width for none.
        // bottomReserved: the height that something at the bottom already takes, the console of commands; the chat stands above it.
        // folded: the player put the chat away. What is left is a tab in the corner where the chat stood, if there is a place
        // for the chat at all: a screen that has no room for it has none for its tab either.
        public static ChatPlacement Place(float width, float height, float scale, float margin, bool available, float rightEdge, float bottomReserved,
            bool folded = false)
        {
            if (!available || !(width > 0f) || !(height > 0f) || !(scale > 0f) || float.IsInfinity(width) || float.IsInfinity(height))
                return ChatPlacement.None;
            margin = margin > 0f && !float.IsInfinity(margin) ? margin : 0f;
            float reserved = bottomReserved > 0f && !float.IsInfinity(bottomReserved) ? bottomReserved : 0f;
            float free = rightEdge > 0f && rightEdge < width ? rightEdge : width;

            ChatPlacement window = Window(width, height, scale, margin, free, reserved);
            return folded && window.Dock != ChatDock.None ? Fold(height, scale, margin, free, reserved, window.Dock) : window;
        }

        // The window the chat has when it is shown.
        private static ChatPlacement Window(float width, float height, float scale, float margin, float free, float reserved)
        {
            bool beside = free >= width;

            if (beside && width >= height)
            {
                float column = Math.Min(Math.Max(width * ColumnShare, ColumnMin * scale), ColumnMax * scale);
                if (width - column - 2f * margin >= width * TableShare)
                {
                    float x = width - column - margin;
                    float tall = height - 2f * margin;
                    if (tall > 0f)
                        return new ChatPlacement(ChatDock.Right, x, margin, column, tall, Math.Min(MostTaken, (width - x + margin * 0.5f) / width), 0f);
                }
            }

            float strip = Math.Min(Math.Max(height * StripShare, StripMin * scale), StripMax * scale);
            strip = Math.Min(strip, Math.Min(height * StripMostOfHeight, height - reserved - 2f * margin));
            float wide = free - 2f * margin;
            if (!(strip > 0f) || !(wide >= StripMinWidth * scale))
                return ChatPlacement.None;
            float top = height - strip - margin - reserved;
            return new ChatPlacement(ChatDock.Bottom, margin, top, wide, strip, 0f, Math.Min(MostTaken, (height - top + margin * 0.5f) / height));
        }

        // The tab of a chat that has been folded away, where its window had its button: at the top of the column, at the bottom
        // of the strip, and at the right end of either. The window was there, so there is room for a tab of the size that fits.
        private static ChatPlacement Fold(float height, float scale, float margin, float free, float reserved, ChatDock was)
        {
            float width = Math.Min(TabWidth * scale, free - 2f * margin);
            float room = was == ChatDock.Right ? height - 2f * margin : height - reserved - 2f * margin;
            float tall = Math.Min(TabHeight * scale, room);
            float x = free - margin - width;
            float y = was == ChatDock.Right ? margin : height - margin - reserved - tall;
            return new ChatPlacement(ChatDock.Tab, x, y, width, tall, 0f, 0f);
        }
    }
}
