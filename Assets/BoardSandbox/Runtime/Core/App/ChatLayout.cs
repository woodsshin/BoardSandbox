using System;

namespace BoardSandbox.Core.App
{
    public enum ChatDock
    {
        // Not shown: there is no room to talk in, the panels are folded away, or a list covers the screen.
        None,

        // A column at the right edge, beside the table.
        Right,

        // A strip along the bottom, under the table.
        Bottom
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

        // The window of the chat.
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        // How much of the width of the screen, from its right edge, and of the height, from its bottom edge, the table keeps out
        // of so that the chat does not cover it. Both are 0 where the chat is not.
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

        // The share of the screen that the column and the strip like to take.
        public const float ColumnShare = 0.24f;
        public const float StripShare = 0.24f;

        // The table keeps at least this share of the width of the screen beside a column, or the chat goes below it instead.
        public const float TableShare = 0.5f;

        // The table is never pushed out of more of the screen than this, and the strip is no taller than this share of it.
        private const float MostTaken = 0.7f;
        private const float StripMostOfHeight = 0.5f;

        // width and height: the screen. scale: the scale of the interface. margin: the gap kept around windows.
        // available: whether there is a chat to show, with nothing over the screen and the panels not folded away.
        // rightEdge: where a window that already stands at the right edge, the AI console, begins; the screen's width for none.
        // bottomReserved: the height that something at the bottom already takes, the console of commands; the chat stands above it.
        public static ChatPlacement Place(float width, float height, float scale, float margin, bool available, float rightEdge, float bottomReserved)
        {
            if (!available || !(width > 0f) || !(height > 0f) || !(scale > 0f) || float.IsInfinity(width) || float.IsInfinity(height))
                return ChatPlacement.None;
            margin = margin > 0f && !float.IsInfinity(margin) ? margin : 0f;
            float reserved = bottomReserved > 0f && !float.IsInfinity(bottomReserved) ? bottomReserved : 0f;
            float free = rightEdge > 0f && rightEdge < width ? rightEdge : width;
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
    }
}
