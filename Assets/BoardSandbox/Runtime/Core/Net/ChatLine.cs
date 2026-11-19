using System;
using System.Text;

namespace BoardSandbox.Core.Net
{
    // One line of what the players of a room say to each other.
    public sealed class ChatLine
    {
        public ChatLine(int number, ulong from, string name, string text)
        {
            Number = number;
            From = from;
            Name = name ?? string.Empty;
            Text = text ?? string.Empty;
        }

        // Counts the lines this side has seen from 1, so that a line is still known after older ones have been dropped. A player who
        // comes late counts from the first line they see.
        public int Number { get; }

        // The player who said it, and the name they had then.
        public ulong From { get; }
        public string Name { get; }

        public string Text { get; }
    }

    // What may be said in a room. The host cleans what the players send and a guest cleans what the host passes on, so that
    // neither has to trust the other with what is drawn on the screen.
    public static class ChatText
    {
        public const int MaxLength = 200;

        // Line breaks, tabs and other blanks become a single space, control characters and the characters that turn text around or
        // hide it are left out, and so are halves of a surrogate pair. No blanks at the ends, and not longer than MaxLength (a
        // pair is never cut in two). Empty when nothing is left to say.
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            StringBuilder clean = new StringBuilder(Math.Min(text.Length, MaxLength));
            for (int i = 0; i < text.Length && clean.Length < MaxLength; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                        continue;
                    if (clean.Length + 2 > MaxLength)
                        break;
                    clean.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c))
                    continue;
                if (char.IsWhiteSpace(c))
                {
                    if (clean.Length == 0 || clean[clean.Length - 1] == ' ')
                        continue;
                    c = ' ';
                }
                else if (char.IsControl(c) || IsHidden(c))
                {
                    continue;
                }
                clean.Append(c);
            }
            return clean.ToString().TrimEnd(' ');
        }

        // The marks that change the direction of the text around them, and the blanks that cannot be seen.
        private static bool IsHidden(char c)
        {
            return c == '\u061c' || c == '\u200b' || c == '\u200e' || c == '\u200f'
                || (c >= '\u202a' && c <= '\u202e') || c == '\u2060' || (c >= '\u2066' && c <= '\u2069') || c == '\ufeff';
        }
    }

    // Keeps one player from filling the screens of the others: at most Limit lines in any Window seconds.
    public sealed class ChatLimiter
    {
        public const int Limit = 5;
        public const double Window = 5.0;

        private readonly double[] _times = new double[Limit];
        private int _count;
        private int _oldest;
        private double _warnedAt = double.NegativeInfinity;

        // Whether a line said at this time may go through; a line that does is counted.
        public bool Allow(double now)
        {
            if (_count < Limit)
            {
                _times[(_oldest + _count) % Limit] = now;
                _count++;
                return true;
            }
            if (now - _times[_oldest] < Window)
                return false;
            _times[_oldest] = now;
            _oldest = (_oldest + 1) % Limit;
            return true;
        }

        // True once in a Window, for the line that has to be left out: the player is told, but not for every line.
        public bool ShouldWarn(double now)
        {
            if (now - _warnedAt < Window)
                return false;
            _warnedAt = now;
            return true;
        }
    }
}
