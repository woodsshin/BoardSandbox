using System;
using System.Collections.Generic;
using BoardSandbox.Core.Net;

namespace BoardSandbox.Core.App
{
    // Looks after what is said in a room while the chat is put away, so that it is not missed: the lines of the others come up as
    // notices, the newest few of them, and are counted until the chat is shown again. The player's own lines are not news to them.
    public sealed class ChatWatch
    {
        // How many of the lines that come in at once are shown as notices; the rest are only counted.
        public const int MaxNotices = 3;
        public const int MaxNameLength = 24;

        private NetRoom _room;
        private int _seen;

        // How many lines of the others came in since the chat was last shown.
        public int Unread { get; private set; }

        // Call it once per frame. shown: whether the chat is on the screen, where the lines are read as they come.
        public void Update(NetRoom room, bool shown, ToastQueue notices, double now)
        {
            if (!ReferenceEquals(room, _room))
            {
                // Another room has another talk, and what was said in it before the player came is not news.
                _room = room;
                _seen = room != null ? room.ChatTotal : 0;
                Unread = 0;
            }
            if (room == null)
                return;
            if (shown)
            {
                _seen = room.ChatTotal;
                Unread = 0;
                return;
            }

            // The lines that came in since the last look are the newest ones; the ones among them that are the others' are news.
            IReadOnlyList<ChatLine> chat = room.Chat;
            int first = chat.Count;
            while (first > 0 && chat[first - 1].Number > _seen)
                first--;
            int news = 0;
            for (int i = first; i < chat.Count; i++)
                news += chat[i].From != room.LocalId ? 1 : 0;
            int skip = Math.Max(0, news - MaxNotices);
            for (int i = first; i < chat.Count; i++)
            {
                ChatLine line = chat[i];
                if (line.From == room.LocalId)
                    continue;
                Unread++;
                if (skip > 0)
                    skip--;
                else if (notices != null)
                    notices.Add(DisplayText.Clip(line.Name, MaxNameLength) + ": " + line.Text, ToastLevel.Info, now);
            }
            _seen = room.ChatTotal;
        }
    }
}
