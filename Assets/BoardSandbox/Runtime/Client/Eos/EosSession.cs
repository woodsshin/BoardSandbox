#if BSE_EOS && UNITY_ANDROID
using System;
using System.Collections.Generic;
using BoardSandbox.Core.Net;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using UnityEngine;

namespace BoardSandbox.Client.Eos
{
    // One room as Epic Online Services shows it: a lobby, whose members are the people of the room, and the peer to peer
    // connections between them. The lobby says who is in the room; the connections carry the messages (see PacketTransport,
    // which cuts a message into the small packets that a connection takes).
    //
    // Only people who are in the lobby may talk to the room: a request to connect from somebody else is not answered. The
    // host talks to every guest, a guest only to the host.
    internal sealed class EosSession : IPacketLink
    {
        private const byte Channel = 0;

        // The lobby and a connection are not always heard of in the same order. A request that comes from somebody the lobby has
        // not shown yet waits this long (seconds) for the lobby to catch up.
        private const float RequestLifetime = 15f;
        private const int MaxPendingRequests = 16;

        private struct Waiting
        {
            public ProductUserId User;
            public ulong Id;
            public float Until;
        }

        private readonly EosBackend _backend;
        private readonly P2PInterface _p2p;
        private readonly LobbyInterface _lobby;
        private readonly ProductUserId _local;
        private readonly string _lobbyId;
        private readonly bool _isHost;
        private readonly SocketId _socket;
        private readonly Dictionary<ulong, ProductUserId> _users = new Dictionary<ulong, ProductUserId>();
        private readonly List<Waiting> _waiting = new List<Waiting>();
        private SocketId _receivedOn = new SocketId();
        private ProductUserId _sender;
        private ulong _statusNotification;
        private ulong _requestNotification;
        private ulong _closedNotification;
        private bool _disposed;

        // The owner of the lobby is the host of the room, whoever it was when the lobby was entered.
        internal EosSession(EosBackend backend, P2PInterface p2p, LobbyInterface lobby, ProductUserId local, string lobbyId, ProductUserId owner, string localName)
        {
            _backend = backend;
            _p2p = p2p;
            _lobby = lobby;
            _local = local;
            _lobbyId = lobbyId;
            ulong localId = EosBackend.IdOf(local);
            ulong hostId = EosBackend.IdOf(owner);
            _isHost = localId == hostId;

            // The name of the socket belongs to the room alone, so that what a peer of an earlier room sends is not taken for
            // a message of this one.
            _socket = new SocketId { SocketName = "bse" + NetIds.FromText(lobbyId).ToString("x16") };
            Transport = new PacketTransport(this, localId, hostId, localName);

            AddNotifyLobbyMemberStatusReceivedOptions statusOptions = new AddNotifyLobbyMemberStatusReceivedOptions();
            _statusNotification = _lobby.AddNotifyLobbyMemberStatusReceived(ref statusOptions, null, OnMemberStatus);
            AddNotifyPeerConnectionRequestOptions requestOptions = new AddNotifyPeerConnectionRequestOptions { LocalUserId = _local, SocketId = _socket };
            _requestNotification = _p2p.AddNotifyPeerConnectionRequest(ref requestOptions, null, OnConnectionRequest);
            AddNotifyPeerConnectionClosedOptions closedOptions = new AddNotifyPeerConnectionClosedOptions { LocalUserId = _local, SocketId = _socket };
            _closedNotification = _p2p.AddNotifyPeerConnectionClosed(ref closedOptions, null, OnConnectionClosed);

            // A guest knows the host from the start, because the first thing it says is meant for the host. The host learns of
            // the guests that came in before it looked.
            Enter(owner);
            if (_isHost)
                EnterLobbyMembers();
        }

        public PacketTransport Transport { get; }

        public int MaxPacketBytes => P2PInterface.MAX_PACKET_SIZE;

        // Call it once per frame.
        internal void Update()
        {
            if (_disposed || _waiting.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].Until > now)
                    continue;
                CloseConnection(_waiting[i].User);
                _waiting.RemoveAt(i);
            }
        }

        public bool SendPacket(ulong peer, byte[] packet, int length)
        {
            if (_disposed || packet == null || length < 1 || length > packet.Length || !_users.TryGetValue(peer, out ProductUserId user))
                return false;
            SendPacketOptions options = new SendPacketOptions
            {
                LocalUserId = _local,
                RemoteUserId = user,
                SocketId = _socket,
                Channel = Channel,
                Data = new ArraySegment<byte>(packet, 0, length),
                AllowDelayedDelivery = true,
                Reliability = PacketReliability.ReliableOrdered
            };
            return _p2p.SendPacket(ref options) == Result.Success;
        }

        public bool ReceivePacket(byte[] buffer, out ulong peer, out int length)
        {
            peer = 0;
            length = 0;
            if (_disposed)
                return false;
            ReceivePacketOptions options = new ReceivePacketOptions { LocalUserId = _local, MaxDataSizeBytes = (uint)buffer.Length };
            while (true)
            {
                Result result = _p2p.ReceivePacket(ref options, ref _sender, ref _receivedOn, out byte channel, new ArraySegment<byte>(buffer), out uint written);
                if (result != Result.Success)
                    return false;
                // Whoever the packet is from, the transport only listens to members.
                if (channel != Channel || written == 0 || _sender == null)
                    continue;
                peer = EosBackend.IdOf(_sender);
                length = (int)written;
                return true;
            }
        }

        // Leaves the room: the host closes the lobby, a guest goes out of it.
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_statusNotification != 0)
                _lobby.RemoveNotifyLobbyMemberStatusReceived(_statusNotification);
            if (_requestNotification != 0)
                _p2p.RemoveNotifyPeerConnectionRequest(_requestNotification);
            if (_closedNotification != 0)
                _p2p.RemoveNotifyPeerConnectionClosed(_closedNotification);
            _statusNotification = 0;
            _requestNotification = 0;
            _closedNotification = 0;

            CloseConnectionsOptions close = new CloseConnectionsOptions { LocalUserId = _local, SocketId = _socket };
            _p2p.CloseConnections(ref close);
            _users.Clear();
            _waiting.Clear();

            if (_isHost)
            {
                DestroyLobbyOptions destroy = new DestroyLobbyOptions { LocalUserId = _local, LobbyId = _lobbyId };
                _lobby.DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => EosBackend.Log("The room was closed: " + info.ResultCode));
            }
            else
            {
                LeaveLobbyOptions leave = new LeaveLobbyOptions { LocalUserId = _local, LobbyId = _lobbyId };
                _lobby.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => EosBackend.Log("The room was left: " + info.ResultCode));
            }
            _backend.Forget(this);
        }

        private void EnterLobbyMembers()
        {
            CopyLobbyDetailsHandleOptions copy = new CopyLobbyDetailsHandleOptions { LobbyId = _lobbyId, LocalUserId = _local };
            if (_lobby.CopyLobbyDetailsHandle(ref copy, out LobbyDetails details) != Result.Success || details == null)
                return;
            try
            {
                LobbyDetailsGetMemberCountOptions countOptions = new LobbyDetailsGetMemberCountOptions();
                uint count = details.GetMemberCount(ref countOptions);
                for (uint i = 0; i < count; i++)
                {
                    LobbyDetailsGetMemberByIndexOptions at = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                    ProductUserId member = details.GetMemberByIndex(ref at);
                    if (member != null)
                        Enter(member);
                }
            }
            finally
            {
                details.Release();
            }
        }

        // Somebody is in the room. A guest only cares for the host; the host for everybody.
        private void Enter(ProductUserId user)
        {
            ulong id = EosBackend.IdOf(user);
            if (id == 0 || id == Transport.LocalId || (!_isHost && id != Transport.HostId))
                return;
            _users[id] = user;
            if (Transport.AddMember(id, string.Empty))
                AcceptWaiting(id);
        }

        private void Leave(ProductUserId user)
        {
            ulong id = EosBackend.IdOf(user);
            Transport.RemoveMember(id);
            _users.Remove(id);
            RemoveWaiting(id);
            CloseConnection(user);
        }

        private void OnMemberStatus(ref LobbyMemberStatusReceivedCallbackInfo info)
        {
            string lobbyId = info.LobbyId;
            ProductUserId user = info.TargetUserId;
            if (_disposed || lobbyId != _lobbyId || user == null)
                return;
            bool me = EosBackend.IdOf(user) == Transport.LocalId;
            switch (info.CurrentStatus)
            {
                case LobbyMemberStatus.Joined:
                    Enter(user);
                    break;
                case LobbyMemberStatus.Left:
                case LobbyMemberStatus.Disconnected:
                case LobbyMemberStatus.Kicked:
                    if (me)
                        Transport.Close("You are not in the room any more.");
                    else
                        Leave(user);
                    break;
                case LobbyMemberStatus.Closed:
                    Transport.Close("The room was closed.");
                    break;
            }
        }

        // Only the people of the room may connect.
        private void OnConnectionRequest(ref OnIncomingConnectionRequestInfo info)
        {
            ProductUserId user = info.RemoteUserId;
            if (_disposed || user == null)
                return;
            ulong id = EosBackend.IdOf(user);
            if (Transport.IsMember(id))
            {
                Accept(user);
                return;
            }
            // Not in the room as far as the lobby has told us, but it may be that it is about to be.
            if (_waiting.Count >= MaxPendingRequests)
                return;
            foreach (Waiting known in _waiting)
            {
                if (known.Id == id)
                    return;
            }
            _waiting.Add(new Waiting { User = user, Id = id, Until = Time.realtimeSinceStartup + RequestLifetime });
        }

        // A connection that went away takes the member with it: what was on its way to them is lost, and a room that has lost a
        // message cannot be trusted to show the same game to everybody.
        private void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
        {
            ProductUserId user = info.RemoteUserId;
            if (_disposed || user == null || info.Reason == ConnectionClosedReason.ClosedByLocalUser)
                return;
            ulong id = EosBackend.IdOf(user);
            if (!Transport.IsMember(id))
                return;
            EosBackend.Log("The connection to a member of the room ended: " + info.Reason);
            Transport.RemoveMember(id);
            _users.Remove(id);
        }

        private void AcceptWaiting(ulong id)
        {
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].Id != id)
                    continue;
                Accept(_waiting[i].User);
                _waiting.RemoveAt(i);
            }
        }

        private void RemoveWaiting(ulong id)
        {
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].Id == id)
                    _waiting.RemoveAt(i);
            }
        }

        private void Accept(ProductUserId user)
        {
            AcceptConnectionOptions options = new AcceptConnectionOptions { LocalUserId = _local, RemoteUserId = user, SocketId = _socket };
            _p2p.AcceptConnection(ref options);
        }

        private void CloseConnection(ProductUserId user)
        {
            CloseConnectionOptions options = new CloseConnectionOptions { LocalUserId = _local, RemoteUserId = user, SocketId = _socket };
            _p2p.CloseConnection(ref options);
        }
    }
}
#endif
