using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Net
{
    // The side of a room that runs the game. The table of the local player is the session of the GameHost;
    // in a split game every other player gets a session of their own.
    public sealed class NetHostRoom : NetRoom
    {
        private const int MaxCommandsPerUpdate = 30;

        private sealed class Table
        {
            public NetBoard Board;
            public GameSession Session;
            public bool Owned;
            public Action<ViewEvent> Listener;
            public readonly List<JsonValue> Outbox = new List<JsonValue>();
        }

        private readonly GameHost _host;
        private readonly List<Table> _tables = new List<Table>();
        private readonly Random _random = new Random();
        private readonly bool _fill;
        private readonly int _players;
        private JsonValue _options;
        private int _nextBoard = 1;
        private bool _drewSeed;

        // options: what the host chose for the room; null opens a room like any other, where the seats go to the people
        // in the order they come and the host plays the ones that nobody takes.
        public NetHostRoom(GameHost host, INetTransport transport, RoomOptions options = null)
            : base(transport, (host ?? throw new ArgumentNullException(nameof(host))).Mod ?? throw new InvalidOperationException("no game is selected"))
        {
            if (!transport.IsHost)
                throw new ArgumentException("the transport is not the host of its room", nameof(transport));
            _host = host;
            ModManifest manifest = Mod.Manifest;
            // The number of people that the host chose is kept for every game of the room.
            _players = options != null && manifest.ChoosesPlayerCount && options.Players > 0
                ? Math.Max(manifest.MinPlayers, Math.Min(manifest.MaxPlayers, options.Players))
                : 0;
            if (_players > 0)
                host.SetOption(manifest.PlayersOption, JsonValue.Of(_players));
            _options = host.NewGameOptions.DeepClone();
            _fill = options != null && options.ComputerFills && CanFill(host);
            ComputerFills = _fill;
            WaitsForPlayers = options != null && !_fill && !Mod.Manifest.SplitScreen && !Mod.Manifest.Cooperative;
            SeatCount = SeatsOfGame;
            _host.SessionReplaced += OnSessionReplaced;
            _host.GameStarting += OnGameStarting;
            _host.GameStarted += OnGameStarted;

            PlayerList.Add(new NetPlayer { Id = transport.LocalId, Name = CleanName(transport.LocalName) });
            Table own = new Table { Board = new NetBoard { Id = _nextBoard++, Owner = SplitScreen ? transport.LocalId : 0 }, Session = host.Session };
            _tables.Add(own);
            BoardList.Add(own.Board);
            Listen(own);
            own.Board.Queue.Reset(own.Session.View);
            Arrange();
            if (_fill)
                Reseat();
        }

        // Whether a room of this game can be filled by the computer: the seats are not played together or at tables of their
        // own, there is somebody to play against and the computer may play them.
        public static bool CanFill(GameHost host)
        {
            ModManifest manifest = host?.Mod?.Manifest;
            return manifest != null && !manifest.SplitScreen && !manifest.Cooperative && manifest.MaxPlayers >= 2 && host.CanPlayComputer;
        }

        // How many seats the game that is on, or about to be started, plays with.
        private int SeatsOfGame => Mod.Manifest.SeatCount(_options);

        public override bool CanAct
        {
            get
            {
                if (Mod.Manifest.Cooperative)
                    return OwnSeat > 0;
                if (IsClosed || !_host.AcceptsHumanInput || (SplitScreen && Finished))
                    return false;
                return SplitScreen || MayPlay(LocalId, _host.Session.View.Turn);
            }
        }

        public override int ActingSeat
        {
            get
            {
                if (Mod.Manifest.Cooperative)
                    return OwnSeat;
                return CanAct ? _host.Session.View.Turn : 0;
            }
        }

        public override void Submit(JsonValue command)
        {
            int seat = ActingSeat;
            if (seat > 0)
                _host.Submit(seat, command, "human");
        }

        // In a cooperative game nobody is on turn: the local player acts as the seat they sit at, whenever the game is
        // on and a person plays the seat. 0 for none.
        private int OwnSeat
        {
            get
            {
                NetPlayer me = LocalPlayer;
                GameSession session = _host.Session;
                if (IsClosed || me == null || me.Seat <= 0 || session == null || !session.IsStarted || session.View.IsOver)
                    return 0;
                return _host.GetSeatMode(me.Seat) == SeatMode.Human ? me.Seat : 0;
            }
        }

        public override void Update()
        {
            if (IsClosed)
                return;
            Dictionary<ulong, int> commands = new Dictionary<ulong, int>();
            while (!IsClosed && Transport.Poll(out NetEvent e))
            {
                switch (e.Kind)
                {
                    case NetEventKind.PeerLeft:
                        Remove(e.Peer);
                        break;
                    case NetEventKind.Data:
                        commands.TryGetValue(e.Peer, out int count);
                        if (count < MaxCommandsPerUpdate)
                        {
                            commands[e.Peer] = count + 1;
                            Receive(e.Peer, Decode(e.Data, MaxClientMessageBytes));
                        }
                        break;
                    case NetEventKind.Closed:
                        Close(e.Reason);
                        break;
                }
            }
            if (IsClosed)
                return;
            Flush();
            CheckFinished();
        }

        protected override void OnClosed()
        {
            _host.SessionReplaced -= OnSessionReplaced;
            _host.GameStarting -= OnGameStarting;
            _host.GameStarted -= OnGameStarted;
            Broadcast(Message("bye").Set("reason", "The host closed the room."));
            foreach (Table table in _tables)
                Release(table);
            _tables.Clear();
        }

        // A seat belongs to the player who sits on it; the host plays the seats nobody took, unless the room waits for people.
        private bool MayPlay(ulong player, int seat)
        {
            if (seat <= 0)
                return false;
            foreach (NetPlayer other in PlayerList)
            {
                if (other.Seat == seat)
                    return other.Id == player;
            }
            return player == LocalId && !WaitsForPlayers;
        }

        private void Receive(ulong peer, JsonValue message)
        {
            if (message == null)
                return;
            NetPlayer player = FindPlayer(peer);
            string type = message["t"].AsString();
            if (type == "hello" && player == null)
            {
                Welcome(peer, message);
                return;
            }
            if (type != "cmd" || player == null || !message["command"].IsObject)
                return;

            Table table = SplitScreen ? FindTable(player.Board) : _tables[0];
            if (table == null || !table.Session.IsStarted)
            {
                Note(peer, "You are watching this game.", ToastLevel.Warning);
                return;
            }
            ViewModel view = table.Session.View;
            int seat = SplitScreen ? view.Turn : player.Seat;
            if (seat <= 0 || view.IsOver || (SplitScreen && Finished))
            {
                Note(peer, seat <= 0 && !SplitScreen ? "You are watching this game." : "The game is over.", ToastLevel.Warning);
                return;
            }
            if (!SplitScreen && !Mod.Manifest.Cooperative && view.Turn > 0 && view.Turn != seat)
            {
                Note(peer, "It is not your turn.", ToastLevel.Warning);
                return;
            }
            if (!SplitScreen && _host.GetSeatMode(seat) != SeatMode.Human)
                return;

            SubmitResult result = table.Session.Submit(new InputPacket(seat, message["command"].DeepClone(), "net"));
            if (!result.IsAccepted)
                Note(peer, (string.IsNullOrEmpty(result.Message) ? result.Code : result.Message) + " [" + result.Code + "]", ToastLevel.Warning);
        }

        private void Welcome(ulong peer, JsonValue hello)
        {
            if (hello["proto"].AsInt(0) != Protocol)
            {
                Send(peer, Message("bye").Set("reason", "The host runs another version of Board Sandbox."));
                return;
            }
            if (hello["mod"].AsString() != Mod.Manifest.Id || hello["hash"].AsString() != Mod.ContentHash)
            {
                Send(peer, Message("bye").Set("reason", "Your copy of '" + Mod.Manifest.Name + "' differs from the host's."));
                return;
            }
            // Whatever is still waiting goes out first, so that the newcomer gets nothing twice.
            Flush();
            NetPlayer player = new NetPlayer { Id = peer, Name = CleanName(hello["name"].AsString()) };
            PlayerList.Add(player);
            Send(peer, Message("welcome").Set("proto", Protocol).Set("you", IdText(peer)));
            Arrange();
            foreach (Table table in _tables)
            {
                // A table that was opened just now tells its whole story with the next flush.
                if (table.Outbox.Count == 0)
                    SendState(peer, table);
            }
            if (_fill)
                Tell(player.Name + (ComputerSeatList.Count > 0 ? " joined. Start a new game to play with them." : " joined and watches."));
            else
                Tell(player.Name + " joined.");
        }

        private void Remove(ulong peer)
        {
            NetPlayer player = FindPlayer(peer);
            if (player == null)
                return;
            PlayerList.Remove(player);
            if (_fill && player.Seat > 0 && player.Seat <= SeatsOfGame && _host.SetSeatMode(player.Seat, _host.ComputerMode))
            {
                // The game goes on from where it stands, with the computer in the seat.
                RefreshComputerSeats();
                Tell(player.Name + " left. The computer plays " + SeatLabel(player.Seat) + " now.");
            }
            else
            {
                Tell(player.Name + " left.");
            }
            Arrange();
        }

        private string SeatLabel(int seat)
        {
            SeatInfo info = Mod.Manifest.GetSeat(seat);
            return info != null ? info.Name : "seat " + seat;
        }

        // Gives the seats of the next game to the people in the room, the host first and then the others in the order they
        // came; the computer plays the seats that are left. A person who comes after the seats are gone keeps watching.
        private void Reseat()
        {
            int seats = Math.Min(SeatsOfGame, Mod.Manifest.MaxPlayers);
            int next = 1;
            foreach (NetPlayer player in PlayerList)
                player.Seat = next <= seats ? next++ : 0;
            for (int seat = 1; seat <= Mod.Manifest.MaxPlayers; seat++)
            {
                // A seat that the game does not use has nobody to play it either.
                bool computer = seat >= next && seat <= seats;
                _host.SetSeatMode(seat, computer ? _host.ComputerMode : SeatMode.Human);
            }
            RefreshComputerSeats();
        }

        private void RefreshComputerSeats()
        {
            ComputerSeatList.Clear();
            int seats = Math.Min(SeatsOfGame, Mod.Manifest.MaxPlayers);
            for (int seat = 1; seat <= seats; seat++)
            {
                if (_host.GetSeatMode(seat) != SeatMode.Human)
                    ComputerSeatList.Add(seat);
            }
        }

        // Hands out seats or tables in the order the players came, and tells everyone when something changed.
        private void Arrange()
        {
            bool changed = false;
            if (SplitScreen)
            {
                for (int i = _tables.Count - 1; i >= 1; i--)
                {
                    if (FindPlayer(_tables[i].Board.Owner) != null)
                        continue;
                    Release(_tables[i]);
                    BoardList.Remove(_tables[i].Board);
                    _tables.RemoveAt(i);
                    changed = true;
                }
                foreach (NetPlayer player in PlayerList)
                {
                    Table table = FindTableOf(player.Id);
                    if (table == null && _tables.Count < Mod.Manifest.RoomPlayers)
                    {
                        table = Open(player);
                        changed |= table != null;
                    }
                    int board = table != null ? table.Board.Id : 0;
                    if (table != null)
                        table.Board.Title = player.Name;
                    changed |= player.Board != board;
                    player.Board = board;
                }
            }
            else if (_fill)
            {
                // Seats change hands when a game starts (Reseat) or somebody leaves (Remove), not as people come.
                changed = true;
            }
            else
            {
                HashSet<int> taken = new HashSet<int>();
                foreach (NetPlayer player in PlayerList)
                {
                    if (player.Seat > 0)
                        taken.Add(player.Seat);
                }
                int seats = Math.Min(SeatsOfGame, Mod.Manifest.MaxPlayers);
                foreach (NetPlayer player in PlayerList)
                {
                    if (player.Seat > 0)
                        continue;
                    for (int seat = 1; seat <= seats; seat++)
                    {
                        if (taken.Add(seat))
                        {
                            player.Seat = seat;
                            break;
                        }
                    }
                }
                changed = true;
            }
            if (changed)
                BoardsVersion++;
            Broadcast(Roster());
        }

        private Table Open(NetPlayer player)
        {
            if (player.Id == LocalId)
                return _tables[0];
            GameSession session;
            try
            {
                session = new GameSession(Mod, _host.SessionOptions);
            }
            catch (Exception e) when (e is LuauException || e is GameSessionException || e is ModException)
            {
                Tell("Could not open a table for " + player.Name + ": " + e.Message, ToastLevel.Error);
                return null;
            }
            Table table = new Table { Board = new NetBoard { Id = _nextBoard++, Owner = player.Id, Title = player.Name }, Session = session, Owned = true };
            Listen(table);
            _tables.Add(table);
            BoardList.Add(table.Board);
            if (_host.Session.IsStarted)
                Start(table);
            return table;
        }

        private void Start(Table table)
        {
            try
            {
                table.Session.NewGame(_options);
            }
            catch (Exception e) when (e is LuauException || e is GameSessionException)
            {
                Tell("Could not start the game of " + table.Board.Title + ": " + e.Message, ToastLevel.Error);
            }
        }

        private void Listen(Table table)
        {
            table.Listener = e =>
            {
                table.Board.Queue.Enqueue(e);
                table.Outbox.Add(e.Raw);
            };
            table.Session.EventApplied += table.Listener;
        }

        private void Release(Table table)
        {
            table.Session.EventApplied -= table.Listener;
            if (table.Owned)
                table.Session.Dispose();
        }

        private Table FindTable(int board)
        {
            foreach (Table table in _tables)
            {
                if (table.Board.Id == board)
                    return table;
            }
            return null;
        }

        private Table FindTableOf(ulong owner)
        {
            foreach (Table table in _tables)
            {
                if (table.Board.Owner == owner)
                    return table;
            }
            return null;
        }

        private void OnSessionReplaced()
        {
            GameSession session = _host.Session;
            if (session == null || session.Mod.Manifest.Id != Mod.Manifest.Id || session.Mod.ContentHash != Mod.ContentHash)
            {
                Close("The host chose another game.");
                return;
            }
            // Choosing the game again starts from its defaults; the room keeps what the host chose for it.
            if (_players > 0)
                _host.SetOption(Mod.Manifest.PlayersOption, JsonValue.Of(_players));
            _options = _host.NewGameOptions.DeepClone();
            SeatCount = SeatsOfGame;
            if (_fill)
                Reseat();
            Table own = _tables[0];
            own.Session.EventApplied -= own.Listener;
            own.Session = session;
            own.Outbox.Clear();
            Listen(own);
            own.Board.Queue.Reset(session.View);
            foreach (NetPlayer player in PlayerList)
            {
                if (player.Id != LocalId)
                    SendState(player.Id, own);
            }
        }

        // The tables of a split game start alike: a seed that the options leave open is drawn once for all.
        private void OnGameStarting(JsonValue options)
        {
            string seed = Mod.Manifest.SeedOption;
            _drewSeed = SplitScreen && seed.Length > 0 && options[seed].AsInt(0) <= 0;
            if (_drewSeed)
                options.Set(seed, _random.Next(1, int.MaxValue));
            _options = options.DeepClone();
            SeatCount = SeatsOfGame;
            // The game that starts now is played by the people who are here now: the ones who waited sit down.
            if (_fill)
                Reseat();
        }

        private void OnGameStarted()
        {
            if (_drewSeed)
                _host.SetOption(Mod.Manifest.SeedOption, JsonValue.Of(0));
            _drewSeed = false;
            Finished = false;
            for (int i = 1; i < _tables.Count; i++)
                Start(_tables[i]);
            Broadcast(Roster());
            if (_fill)
                TellTheirSeats();
        }

        // Tells every guest that has a seat in the game that has just started which one it is.
        private void TellTheirSeats()
        {
            foreach (NetPlayer player in PlayerList)
            {
                if (player.Id != LocalId && player.Seat > 0)
                    Note(player.Id, "A new game has started. You play " + SeatLabel(player.Seat) + ".", ToastLevel.Info);
            }
        }

        private void Broadcast(JsonValue message)
        {
            foreach (NetPlayer player in PlayerList)
            {
                if (player.Id != LocalId)
                    Send(player.Id, message);
            }
        }

        private void Flush()
        {
            foreach (Table table in _tables)
            {
                if (table.Outbox.Count == 0)
                    continue;
                for (int start = 0; start < table.Outbox.Count; start += EventsPerMessage)
                {
                    JsonValue list = JsonValue.NewArray();
                    for (int i = start; i < table.Outbox.Count && i < start + EventsPerMessage; i++)
                        list.Add(table.Outbox[i]);
                    Broadcast(Message("ev").Set("board", table.Board.Id).Set("events", list));
                }
                table.Outbox.Clear();
            }
        }

        // Everything that is on a table, for someone who has not seen it yet.
        private void SendState(ulong peer, Table table)
        {
            IReadOnlyList<ViewEvent> events = table.Session.View.Synthesize();
            int index = 0;
            do
            {
                JsonValue list = JsonValue.NewArray();
                for (int i = 0; i < EventsPerMessage && index < events.Count; i++, index++)
                    list.Add(events[index].Raw);
                Send(peer, Message("sync").Set("board", table.Board.Id).Set("events", list).Set("more", index < events.Count));
            }
            while (index < events.Count);
        }

        private void CheckFinished()
        {
            if (!SplitScreen || Finished)
                return;
            foreach (Table table in _tables)
            {
                ViewModel view = table.Session.View;
                if (!table.Session.IsStarted || !view.IsOver || view.Over.Winner <= 0)
                    continue;
                Finished = true;
                string text = PlayerName(table.Board.Owner) + " finished first!";
                Tell(text);
                Broadcast(Message("finished").Set("board", table.Board.Id).Set("text", text));
                return;
            }
        }

        private void Note(ulong peer, string text, ToastLevel level)
        {
            Send(peer, Message("note").Set("text", text).Set("level", level == ToastLevel.Error ? "error" : level == ToastLevel.Warning ? "warning" : "info"));
        }

        private JsonValue Roster()
        {
            JsonValue players = JsonValue.NewArray();
            foreach (NetPlayer player in PlayerList)
                players.Add(JsonValue.NewObject().Set("id", IdText(player.Id)).Set("name", player.Name).Set("seat", player.Seat).Set("board", player.Board));
            JsonValue boards = JsonValue.NewArray();
            foreach (NetBoard board in BoardList)
                boards.Add(JsonValue.NewObject().Set("id", board.Id).Set("owner", IdText(board.Owner)).Set("title", board.Title));
            JsonValue computer = JsonValue.NewArray();
            foreach (int seat in ComputerSeatList)
                computer.Add(seat);
            return Message("roster").Set("players", players).Set("boards", boards).Set("finished", Finished)
                .Set("fill", _fill).Set("wait", WaitsForPlayers).Set("seats", SeatCount).Set("ai", computer);
        }
    }
}
