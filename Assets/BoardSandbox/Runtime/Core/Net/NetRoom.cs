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

        protected readonly INetTransport Transport;
        protected readonly List<NetPlayer> PlayerList = new List<NetPlayer>();
        protected readonly List<NetBoard> BoardList = new List<NetBoard>();

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
