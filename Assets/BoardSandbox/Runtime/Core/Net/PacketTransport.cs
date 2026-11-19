using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Net
{
    // How a room reaches its members through a service that sends small packets between the people of a room, reliably and in
    // order, such as the peer to peer messages of Epic Online Services.
    public interface IPacketLink : IDisposable
    {
        // The most bytes a packet holds.
        int MaxPacketBytes { get; }

        // Sends the first length bytes of the packet to a member of the room. The packet is copied before this returns. False
        // when it cannot be sent.
        bool SendPacket(ulong peer, byte[] packet, int length);

        // Moves the packet that arrived next into the buffer, which holds MaxPacketBytes at least, and tells who sent it and
        // how long it is. False when no packet is waiting.
        bool ReceivePacket(byte[] buffer, out ulong peer, out int length);
    }

    // The connection of a room over a packet link. Whoever keeps the list of the room's members, such as the lobby of the
    // service, tells it who came (AddMember) and who left (RemoveMember); it sends and receives whole messages by cutting them
    // into packets and putting them together again, and hears only from members.
    public sealed class PacketTransport : INetTransport
    {
        // How many packets are taken in before Poll finds nothing more to hand out, so that a flood of packets cannot hold up a
        // frame: the rest waits in the link until the next frame.
        public const int PacketsPerDrain = 1024;

        private readonly IPacketLink _link;
        private readonly int _maxPacketBytes;
        private readonly byte[] _outgoing;
        private readonly byte[] _incoming;
        private readonly Dictionary<ulong, PacketAssembler> _members = new Dictionary<ulong, PacketAssembler>();
        private readonly Queue<NetEvent> _events = new Queue<NetEvent>();
        private int _budget = PacketsPerDrain;
        private bool _closed;
        private bool _disposed;

        public PacketTransport(IPacketLink link, ulong localId, ulong hostId, string localName)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _maxPacketBytes = link.MaxPacketBytes;
            if (_maxPacketBytes < PacketFraming.MinPacketBytes)
                throw new ArgumentException("the packets of the link are too small", nameof(link));
            if (localId == 0 || hostId == 0)
                throw new ArgumentException("nobody has the id 0");
            _outgoing = new byte[_maxPacketBytes];
            _incoming = new byte[_maxPacketBytes];
            LocalId = localId;
            HostId = hostId;
            LocalName = localName ?? string.Empty;
        }

        public ulong LocalId { get; }
        public ulong HostId { get; }
        public bool IsHost => LocalId == HostId;
        public string LocalName { get; }

        public bool IsMember(ulong peer)
        {
            return _members.ContainsKey(peer);
        }

        // Somebody came into the room. False when they were in already, and for the local player, who is not a member of
        // their own room.
        public bool AddMember(ulong peer, string name)
        {
            if (_disposed || peer == 0 || peer == LocalId || _members.ContainsKey(peer))
                return false;
            // The host sends what the game shows, which is much more than the guests send.
            int limit = peer == HostId ? NetClientRoom.MaxHostMessageBytes : NetRoom.MaxClientMessageBytes;
            _members.Add(peer, new PacketAssembler(limit));
            _events.Enqueue(new NetEvent { Kind = NetEventKind.PeerJoined, Peer = peer, Name = name ?? string.Empty });
            return true;
        }

        // Somebody left the room. When it is the host, the room is over.
        public void RemoveMember(ulong peer)
        {
            if (_disposed || !_members.Remove(peer))
                return;
            _events.Enqueue(new NetEvent { Kind = NetEventKind.PeerLeft, Peer = peer });
            if (peer == HostId)
                Close("The host left.");
        }

        // The room is over, whatever the reason; said once.
        public void Close(string reason)
        {
            if (_disposed || _closed)
                return;
            _closed = true;
            _events.Enqueue(new NetEvent { Kind = NetEventKind.Closed, Reason = string.IsNullOrEmpty(reason) ? "The room was closed." : reason });
        }

        public bool Send(ulong peer, byte[] data)
        {
            int limit = IsHost ? NetClientRoom.MaxHostMessageBytes : NetRoom.MaxClientMessageBytes;
            if (_disposed || data == null || data.Length == 0 || data.Length > limit || !_members.ContainsKey(peer))
                return false;
            int offset = 0;
            while (offset < data.Length)
            {
                int length = PacketFraming.Write(data, ref offset, _outgoing, _maxPacketBytes);
                if (!_link.SendPacket(peer, _outgoing, length))
                    return false;
            }
            return true;
        }

        public bool Poll(out NetEvent e)
        {
            if (_events.Count == 0 && !_disposed)
                Receive();
            if (_events.Count == 0)
            {
                _budget = PacketsPerDrain;
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
            _members.Clear();
            _events.Clear();
            _link.Dispose();
        }

        private void Receive()
        {
            while (_budget > 0 && _link.ReceivePacket(_incoming, out ulong peer, out int length))
            {
                _budget--;
                if (!_members.TryGetValue(peer, out PacketAssembler assembler))
                    continue;
                byte[] message = assembler.Add(_incoming, length);
                if (message != null)
                    _events.Enqueue(new NetEvent { Kind = NetEventKind.Data, Peer = peer, Data = message });
            }
        }
    }
}
