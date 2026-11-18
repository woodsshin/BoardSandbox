#if BSE_EOS && UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BoardSandbox.Core.Net;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace BoardSandbox.Client.Eos
{
    // Rooms as Epic Online Services lobbies, for Android. Nobody has to sign in: the first time the app starts, the device makes
    // itself an anonymous account (a "device id"), and that account is what the other players of a room see. The account goes
    // away with the app (or when its data is cleared), and the next start makes a new one.
    //
    // A lobby carries the game it plays in its public attributes, so that a search finds the rooms of one game; the members
    // talk to each other through peer to peer connections (EosSession).
    //
    // The service itself (starting it, running its callbacks every frame, the keys of the product) is the job of the Epic Online
    // Services plugin for Unity, which has to be set up in the project; see the readme.
    public sealed class EosBackend : ILobbyBackend
    {
        public const string ModKey = "bse_mod";
        public const string HashKey = "bse_hash";
        public const string VersionKey = "bse_version";
        public const string ProtocolKey = "bse_proto";
        public const string NameKey = "bse_name";
        public const string Bucket = "bse";
        public const int MaxListed = 50;

        // What a text in a lobby attribute may take; the service counts in bytes and allows 64 of them at most. The hash of a
        // game (64 letters) is cut in two to stay clear of the limit.
        private const int MaxAttributeBytes = 60;
        private const int HashPieceChars = 32;
        private const string DisplayName = "Player";

        // Seconds the plugin has to start the service, and seconds between two attempts to sign in.
        private const float StartTimeout = 30f;
        private const float RetryAfter = 10f;

        private enum Phase
        {
            Starting,
            SigningIn,
            Waiting,
            SignedIn,
            Failed
        }

        private readonly List<LobbyInfo> _lobbies = new List<LobbyInfo>();
        private readonly Dictionary<ulong, LobbyDetails> _found = new Dictionary<ulong, LobbyDetails>();
        private EOSManager.EOSSingleton _eos;
        private Phase _phase;
        private float _giveUpAt;
        private float _retryAt;
        private ProductUserId _localUser;
        private LobbySearch _search;
        private EosSession _session;
        private INetTransport _ready;
        private string _error;
        private bool _disposed;

        public EosBackend()
        {
            Status = "Starting the online service…";
            try
            {
                // The plugin starts the service when its manager is added to a game object, and runs it from then on.
                GameObject owner = new GameObject("Board Sandbox online service");
                UnityEngine.Object.DontDestroyOnLoad(owner);
                owner.AddComponent<EOSManager>();
                _eos = EOSManager.Instance;
            }
            catch (Exception e)
            {
                Fail("The online service could not start: " + e.Message);
                return;
            }
            _giveUpAt = Time.realtimeSinceStartup + StartTimeout;
        }

        public bool Available => _phase == Phase.SignedIn && !_disposed;
        public string Status { get; private set; }
        public string LocalName { get; private set; } = DisplayName;
        public bool Busy { get; private set; }
        public IReadOnlyList<LobbyInfo> Lobbies => _lobbies;

        public void Update()
        {
            if (_disposed || _eos == null)
                return;
            float now = Time.realtimeSinceStartup;
            switch (_phase)
            {
                case Phase.Starting:
                    if (_eos.GetEOSPlatformInterface() != null)
                        BeginSignIn();
                    else if (now > _giveUpAt)
                        Fail("Online play is not set up in this build: the Epic Online Services settings are missing or wrong.");
                    break;
                case Phase.Waiting:
                    if (now >= _retryAt)
                        BeginSignIn();
                    break;
            }
            _session?.Update();
        }

        public void RequestList(string modId)
        {
            if (!Available || Busy || string.IsNullOrEmpty(modId))
                return;
            ForgetFound();
            if (!FitsAnAttribute(modId))
                return;
            LobbyInterface lobby = _eos.GetEOSLobbyInterface();
            CreateLobbySearchOptions create = new CreateLobbySearchOptions { MaxResults = MaxListed };
            if (lobby.CreateLobbySearch(ref create, out LobbySearch search) != Result.Success || search == null)
            {
                _error = "The list of rooms could not be asked for.";
                return;
            }
            if (!Filter(search, LobbyInterface.SEARCH_BUCKET_ID, Bucket)
                || !Filter(search, ModKey, modId)
                || !Filter(search, ProtocolKey, NetRoom.Protocol.ToString(CultureInfo.InvariantCulture)))
            {
                search.Release();
                _error = "The list of rooms could not be asked for.";
                return;
            }
            LobbySearchFindOptions find = new LobbySearchFindOptions { LocalUserId = _localUser };
            _search = search;
            Busy = true;
            search.Find(ref find, null, OnListed);
        }

        public void Create(LobbyInfo description)
        {
            if (!Available || Busy || description == null)
                return;
            if (!FitsAnAttribute(description.ModId))
            {
                _error = "The id of this game is too long for an online room.";
                return;
            }
            CreateLobbyOptions options = new CreateLobbyOptions
            {
                LocalUserId = _localUser,
                MaxLobbyMembers = (uint)Math.Max(2, Math.Min(LobbyInterface.MAX_LOBBY_MEMBERS, description.Capacity)),
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = false,
                BucketId = Bucket,
                DisableHostMigration = true,
                EnableRTCRoom = false
            };
            Busy = true;
            _eos.GetEOSLobbyInterface().CreateLobby(ref options, null, (ref CreateLobbyCallbackInfo info) => OnCreated(info.ResultCode, info.LobbyId, description));
        }

        public void Join(LobbyInfo lobby)
        {
            if (!Available || Busy || lobby == null)
                return;
            if (!_found.TryGetValue(lobby.Id, out LobbyDetails details))
            {
                _error = "That room is not listed any more. Refresh the list.";
                return;
            }
            JoinLobbyOptions options = new JoinLobbyOptions { LobbyDetailsHandle = details, LocalUserId = _localUser, PresenceEnabled = false };
            Busy = true;
            _eos.GetEOSLobbyInterface().JoinLobby(ref options, null, OnJoined);
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
            _session?.Dispose();
            _session = null;
            _ready = null;
            ForgetFound();
            // The service stays up: the plugin shuts it down when the app ends.
        }

        internal static void Log(string message)
        {
            Debug.Log("[Board Sandbox online] " + message);
        }

        // The id that everybody in a room knows a person by: made of the text of their product user id, which every device sees alike.
        internal static ulong IdOf(ProductUserId user)
        {
            return user == null ? 0UL : NetIds.FromText(user.ToString());
        }

        internal void Forget(EosSession session)
        {
            if (_session == session)
                _session = null;
        }

        // ---- signing in ----------------------------------------------------------------------------------------------

        private void Fail(string message)
        {
            _phase = Phase.Failed;
            Status = message;
            Log(message);
        }

        private void BeginSignIn()
        {
            _phase = Phase.SigningIn;
            Status = "Signing in to the online service…";
            string model = ServiceText.ClipUtf8(SystemInfo.deviceModel, ConnectInterface.CREATEDEVICEID_DEVICEMODEL_MAX_LENGTH);
            CreateDeviceIdOptions options = new CreateDeviceIdOptions { DeviceModel = model.Length > 0 ? model : "Android device" };
            _eos.GetEOSConnectInterface().CreateDeviceId(ref options, null, OnDeviceId);
        }

        // The id of the device is made once; asking again says it is there already, which is as good.
        private void OnDeviceId(ref CreateDeviceIdCallbackInfo info)
        {
            Result result = info.ResultCode;
            if (_disposed)
                return;
            if (result != Result.Success && result != Result.DuplicateNotAllowed)
            {
                SignInFailed(result);
                return;
            }
            LoginOptions login = new LoginOptions
            {
                Credentials = new Credentials { Token = null, Type = ExternalCredentialType.DeviceidAccessToken },
                UserLoginInfo = new UserLoginInfo { DisplayName = DisplayName }
            };
            _eos.StartConnectLoginWithOptions(login, OnLogin);
        }

        private void OnLogin(LoginCallbackInfo info)
        {
            if (_disposed)
                return;
            if (info.ResultCode == Result.Success)
            {
                SignedIn(info.LocalUserId);
                return;
            }
            // The first time this device signs in the service does not know it yet, and asks to make its account.
            if (info.ResultCode == Result.InvalidUser && info.ContinuanceToken != null)
            {
                _eos.CreateConnectUserWithContinuanceToken(info.ContinuanceToken, OnUserCreated);
                return;
            }
            SignInFailed(info.ResultCode);
        }

        private void OnUserCreated(CreateUserCallbackInfo info)
        {
            if (_disposed)
                return;
            if (info.ResultCode == Result.Success)
                SignedIn(info.LocalUserId);
            else
                SignInFailed(info.ResultCode);
        }

        private void SignedIn(ProductUserId user)
        {
            _localUser = user;
            _phase = Phase.SignedIn;
            Status = string.Empty;
            // Nobody gave a name, so the player is told apart by a number that stays the same as long as the account does.
            LocalName = DisplayName + " " + (1000 + IdOf(user) % 9000).ToString(CultureInfo.InvariantCulture);
            Log("Signed in as " + LocalName + ".");
        }

        private void SignInFailed(Result result)
        {
            _phase = Phase.Waiting;
            _retryAt = Time.realtimeSinceStartup + RetryAfter;
            bool offline = result == Result.NoConnection || result == Result.NetworkDisconnected || result == Result.TimedOut;
            Status = offline
                ? "The online service cannot be reached. Check the internet connection; trying again…"
                : "Signing in to the online service did not work (" + result + "); trying again…";
            Log("Sign in failed: " + result);
        }

        // ---- the list of rooms ---------------------------------------------------------------------------------------

        private static bool Filter(LobbySearch search, Utf8String key, string value)
        {
            LobbySearchSetParameterOptions options = new LobbySearchSetParameterOptions
            {
                Parameter = new AttributeData { Key = key, Value = value },
                ComparisonOp = ComparisonOp.Equal
            };
            return search.SetParameter(ref options) == Result.Success;
        }

        private void OnListed(ref LobbySearchFindCallbackInfo info)
        {
            Result result = info.ResultCode;
            LobbySearch search = _search;
            _search = null;
            Busy = false;
            if (search == null)
                return;
            try
            {
                // Nothing found is a result, not a failure.
                if (_disposed || result == Result.NotFound)
                    return;
                if (result != Result.Success)
                {
                    _error = "The list of rooms could not be fetched (" + result + ").";
                    return;
                }
                ReadLobbies(search);
            }
            finally
            {
                search.Release();
            }
        }

        private void ReadLobbies(LobbySearch search)
        {
            LobbySearchGetSearchResultCountOptions countOptions = new LobbySearchGetSearchResultCountOptions();
            uint count = search.GetSearchResultCount(ref countOptions);
            for (uint i = 0; i < count && _lobbies.Count < MaxListed; i++)
            {
                LobbySearchCopySearchResultByIndexOptions at = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                if (search.CopySearchResultByIndex(ref at, out LobbyDetails details) != Result.Success || details == null)
                    continue;
                LobbyInfo room = Describe(details);
                if (room == null || _found.ContainsKey(room.Id))
                {
                    details.Release();
                    continue;
                }
                _found.Add(room.Id, details);
                _lobbies.Add(room);
            }
        }

        // The room that a lobby stands for; null when it is not a room of this game, or is the stale room of the local player.
        private LobbyInfo Describe(LobbyDetails details)
        {
            LobbyDetailsCopyInfoOptions infoOptions = new LobbyDetailsCopyInfoOptions();
            if (details.CopyInfo(ref infoOptions, out LobbyDetailsInfo? data) != Result.Success || !data.HasValue)
                return null;
            LobbyDetailsInfo lobby = data.Value;
            string lobbyId = lobby.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
                return null;
            LobbyDetailsGetLobbyOwnerOptions ownerOptions = new LobbyDetailsGetLobbyOwnerOptions();
            if (IdOf(details.GetLobbyOwner(ref ownerOptions)) == IdOf(_localUser))
                return null;
            string mod = Text(details, ModKey);
            if (mod.Length == 0)
                return null;
            return new LobbyInfo
            {
                Id = NetIds.FromText(lobbyId),
                Name = Text(details, NameKey),
                ModId = mod,
                ModHash = Text(details, HashKey + "0") + Text(details, HashKey + "1"),
                ModVersion = Text(details, VersionKey),
                Players = (int)(lobby.MaxMembers - lobby.AvailableSlots),
                Capacity = (int)lobby.MaxMembers
            };
        }

        private static string Text(LobbyDetails details, string key)
        {
            LobbyDetailsCopyAttributeByKeyOptions options = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
            if (details.CopyAttributeByKey(ref options, out Epic.OnlineServices.Lobby.Attribute? attribute) != Result.Success || !attribute.HasValue)
                return string.Empty;
            AttributeData? data = attribute.Value.Data;
            if (!data.HasValue || data.Value.Value.ValueType != AttributeType.String)
                return string.Empty;
            string text = data.Value.Value.AsUtf8;
            return text ?? string.Empty;
        }

        private void ForgetFound()
        {
            foreach (LobbyDetails details in _found.Values)
                details.Release();
            _found.Clear();
            _lobbies.Clear();
        }

        // ---- opening a room ------------------------------------------------------------------------------------------

        private void OnCreated(Result result, string lobbyId, LobbyInfo description)
        {
            if (_disposed)
                return;
            if (result != Result.Success || string.IsNullOrEmpty(lobbyId))
            {
                Busy = false;
                _error = "The room could not be opened (" + result + ").";
                return;
            }

            // The lobby exists. What makes it a room of this game is added now; only then can others find it.
            LobbyInterface lobby = _eos.GetEOSLobbyInterface();
            UpdateLobbyModificationOptions modify = new UpdateLobbyModificationOptions { LocalUserId = _localUser, LobbyId = lobbyId };
            if (lobby.UpdateLobbyModification(ref modify, out LobbyModification modification) != Result.Success || modification == null)
            {
                GiveUp(lobbyId, "The room could not be set up.");
                return;
            }
            string hash = description.ModHash ?? string.Empty;
            string first = hash.Length > HashPieceChars ? hash.Substring(0, HashPieceChars) : hash;
            string second = hash.Length > HashPieceChars ? hash.Substring(HashPieceChars) : string.Empty;
            bool added = AddAttribute(modification, NameKey, description.Name)
                && AddAttribute(modification, ModKey, description.ModId)
                && AddAttribute(modification, HashKey + "0", first)
                && AddAttribute(modification, HashKey + "1", second)
                && AddAttribute(modification, VersionKey, description.ModVersion)
                && AddAttribute(modification, ProtocolKey, NetRoom.Protocol.ToString(CultureInfo.InvariantCulture));
            if (!added)
            {
                modification.Release();
                GiveUp(lobbyId, "The room could not be set up.");
                return;
            }
            UpdateLobbyOptions update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
            lobby.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo info) => OnPublished(info.ResultCode, lobbyId));
            modification.Release();
        }

        // A text that is empty is left out: the service does not take empty values, and a room without the attribute reads the same.
        private static bool AddAttribute(LobbyModification modification, string key, string value)
        {
            string text = ServiceText.ClipUtf8(value, MaxAttributeBytes);
            if (text.Length == 0)
                return true;
            LobbyModificationAddAttributeOptions options = new LobbyModificationAddAttributeOptions
            {
                Attribute = new AttributeData { Key = key, Value = text },
                Visibility = LobbyAttributeVisibility.Public
            };
            return modification.AddAttribute(ref options) == Result.Success;
        }

        private void OnPublished(Result result, string lobbyId)
        {
            if (_disposed)
                return;
            if (result != Result.Success)
            {
                GiveUp(lobbyId, "The room could not be set up (" + result + ").");
                return;
            }
            Busy = false;
            // The one who opens the lobby owns it, and so hosts the room.
            Use(new EosSession(this, _eos.GetEOSP2PInterface(), _eos.GetEOSLobbyInterface(), _localUser, lobbyId, _localUser, LocalName));
            Log("Opened a room.");
        }

        // A room that came about but cannot be used is taken down again.
        private void GiveUp(string lobbyId, string problem)
        {
            Busy = false;
            _error = problem;
            DestroyLobbyOptions destroy = new DestroyLobbyOptions { LocalUserId = _localUser, LobbyId = lobbyId };
            _eos.GetEOSLobbyInterface().DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => Log("A room that could not be set up was taken down: " + info.ResultCode));
        }

        // ---- joining a room ------------------------------------------------------------------------------------------

        private void OnJoined(ref JoinLobbyCallbackInfo info)
        {
            Result result = info.ResultCode;
            string lobbyId = info.LobbyId;
            Busy = false;
            if (_disposed)
                return;
            if (result != Result.Success)
            {
                _error = JoinProblem(result);
                return;
            }
            LobbyInterface lobby = _eos.GetEOSLobbyInterface();
            ProductUserId owner = OwnerOf(lobby, lobbyId);
            if (owner == null)
            {
                Leave(lobby, lobbyId);
                _error = "The room could not be joined: its host is not known.";
                return;
            }
            // The owner of the lobby is the host, whoever it is at the moment the room is entered.
            Use(new EosSession(this, _eos.GetEOSP2PInterface(), lobby, _localUser, lobbyId, owner, LocalName));
            Log("Joined a room.");
        }

        private ProductUserId OwnerOf(LobbyInterface lobby, string lobbyId)
        {
            CopyLobbyDetailsHandleOptions copy = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = _localUser };
            if (lobby.CopyLobbyDetailsHandle(ref copy, out LobbyDetails details) != Result.Success || details == null)
                return null;
            try
            {
                LobbyDetailsGetLobbyOwnerOptions ownerOptions = new LobbyDetailsGetLobbyOwnerOptions();
                return details.GetLobbyOwner(ref ownerOptions);
            }
            finally
            {
                details.Release();
            }
        }

        private void Leave(LobbyInterface lobby, string lobbyId)
        {
            LeaveLobbyOptions leave = new LeaveLobbyOptions { LocalUserId = _localUser, LobbyId = lobbyId };
            lobby.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => Log("A room was left: " + info.ResultCode));
        }

        private static string JoinProblem(Result result)
        {
            switch (result)
            {
                case Result.LobbyTooManyPlayers:
                    return "The room is full.";
                case Result.NotFound:
                case Result.LobbyInvalidSession:
                    return "The room is gone.";
                case Result.NoConnection:
                case Result.NetworkDisconnected:
                case Result.TimedOut:
                    return "The online service cannot be reached.";
                default:
                    return "The room could not be joined (" + result + "). It may be full or closed.";
            }
        }

        private void Use(EosSession session)
        {
            _session?.Dispose();
            _session = session;
            _ready = session.Transport;
        }

        private static bool FitsAnAttribute(string text)
        {
            return !string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) <= MaxAttributeBytes;
        }
    }
}
#endif
