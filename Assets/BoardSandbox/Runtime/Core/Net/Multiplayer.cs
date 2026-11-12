using System;
using System.Collections.Generic;
using BoardSandbox.Core.App;
using BoardSandbox.Core.Mods;

namespace BoardSandbox.Core.Net
{
    // A room that people can find and join.
    public sealed class LobbyInfo
    {
        public ulong Id;
        public string Name = string.Empty;
        public string ModId = string.Empty;
        public string ModHash = string.Empty;
        public string ModVersion = string.Empty;
        public int Players;
        public int Capacity;
    }

    // The service that lists rooms and connects their members, such as Steam. Requests finish some frames
    // later; Update moves them along.
    public interface ILobbyBackend : IDisposable
    {
        // False when the service cannot be used; Status says why.
        bool Available { get; }
        string Status { get; }
        string LocalName { get; }

        // True while a list, a new room or a join is on its way.
        bool Busy { get; }

        // The rooms the last finished request found.
        IReadOnlyList<LobbyInfo> Lobbies { get; }

        void Update();
        void RequestList(string modId);
        void Create(LobbyInfo description);
        void Join(LobbyInfo lobby);

        // The connection of a room that was created or joined, once; null while there is none.
        INetTransport TakeTransport();

        // Why the last request failed, once; null when it did not.
        string TakeError();
    }

    // Playing with others: finds rooms for a game, opens one or joins one, and keeps the room going.
    public sealed class Multiplayer : IDisposable
    {
        public const int MaxSpectators = 8;

        private readonly GameHost _host;
        private readonly ILobbyBackend _backend;
        private ModPackage _pending;
        private bool _pendingHost;
        private string _listedMod = string.Empty;

        public Multiplayer(GameHost host, ILobbyBackend backend)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _backend = backend;
        }

        public bool Available => _backend != null && _backend.Available;
        public string Status => _backend == null ? "This build has no online service." : _backend.Status;
        public bool Busy => _backend != null && _backend.Busy;

        // The room the local player is in; null outside of one.
        public NetRoom Room { get; private set; }

        // Raised when a room was entered or left.
        public event Action RoomChanged;

        // Raised when the room of somebody else's game went away without the local player leaving it.
        public event Action RoomLost;

        // What the local player is waiting for on the way into a room; null when there is nothing to wait for.
        public string Loading
        {
            get
            {
                if (_pending != null)
                    return _pendingHost ? "Opening a room…" : "Joining the room…";
                NetClientRoom guest = Room as NetClientRoom;
                if (guest == null || guest.Ready)
                    return null;
                return guest.Joined ? "Loading the game…" : "Waiting for the host…";
            }
        }

        // The rooms that play the given game.
        public IReadOnlyList<LobbyInfo> Lobbies(string modId)
        {
            return Available && _listedMod == modId ? _backend.Lobbies : new LobbyInfo[0];
        }

        public void Refresh(string modId)
        {
            if (!Available || _backend.Busy || string.IsNullOrEmpty(modId))
                return;
            _listedMod = modId;
            _backend.RequestList(modId);
        }

        public bool HostGame(ModPackage mod)
        {
            if (!Ready(mod))
                return false;
            ModManifest manifest = mod.Manifest;
            _pending = mod;
            _pendingHost = true;
            _backend.Create(new LobbyInfo
            {
                Name = NetRoom.CleanName(_backend.LocalName) + " - " + manifest.Name,
                ModId = manifest.Id,
                ModHash = mod.ContentHash,
                ModVersion = manifest.Version,
                Capacity = manifest.RoomPlayers + MaxSpectators
            });
            return true;
        }

        public bool Join(LobbyInfo lobby)
        {
            if (lobby == null)
                return false;
            ModPackage mod = _host.Catalog.Find(lobby.ModId);
            if (mod == null)
            {
                Say("You do not have the game of that room.", ToastLevel.Warning);
                return false;
            }
            if (mod.ContentHash != lobby.ModHash)
            {
                Say("Your copy of '" + mod.Manifest.Name + "' differs from the host's (v" + DisplayText.Clip(lobby.ModVersion, 16) + ").", ToastLevel.Warning);
                return false;
            }
            if (!Ready(mod))
                return false;
            _pending = mod;
            _pendingHost = false;
            _backend.Join(lobby);
            return true;
        }

        public void Leave()
        {
            if (Room == null)
                return;
            NetRoom room = Room;
            Room = null;
            room.Notice -= Say;
            room.Dispose();
            RoomChanged?.Invoke();
        }

        // Call it once per frame.
        public void Update()
        {
            if (_backend == null)
                return;
            _backend.Update();

            string error = _backend.TakeError();
            if (error != null)
            {
                _pending = null;
                Say(error, ToastLevel.Error);
            }
            INetTransport transport = _backend.TakeTransport();
            if (transport != null)
                Enter(transport);

            if (Room == null)
                return;
            Room.Update();
            if (Room.IsClosed)
            {
                bool guest = !Room.IsHost;
                Say(Room.ClosedReason, ToastLevel.Warning);
                Leave();
                if (guest)
                    RoomLost?.Invoke();
            }
        }

        public void Dispose()
        {
            Leave();
            _backend?.Dispose();
        }

        private bool Ready(ModPackage mod)
        {
            if (mod == null)
                return false;
            if (!Available)
            {
                Say(Status, ToastLevel.Warning);
                return false;
            }
            if (_backend.Busy)
                return false;
            Leave();
            return true;
        }

        private void Enter(INetTransport transport)
        {
            ModPackage mod = _pending;
            _pending = null;
            if (mod == null || transport.IsHost != _pendingHost)
            {
                transport.Dispose();
                return;
            }
            Leave();
            if (_pendingHost)
            {
                if (!_host.SelectMod(mod))
                {
                    transport.Dispose();
                    return;
                }
                Room = new NetHostRoom(_host, transport);
                Room.Notice += Say;
                RoomChanged?.Invoke();
                _host.StartNewGame();
            }
            else
            {
                Room = new NetClientRoom(mod, transport);
                Room.Notice += Say;
                RoomChanged?.Invoke();
            }
        }

        private void Say(string text, ToastLevel level)
        {
            if (!string.IsNullOrEmpty(text))
                _host.Toasts.Add(text, level, _host.Now);
        }
    }
}
