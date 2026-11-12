using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Net
{
    public enum NetEventKind
    {
        PeerJoined,
        PeerLeft,
        Data,
        Closed
    }

    public struct NetEvent
    {
        public NetEventKind Kind;
        public ulong Peer;
        public string Name;
        public byte[] Data;
        public string Reason;
    }

    // A room of peers that can send each other messages; one of them is the host. Messages arrive whole and in
    // the order they were sent.
    public interface INetTransport : IDisposable
    {
        ulong LocalId { get; }
        ulong HostId { get; }
        bool IsHost { get; }
        string LocalName { get; }

        // False when the peer cannot be reached.
        bool Send(ulong peer, byte[] data);

        // Hands out what happened since the last call, one event at a time.
        bool Poll(out NetEvent e);
    }

    // Rooms inside one process, for tests and for trying a game without a network.
    public sealed class LoopbackNetwork
    {
        private readonly List<Endpoint> _endpoints = new List<Endpoint>();
        private ulong _nextId = 1;

        public INetTransport Host(string name)
        {
            if (_endpoints.Count > 0)
                throw new InvalidOperationException("the room has a host already");
            return Add(name);
        }

        public INetTransport Join(string name)
        {
            if (_endpoints.Count == 0 || _endpoints[0].Disposed)
                throw new InvalidOperationException("the room has no host");
            Endpoint joined = Add(name);
            foreach (Endpoint other in _endpoints)
            {
                if (other == joined || other.Disposed)
                    continue;
                other.Push(new NetEvent { Kind = NetEventKind.PeerJoined, Peer = joined.LocalId, Name = joined.LocalName });
                joined.Push(new NetEvent { Kind = NetEventKind.PeerJoined, Peer = other.LocalId, Name = other.LocalName });
            }
            return joined;
        }

        private Endpoint Add(string name)
        {
            Endpoint endpoint = new Endpoint(this, _nextId++, name);
            _endpoints.Add(endpoint);
            return endpoint;
        }

        private Endpoint Find(ulong id)
        {
            foreach (Endpoint endpoint in _endpoints)
            {
                if (endpoint.LocalId == id && !endpoint.Disposed)
                    return endpoint;
            }
            return null;
        }

        private void Leave(Endpoint left)
        {
            bool wasHost = _endpoints.Count > 0 && _endpoints[0] == left;
            foreach (Endpoint other in _endpoints)
            {
                if (other == left || other.Disposed)
                    continue;
                other.Push(new NetEvent { Kind = NetEventKind.PeerLeft, Peer = left.LocalId });
                if (wasHost)
                    other.Push(new NetEvent { Kind = NetEventKind.Closed, Reason = "The host left." });
            }
        }

        private sealed class Endpoint : INetTransport
        {
            private readonly LoopbackNetwork _network;
            private readonly Queue<NetEvent> _events = new Queue<NetEvent>();

            public Endpoint(LoopbackNetwork network, ulong id, string name)
            {
                _network = network;
                LocalId = id;
                LocalName = name ?? string.Empty;
            }

            public ulong LocalId { get; }
            public ulong HostId => _network._endpoints[0].LocalId;
            public bool IsHost => _network._endpoints[0] == this;
            public string LocalName { get; }
            public bool Disposed { get; private set; }

            public void Push(NetEvent e)
            {
                _events.Enqueue(e);
            }

            public bool Send(ulong peer, byte[] data)
            {
                Endpoint target = Disposed ? null : _network.Find(peer);
                if (target == null || data == null)
                    return false;
                target.Push(new NetEvent { Kind = NetEventKind.Data, Peer = LocalId, Data = (byte[])data.Clone() });
                return true;
            }

            public bool Poll(out NetEvent e)
            {
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
                if (Disposed)
                    return;
                Disposed = true;
                _network.Leave(this);
            }
        }
    }
}
