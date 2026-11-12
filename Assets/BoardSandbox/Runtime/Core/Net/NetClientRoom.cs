using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Net
{
    // The side of a room that only shows the game: it plays what the host tells it and sends the host what the
    // local player wants to do.
    public sealed class NetClientRoom : NetRoom
    {
        public const int MaxHostMessageBytes = 4 * 1024 * 1024;
        public const int MaxBoards = 16;
        public const int MaxPlayers = 64;

        private readonly Dictionary<int, ViewModel> _incoming = new Dictionary<int, ViewModel>();
        private readonly HashSet<int> _shown = new HashSet<int>();

        public NetClientRoom(ModPackage mod, INetTransport transport)
            : base(transport, mod)
        {
            if (transport.IsHost)
                throw new ArgumentException("the transport is the host of its room", nameof(transport));
            Send(transport.HostId, Message("hello").Set("proto", Protocol).Set("mod", mod.Manifest.Id).Set("hash", mod.ContentHash)
                .Set("name", CleanName(transport.LocalName)));
        }

        // True once the host has let the local player in.
        public bool Joined { get; private set; }

        // True once every table has arrived and can be shown.
        public bool Ready
        {
            get
            {
                if (!Joined || BoardList.Count == 0)
                    return false;
                foreach (NetBoard board in BoardList)
                {
                    if (!_shown.Contains(board.Id))
                        return false;
                }
                return true;
            }
        }

        public override bool CanAct
        {
            get
            {
                NetBoard board = LocalBoard;
                NetPlayer me = LocalPlayer;
                if (IsClosed || !Joined || board == null || me == null || board.Queue.IsBusy)
                    return false;
                ViewModel view = board.Queue.Display;
                if (view.IsOver || view.Turn <= 0)
                    return false;
                return SplitScreen ? !Finished : me.Seat == view.Turn;
            }
        }

        public override int ActingSeat => CanAct ? LocalBoard.Queue.Display.Turn : 0;

        public override void Submit(JsonValue command)
        {
            if (CanAct && command != null && command.IsObject)
                Send(Transport.HostId, Message("cmd").Set("command", command));
        }

        public override void Update()
        {
            while (!IsClosed && Transport.Poll(out NetEvent e))
            {
                switch (e.Kind)
                {
                    case NetEventKind.PeerLeft:
                        if (e.Peer == Transport.HostId)
                            Close("The host left.");
                        break;
                    case NetEventKind.Data:
                        if (e.Peer == Transport.HostId)
                            Receive(Decode(e.Data, MaxHostMessageBytes));
                        break;
                    case NetEventKind.Closed:
                        Close(e.Reason);
                        break;
                }
            }
        }

        private void Receive(JsonValue message)
        {
            if (message == null)
                return;
            switch (message["t"].AsString())
            {
                case "welcome":
                    Joined = true;
                    break;
                case "roster":
                    ReadRoster(message);
                    break;
                case "sync":
                    ReadState(message);
                    break;
                case "ev":
                    NetBoard board = FindBoard(message["board"].AsInt(0));
                    if (board == null || _incoming.ContainsKey(board.Id))
                        break;
                    _shown.Add(board.Id);
                    foreach (JsonValue raw in message["events"].Items)
                    {
                        ViewEvent e = Parse(raw);
                        if (e != null)
                            board.Queue.Enqueue(e);
                    }
                    break;
                case "finished":
                    Finished = true;
                    Tell(DisplayText.Clip(message["text"].AsString(), ToastQueue.MaxTextLength));
                    break;
                case "note":
                    string level = message["level"].AsString();
                    Tell(DisplayText.Clip(message["text"].AsString(), ToastQueue.MaxTextLength),
                        level == "error" ? ToastLevel.Error : level == "warning" ? ToastLevel.Warning : ToastLevel.Info);
                    break;
                case "bye":
                    Close(DisplayText.Clip(message["reason"].AsString(), ToastQueue.MaxTextLength));
                    break;
            }
        }

        private void ReadRoster(JsonValue message)
        {
            PlayerList.Clear();
            foreach (JsonValue entry in message["players"].Items)
            {
                if (PlayerList.Count >= MaxPlayers || !TryId(entry["id"], out ulong id))
                    continue;
                PlayerList.Add(new NetPlayer { Id = id, Name = CleanName(entry["name"].AsString()), Seat = Math.Max(0, entry["seat"].AsInt(0)), Board = entry["board"].AsInt(0) });
            }

            List<NetBoard> kept = new List<NetBoard>();
            foreach (JsonValue entry in message["boards"].Items)
            {
                int id = entry["id"].AsInt(0);
                if (id <= 0 || kept.Count >= MaxBoards)
                    continue;
                NetBoard board = FindBoard(id) ?? new NetBoard { Id = id };
                TryId(entry["owner"], out board.Owner);
                board.Title = CleanName(entry["title"].AsString());
                if (!kept.Contains(board))
                    kept.Add(board);
            }
            bool changed = kept.Count != BoardList.Count;
            for (int i = 0; i < kept.Count && !changed; i++)
                changed = kept[i] != BoardList[i];
            BoardList.Clear();
            BoardList.AddRange(kept);
            Finished = message["finished"].AsBool(false);
            if (changed)
                BoardsVersion++;
        }

        // The state of a table arrives in parts; it is shown once the last part is there.
        private void ReadState(JsonValue message)
        {
            NetBoard board = FindBoard(message["board"].AsInt(0));
            if (board == null)
                return;
            if (!_incoming.TryGetValue(board.Id, out ViewModel model))
            {
                model = new ViewModel();
                _incoming[board.Id] = model;
            }
            foreach (JsonValue raw in message["events"].Items)
            {
                ViewEvent e = Parse(raw);
                if (e != null)
                    model.Apply(e);
            }
            if (message["more"].AsBool(false))
                return;
            _incoming.Remove(board.Id);
            _shown.Add(board.Id);
            board.Queue.Reset(model);
        }

        private static ViewEvent Parse(JsonValue raw)
        {
            try
            {
                return raw.IsObject ? ViewEvent.Parse(raw) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
