using System.Text;

namespace BoardSandbox.Core.Net
{
    // Online services keep the text of a room (its name, the game it plays) in fields of a limited length, counted in bytes.
    public static class ServiceText
    {
        // The start of the text that fits in the given number of bytes (as UTF-8), without cutting a character in two.
        public static string ClipUtf8(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || maxBytes <= 0)
                return string.Empty;
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
                return text;

            int bytes = 0;
            int end = 0;
            while (end < text.Length)
            {
                // A character outside the first plane is two chars of the string, and is not cut apart either.
                int step = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(text.Substring(end, step));
                if (bytes + size > maxBytes)
                    break;
                bytes += size;
                end += step;
            }
            return text.Substring(0, end);
        }
    }
}
