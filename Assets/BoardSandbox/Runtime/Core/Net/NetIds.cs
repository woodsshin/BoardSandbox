using System.Text;

namespace BoardSandbox.Core.Net
{
    // A room names its people and the rooms people find with numbers, while some online services name them with text, such as
    // the product user id of Epic Online Services. This turns such a text into a number. Every device that is given the same
    // text arrives at the same number, so they all agree on who is who without asking each other, and the number tells nothing
    // that the text did not.
    public static class NetIds
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // 0 stands for nobody: it is what no text (null or empty) comes to, and no other text does. Capital letters make no
        // difference, the ids of such services are hexadecimal.
        public static ulong FromText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            ulong hash = Offset;
            unchecked
            {
                foreach (byte b in Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
                {
                    hash ^= b;
                    hash *= Prime;
                }
            }
            return hash == 0 ? 1UL : hash;
        }
    }
}
