using System;
using System.Collections.Generic;
using System.Text;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Net
{
    public sealed class NetPlayer
    {
        public ulong Id;
        public string Name = string.Empty;

        // The seat at a shared table; 0 for someone who watches.
        public int Seat;

        // The table of the player in a split game; 0 for none.
        public int Board;
    }

    // One table of a networked game as it is shown: the events of its game arrive in the queue.
    public sealed class NetBoard
    {
        public int Id;
        public ulong Owner;
        public string Title = string.Empty;
        public PlaybackQueue Queue = new PlaybackQueue();
    }

    // A game played by several people. The host runs the game and tells everyone what happens; the others send
    // it what they want to do. A shared game has one table with a seat per player, a split game gives every
    // player a table of their own that all the others can watch.
    public abstract class NetRoom : IDisposable
    {
        public const int Protocol = 1;
        public const int MaxNameLength = 32;
        public const int MaxClientMessageBytes = 32 * 1024;
        public const int EventsPerMessage = 200;

        // The least time between two views that the local player passes on, in seconds.
        public const double ViewInterval = 0.1;

        // How many lines of the chat a room keeps; the oldest go first.
        public const int MaxChatLines = 100;

        protected readonly INetTransport Transport;
        protected readonly List<NetPlayer> PlayerList = new List<NetPlayer>();
        protected readonly List<NetBoard> BoardList = new List<NetBoard>();
        protected readonly List<int> ComputerSeatList = new List<int>();

        // How each player looks at their own table, as far as it is known.
        protected readonly Dictionary<ulong, ViewPose> ViewList = new Dictionary<ulong, ViewPose>();

        // What the players have said, the newest last.
        protected readonly List<ChatLine> ChatList = new List<ChatLine>();

        private readonly ChatLimiter _chatLimiter = new ChatLimiter();
        private ViewPose _sentView;
        private double _sentViewAt;

        protected NetRoom(INetTransport transport, ModPackage mod)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        }

        public ModPackage Mod { get; }
        public bool IsHost => Transport.IsHost;
        public bool SplitScreen => Mod.Manifest.SplitScreen;
        public ulong LocalId => Transport.LocalId;
        public IReadOnlyList<NetPlayer> Players => PlayerList;
        public IReadOnlyList<NetBoard> Boards => BoardList;

        // The computer plays the seats that nobody sits at (RoomOptions.ComputerFills). Someone who joins watches the
        // game that is on until the host starts the next one, which seats them.
        public bool ComputerFills { get; protected set; }

        // How many seats the game plays with, and the ones of them that the computer plays at the moment.
        public int SeatCount { get; protected set; }
        public IReadOnlyList<int> ComputerSeats => ComputerSeatList;

        public bool IsComputerSeat(int seat)
        {
            return ComputerSeatList.Contains(seat);
        }

        // The people who have no seat in the game that is on while the computer fills the room: they watch until the host
        // starts the next game.
        public int WaitingCount
        {
            get
            {
                int waiting = 0;
                if (ComputerFills)
                {
                    foreach (NetPlayer player in PlayerList)
                        waiting += player.Seat <= 0 ? 1 : 0;
                }
                return waiting;
            }
        }

        // The host opened the room for people only: a seat that nobody sits at stays empty and the game waits for someone to
        // take it, instead of the host playing it too.
        public bool WaitsForPlayers { get; protected set; }

        // Grows whenever the list of tables changes.
        public int BoardsVersion { get; protected set; }

        // Set once somebody has finished a split game first.
        public bool Finished { get; protected set; }

        // Null while the room is open, afterwards why it closed.
        public string ClosedReason { get; private set; }
        public bool IsClosed => ClosedReason != null;

        // Something the local player should read.
        public event Action<string, ToastLevel> Notice;

        public NetPlayer LocalPlayer => FindPlayer(Transport.LocalId);

        // The table the local player acts on; null for someone who only watches a split game.
        public NetBoard LocalBoard
        {
            get
            {
                if (!SplitScreen)
                    return BoardList.Count > 0 ? BoardList[0] : null;
                NetPlayer me = LocalPlayer;
                return me == null ? null : FindBoard(me.Board);
            }
        }

        // Whether the players see each other's tables the way their owners look at them: in a game where everybody has a table of their
        // own, with a host that passes the views on. A host that does not knows nothing of them, and the tables stay as each player
        // turns them.
        public abstract bool SharesViews { get; }

        // How the player looks at their own table, as far as it is known; null for none.
        public ViewPose ViewOf(ulong player)
        {
            return ViewList.TryGetValue(player, out ViewPose pose) ? pose : null;
        }

        // Tells the others how the local player looks at their table. Call it every frame with the way it is looked at now: a change
        // goes out at once the first time and then at most ViewInterval apart, and a view that nobody could tell from the one that went
        // out last is not sent at all.
        public void ShareView(ViewPose pose, double now)
        {
            if (pose == null || IsClosed || !SharesViews)
                return;
            if (_sentView != null && (now - _sentViewAt < ViewInterval || pose.Near(_sentView)))
                return;
            _sentView = pose;
            _sentViewAt = now;
            PassOnView(pose);
        }

        protected abstract void PassOnView(ViewPose pose);

        // The message that carries a view; from is the player whose view it is, 0 in a message to the host, which knows who sent it.
        protected static JsonValue ViewMessage(ViewPose pose, ulong from)
        {
            JsonValue message = Message("view");
            if (from != 0)
                message.Set("from", IdText(from));
            pose.WriteTo(message);
            return message;
        }

        // Whether the players can talk to each other: a host always passes the lines on, a guest can only talk through a host
        // that does (an older host knows nothing of the chat).
        public abstract bool CanChat { get; }

        // What has been said in the room, the newest line last, at most MaxChatLines of them.
        public IReadOnlyList<ChatLine> Chat => ChatList;

        // How many lines have been said in all, counting the ones that were dropped; the Number of the newest line.
        public int ChatTotal { get; private set; }

        // Says something to everybody in the room. The line comes back through the host like everybody else's and is added then, so
        // that it reads the same on every screen. False when nothing was said: there is nobody to say it to, nothing is left of the
        // text once it is cleaned (ChatText.Clean), or the player has said too much lately (ChatLimiter), and then the player is told.
        public bool Say(string text, double now)
        {
            if (IsClosed || !CanChat)
                return false;
            string clean = ChatText.Clean(text);
            if (clean.Length == 0)
                return false;
            if (!_chatLimiter.Allow(now))
            {
                if (_chatLimiter.ShouldWarn(now))
                    Tell("You are talking too fast.", ToastLevel.Warning);
                return false;
            }
            PassOnChat(clean);
            return true;
        }

        protected abstract void PassOnChat(string text);

        protected void AddChat(ulong from, string name, string text)
        {
            if (ChatList.Count >= MaxChatLines)
                ChatList.RemoveAt(0);
            ChatTotal++;
            ChatList.Add(new ChatLine(ChatTotal, from, name, text));
        }

        // The message that carries a line to the players: who said it, by the name that the host has of them, and what.
        protected static JsonValue ChatMessage(ulong from, string name, string text)
        {
            return Message("chat").Set("from", IdText(from)).Set("name", name).Set("text", text);
        }

        // Whether the local player may make a move right now.
        public abstract bool CanAct { get; }

        // The seat whose pieces the local player would place right now; 0 for none.
        public abstract int ActingSeat { get; }

        public abstract void Submit(JsonValue command);

        // Call it once per frame.
        public abstract void Update();

        public NetPlayer FindPlayer(ulong id)
        {
            foreach (NetPlayer player in PlayerList)
            {
                if (player.Id == id)
                    return player;
            }
            return null;
        }

        public NetBoard FindBoard(int id)
        {
            foreach (NetBoard board in BoardList)
            {
                if (board.Id == id)
                    return board;
            }
            return null;
        }

        public string PlayerName(ulong id)
        {
            NetPlayer player = FindPlayer(id);
            return player != null && player.Name.Length > 0 ? player.Name : "Player";
        }

        public void Close(string reason)
        {
            if (IsClosed)
                return;
            ClosedReason = string.IsNullOrEmpty(reason) ? "The room was closed." : reason;
            OnClosed();
            Transport.Dispose();
        }

        public void Dispose()
        {
            Close("You left the room.");
        }

        protected virtual void OnClosed()
        {
        }

        protected void Tell(string text, ToastLevel level = ToastLevel.Info)
        {
            Notice?.Invoke(text, level);
        }

        protected bool Send(ulong peer, JsonValue message)
        {
            return Transport.Send(peer, Encoding.UTF8.GetBytes(message.ToJson()));
        }

        protected static JsonValue Message(string type)
        {
            return JsonValue.NewObject().Set("t", type);
        }

        protected static JsonValue Decode(byte[] data, int limit)
        {
            if (data == null || data.Length == 0 || data.Length > limit)
                return null;
            try
            {
                JsonValue value = JsonValue.Parse(Encoding.UTF8.GetString(data));
                return value.IsObject ? value : null;
            }
            catch (Exception e) when (e is JsonParseException || e is ArgumentException)
            {
                return null;
            }
        }

        public static string CleanName(string name)
        {
            StringBuilder clean = new StringBuilder();
            foreach (char c in name ?? string.Empty)
            {
                if (!char.IsControl(c) && clean.Length < MaxNameLength)
                    clean.Append(c);
            }
            string text = clean.ToString().Trim();
            return text.Length > 0 ? text : "Player";
        }

        protected static string IdText(ulong id)
        {
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        protected static bool TryId(JsonValue value, out ulong id)
        {
            id = 0;
            return value != null && value.IsString
                && ulong.TryParse(value.AsString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id);
        }
    }
}
