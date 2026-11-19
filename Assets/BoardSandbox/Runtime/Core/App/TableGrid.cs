using System;

namespace BoardSandbox.Core.App
{
    // How the tables of a room share the screen: a grid, the first table at the top left, in what the AI console and the chat leave free.
    public static class TableGrid
    {
        // The part of the screen for table index of count tables, as shares of the screen with the origin at its lower left corner, which
        // is where the cameras of Unity have it. dockRight is the share of the width, from the right edge, and dockBottom the share of the
        // height, from the bottom edge, that is not for the tables.
        public static void Cell(int index, int count, float dockRight, float dockBottom, out float x, out float y, out float width, out float height)
        {
            count = Math.Max(1, count);
            index = Math.Max(0, Math.Min(count - 1, index));
            float free = 1f - Clamp01(dockRight);
            float high = 1f - Clamp01(dockBottom);
            int columns = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling(count / (double)columns);
            x = index % columns / (float)columns * free;
            y = Clamp01(dockBottom) + (1f - (index / columns + 1) / (float)rows) * high;
            width = free / columns;
            height = high / rows;
        }

        private static float Clamp01(float value)
        {
            return value > 0f ? (value < 1f ? value : 1f) : 0f;
        }
    }
}
