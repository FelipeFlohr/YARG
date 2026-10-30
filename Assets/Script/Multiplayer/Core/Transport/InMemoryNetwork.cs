using System;
using System.Collections.Generic;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// Single-threaded in-process network for tests: one host and any number of clients.
    /// Host and port are ignored; everything is delivered on <see cref="INetTransport.Poll"/>.
    /// </summary>
    public sealed class InMemoryNetwork
    {
        private sealed class Endpoint : INetTransport
        {
            private readonly InMemoryNetwork  _network;
            private readonly Queue<NetEvent>  _events = new();

            // Remote endpoint and the peer ID this endpoint is known as over there
            private readonly Dictionary<int, (Endpoint Remote, int IdThere)> _peers = new();

            public Endpoint(InMemoryNetwork network)
            {
                _network = network;
            }

            public void StartHost(int port)
            {
                _network._host = this;
            }

            public void Connect(string host, int port)
            {
                var hostEndpoint = _network._host;
                if (hostEndpoint == null)
                {
                    _events.Enqueue(NetEvent.Disconnected(0, "could not connect: no host"));
                    return;
                }

                int clientId = _network._nextPeerId++;
                _peers[0] = (hostEndpoint, clientId);
                hostEndpoint._peers[clientId] = (this, 0);

                hostEndpoint._events.Enqueue(NetEvent.Connected(clientId));
                _events.Enqueue(NetEvent.Connected(0));
            }

            public void Send(int peerId, byte[] message)
            {
                if (_peers.TryGetValue(peerId, out var peer))
                {
                    peer.Remote._events.Enqueue(NetEvent.Received(peer.IdThere, (byte[]) message.Clone()));
                }
            }

            public long PendingSendBytes(int peerId) => 0;

            public void Disconnect(int peerId)
            {
                if (_peers.Remove(peerId, out var peer))
                {
                    peer.Remote._peers.Remove(peer.IdThere);
                    peer.Remote._events.Enqueue(NetEvent.Disconnected(peer.IdThere, "connection closed"));
                    _events.Enqueue(NetEvent.Disconnected(peerId, "disconnected locally"));
                }
            }

            public void Poll(INetEventListener listener)
            {
                while (_events.Count > 0)
                {
                    _events.Dequeue().Dispatch(listener);
                }
            }

            public void Dispose()
            {
                foreach (var (remote, idThere) in _peers.Values)
                {
                    remote._peers.Remove(idThere);
                    remote._events.Enqueue(NetEvent.Disconnected(idThere, "connection closed"));
                }
                _peers.Clear();
                _events.Clear();

                if (_network._host == this)
                {
                    _network._host = null;
                }
            }
        }

        private Endpoint? _host;
        private int       _nextPeerId = 1;

        public INetTransport CreateHost() => new Endpoint(this);

        public INetTransport CreateClient() => new Endpoint(this);
    }
}
