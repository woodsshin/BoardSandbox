namespace BoardSandbox.Core.App
{
    // Shortens text that comes from a mod before a front end puts it on screen.
    public static class DisplayText
    {
        // At most max characters; a longer text ends in an ellipsis. With singleLine, line breaks become spaces.
        public static string Clip(string text, int max, bool singleLine = true)
        {
            if (string.IsNullOrEmpty(text) || max < 1)
                return string.Empty;
            string value = singleLine ? text.Replace('\r', ' ').Replace('\n', ' ') : text;
            if (value.Length <= max)
                return value;
            int cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
                cut--;
            return value.Substring(0, cut) + "…";
        }
    }
}
