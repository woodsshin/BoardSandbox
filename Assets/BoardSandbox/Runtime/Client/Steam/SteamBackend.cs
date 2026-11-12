#if BSE_STEAM && UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BoardSandbox.Core.Net;
using Steamworks;

namespace BoardSandbox.Client.Steam
{
    // Rooms as Steam lobbies. A lobby carries the game it plays in its data, so that a search finds the rooms of
    // one game; its members talk to each other through Steam's relayed messages.
    public sealed class SteamBackend : ILobbyBackend
    {
        public const string ModKey = "bse_mod";
        public const string HashKey = "bse_hash";
        public const string VersionKey = "bse_version";
        public const string ProtocolKey = "bse_proto";
        public const string NameKey = "name";
        public const int MaxListed = 50;
        public const int Channel = 0;
        public const int MaxMessageBytes = NetClientRoom.MaxHostMessageBytes;

        private readonly List<LobbyInfo> _lobbies = new List<LobbyInfo>();
        private readonly bool _initialized;
        private Callback<LobbyChatUpdate_t> _chatUpdate;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private CallResult<LobbyMatchList_t> _listed;
        private CallResult<LobbyCreated_t> _created;
        private CallResult<LobbyEnter_t> _entered;
        private LobbyInfo _creating;
        private SteamTransport _transport;
        private INetTransport _ready;
        private string _error;
        private bool _disposed;

        public SteamBackend()
        {
            try
            {
                ESteamAPIInitResult result = SteamAPI.InitEx(out string message);
                _initialized = result == ESteamAPIInitResult.k_ESteamAPIInitResult_OK;
                if (!_initialized)
                {
                    Status = result == ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient
                        ? "Steam is not running. Start Steam and then this game again."
                        : "Steam could not be reached: " + message;
                    return;
                }
            }
            catch (Exception e) when (e is DllNotFoundException || e is InvalidOperationException || e is EntryPointNotFoundException)
            {
                Status = "The Steam library could not be loaded: " + e.Message;
                return;
            }

            Status = string.Empty;
            LocalName = SteamFriends.GetPersonaName();
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _listed = CallResult<LobbyMatchList_t>.Create(OnListed);
            _created = CallResult<LobbyCreated_t>.Create(OnCreated);
            _entered = CallResult<LobbyEnter_t>.Create(OnEntered);
        }

        public bool Available => _initialized && !_disposed;
        public string Status { get; private set; }
        public string LocalName { get; private set; } = string.Empty;
        public bool Busy { get; private set; }
        public IReadOnlyList<LobbyInfo> Lobbies => _lobbies;

        public void Update()
        {
            if (Available)
                SteamAPI.RunCallbacks();
        }

        public void RequestList(string modId)
        {
            if (!Available || Busy)
                return;
            SteamMatchmaking.AddRequestLobbyListStringFilter(ModKey, modId, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(ProtocolKey, NetRoom.Protocol.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(MaxListed);
            _listed.Set(SteamMatchmaking.RequestLobbyList());
            Busy = true;
        }

        public void Create(LobbyInfo description)
        {
            if (!Available || Busy || description == null)
                return;
            _creating = description;
            _created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, Math.Max(1, Math.Min(250, description.Capacity))));
            Busy = true;
        }

        public void Join(LobbyInfo lobby)
        {
            if (!Available || Busy || lobby == null)
                return;
            _entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby.Id)));
            Busy = true;
        }

        public INetTransport TakeTransport()
        {
            INetTransport ready = _ready;
            _ready = null;
            return ready;
        }

        public string TakeError()
        {
            string error = _error;
            _error = null;
            return error;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _transport?.Dispose();
            _chatUpdate?.Dispose();
            _sessionRequest?.Dispose();
            _listed?.Dispose();
            _created?.Dispose();
            _entered?.Dispose();
            if (_initialized)
                SteamAPI.Shutdown();
        }

        internal void Forget(SteamTransport transport)
        {
            if (_transport == transport)
                _transport = null;
        }

        private void OnListed(LobbyMatchList_t result, bool failed)
        {
            Busy = false;
            _lobbies.Clear();
            if (failed)
            {
                _error = "The list of rooms could not be fetched.";
                return;
            }
            for (int i = 0; i < result.m_nLobbiesMatching && i < MaxListed; i++)
            {
                CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
                _lobbies.Add(new LobbyInfo
                {
                    Id = lobby.m_SteamID,
                    Name = SteamMatchmaking.GetLobbyData(lobby, NameKey),
                    ModId = SteamMatchmaking.GetLobbyData(lobby, ModKey),
                    ModHash = SteamMatchmaking.GetLobbyData(lobby, HashKey),
                    ModVersion = SteamMatchmaking.GetLobbyData(lobby, VersionKey),
                    Players = SteamMatchmaking.GetNumLobbyMembers(lobby),
                    Capacity = SteamMatchmaking.GetLobbyMemberLimit(lobby)
                });
            }
        }

        private void OnCreated(LobbyCreated_t result, bool failed)
        {
            Busy = false;
            LobbyInfo description = _creating;
            _creating = null;
            if (failed || result.m_eResult != EResult.k_EResultOK || description == null)
            {
                _error = "The room could not be opened (" + result.m_eResult + ").";
                return;
            }
            CSteamID lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, NameKey, description.Name);
            SteamMatchmaking.SetLobbyData(lobby, ModKey, description.ModId);
            SteamMatchmaking.SetLobbyData(lobby, HashKey, description.ModHash);
            SteamMatchmaking.SetLobbyData(lobby, VersionKey, description.ModVersion);
            SteamMatchmaking.SetLobbyData(lobby, ProtocolKey, NetRoom.Protocol.ToString());
            Use(new SteamTransport(this, lobby, LocalName));
        }

        private void OnEntered(LobbyEnter_t result, bool failed)
        {
            Busy = false;
            if (failed || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                _error = "The room could not be joined. It may be full or closed.";
                return;
            }
            Use(new SteamTransport(this, new CSteamID(result.m_ulSteamIDLobby), LocalName));
        }

        private void Use(SteamTransport transport)
        {
            _transport?.Dispose();
            _transport = transport;
            _ready = transport;
        }

        private void OnChatUpdate(LobbyChatUpdate_t update)
        {
            _transport?.OnChatUpdate(update);
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            _transport?.OnSessionRequest(request);
        }
    }

    // The members of one Steam lobby. Whoever owned the lobby when it was entered is the host.
    public sealed class SteamTransport : INetTransport
    {
        private readonly SteamBackend _backend;
        private readonly CSteamID _lobby;
        private readonly HashSet<ulong> _members = new HashSet<ulong>();
        private readonly Queue<NetEvent> _events = new Queue<NetEvent>();
        private readonly IntPtr[] _received = new IntPtr[32];
        private bool _disposed;

        internal SteamTransport(SteamBackend backend, CSteamID lobby, string localName)
        {
            _backend = backend;
            _lobby = lobby;
            LocalName = localName ?? string.Empty;
            LocalId = SteamUser.GetSteamID().m_SteamID;
            HostId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (member.m_SteamID == LocalId || !_members.Add(member.m_SteamID))
                    continue;
                _events.Enqueue(new NetEvent { Kind = NetEventKind.PeerJoined, Peer = member.m_SteamID, Name = SteamFriends.GetFriendPersonaName(member) });
            }
        }

        public ulong LocalId { get; }
        public ulong HostId { get; }
        public bool IsHost => LocalId == HostId;
        public string LocalName { get; }

        public bool Send(ulong peer, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0 || data.Length > SteamBackend.MaxMessageBytes || !_members.Contains(peer))
                return false;
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(peer);
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return SteamNetworkingMessages.SendMessageToUser(ref identity, pinned.AddrOfPinnedObject(), (uint)data.Length,
                    Constants.k_nSteamNetworkingSend_Reliable, SteamBackend.Channel) == EResult.k_EResultOK;
            }
            finally
            {
                pinned.Free();
            }
        }

        public bool Poll(out NetEvent e)
        {
            if (_events.Count == 0 && !_disposed)
                Receive();
            if (_events.Count == 0)
            {
                e = default(NetEvent);
                return false;
            }
            e = _events.Dequeue();
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (ulong member in _members)
                CloseSession(member);
            _members.Clear();
            SteamMatchmaking.LeaveLobby(_lobby);
            _backend.Forget(this);
        }

        internal void OnChatUpdate(LobbyChatUpdate_t update)
        {
            if (_disposed || update.m_ulSteamIDLobby != _lobby.m_SteamID || update.m_ulSteamIDUserChanged == LocalId)
                return;
            ulong user = update.m_ulSteamIDUserChanged;
            bool entered = (update.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            if (entered)
            {
                if (_members.Add(user))
                    _events.Enqueue(new NetEvent { Kind = NetEventKind.PeerJoined, Peer = user, Name = SteamFriends.GetFriendPersonaName(new CSteamID(user)) });
                return;
            }
            if (!_members.Remove(user))
                return;
            CloseSession(user);
            _events.Enqueue(new NetEvent { Kind = NetEventKind.PeerLeft, Peer = user });
            if (user == HostId)
                _events.Enqueue(new NetEvent { Kind = NetEventKind.Closed, Reason = "The host left." });
        }

        // Only the members of the lobby may open a connection.
        internal void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            SteamNetworkingIdentity identity = request.m_identityRemote;
            if (!_disposed && _members.Contains(identity.GetSteamID64()))
                SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }

        private void Receive()
        {
            int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(SteamBackend.Channel, _received, _received.Length);
            for (int i = 0; i < count; i++)
            {
                IntPtr pointer = _received[i];
                try
                {
                    SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    ulong sender = message.m_identityPeer.GetSteamID64();
                    if (message.m_cbSize <= 0 || message.m_cbSize > SteamBackend.MaxMessageBytes || !_members.Contains(sender))
                        continue;
                    byte[] data = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, data, 0, data.Length);
                    _events.Enqueue(new NetEvent { Kind = NetEventKind.Data, Peer = sender, Data = data });
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                }
            }
        }

        private static void CloseSession(ulong peer)
        {
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(peer);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }
    }
}
#endif
