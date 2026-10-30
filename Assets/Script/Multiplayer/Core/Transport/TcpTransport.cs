using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// TCP transport. Each connection has a reader thread and a writer thread, so neither
    /// <see cref="Send"/> nor <see cref="Poll"/> ever blocks on the network.
    /// </summary>
    public sealed class TcpTransport : INetTransport
    {
        private const int HOST_PEER_ID = 0;

        private sealed class Connection
        {
            public readonly int                         PeerId;
            public readonly BlockingCollection<byte[]>  Outgoing = new();
            public          TcpClient?                  Client;
            private         int                         _closed;

            public Connection(int peerId)
            {
                PeerId = peerId;
            }

            /// <summary>
            /// Returns true only for the first call, so each peer gets exactly one disconnect event.
            /// </summary>
            public bool TryClose()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                {
                    return false;
                }
                Outgoing.CompleteAdding();
                Client?.Close();
                return true;
            }
        }

        private readonly ConcurrentQueue<NetEvent>        _events      = new();
        private readonly ConcurrentDictionary<int, Connection> _connections = new();

        private TcpListener? _listener;
        private int          _nextPeerId = 1;
        private volatile bool _disposed;

        /// <summary>
        /// The port actually listened on (useful after <c>StartHost(0)</c>).
        /// </summary>
        public int LocalPort => _listener != null ? ((IPEndPoint) _listener.LocalEndpoint).Port : 0;

        public void StartHost(int port)
        {
            // Dual mode accepts both IPv4 and IPv6 clients (Tailscale hands out both)
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start();
            _listener = listener;

            StartThread(AcceptLoop, "Multiplayer accept");
        }

        public void Connect(string host, int port)
        {
            var connection = new Connection(HOST_PEER_ID);
            _connections[HOST_PEER_ID] = connection;

            StartThread(() =>
            {
                var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
                client.Client.DualMode = true;
                try
                {
                    client.Connect(host, port);
                }
                catch (Exception e) when (e is SocketException || e is IOException || e is ArgumentException)
                {
                    client.Close();
                    if (connection.TryClose())
                    {
                        _events.Enqueue(NetEvent.Disconnected(HOST_PEER_ID, $"could not connect: {e.Message}"));
                    }
                    return;
                }

                connection.Client = client;
                Run(connection);
            }, "Multiplayer connect");
        }

        public void Send(int peerId, byte[] message)
        {
            if (message.Length > ProtocolInfo.MaxMessageSize)
            {
                throw new ArgumentException($"Message of {message.Length} bytes exceeds the maximum size");
            }

            if (_connections.TryGetValue(peerId, out var connection) && !connection.Outgoing.IsAddingCompleted)
            {
                try
                {
                    connection.Outgoing.Add(MessageFramer.Frame(message));
                }
                catch (InvalidOperationException)
                {
                    // Closed concurrently; the disconnect event is already queued
                }
            }
        }

        public void Disconnect(int peerId)
        {
            if (_connections.TryRemove(peerId, out var connection) && connection.TryClose())
            {
                _events.Enqueue(NetEvent.Disconnected(peerId, "disconnected locally"));
            }
        }

        public void Poll(INetEventListener listener)
        {
            while (_events.TryDequeue(out var e))
            {
                e.Dispatch(listener);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _listener?.Stop();
            foreach (var connection in _connections.Values)
            {
                connection.TryClose();
            }
            _connections.Clear();
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception e) when (e is SocketException || e is ObjectDisposedException ||
                    e is InvalidOperationException)
                {
                    return;
                }

                client.NoDelay = true;
                var connection = new Connection(Interlocked.Increment(ref _nextPeerId) - 1)
                {
                    Client = client
                };
                _connections[connection.PeerId] = connection;
                Run(connection);
            }
        }

        private void Run(Connection connection)
        {
            if (_disposed)
            {
                connection.TryClose();
                return;
            }

            _events.Enqueue(NetEvent.Connected(connection.PeerId));
            StartThread(() => WriteLoop(connection), $"Multiplayer write {connection.PeerId}");
            StartThread(() => ReadLoop(connection), $"Multiplayer read {connection.PeerId}");
        }

        private void ReadLoop(Connection connection)
        {
            var framer = new MessageFramer();
            var buffer = new byte[64 * 1024];
            string reason;
            try
            {
                var stream = connection.Client!.GetStream();
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        reason = "connection closed";
                        break;
                    }

                    framer.Append(buffer.AsSpan(0, read));
                    while (framer.TryRead(out var message))
                    {
                        _events.Enqueue(NetEvent.Received(connection.PeerId, message));
                    }
                }
            }
            catch (InvalidDataException)
            {
                reason = "message too large";
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException ||
                e is InvalidOperationException)
            {
                reason = e.Message;
            }

            Close(connection, reason);
        }

        private void WriteLoop(Connection connection)
        {
            try
            {
                var stream = connection.Client!.GetStream();
                foreach (var framed in connection.Outgoing.GetConsumingEnumerable())
                {
                    stream.Write(framed, 0, framed.Length);
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException ||
                e is InvalidOperationException)
            {
                Close(connection, e.Message);
            }
        }

        private void Close(Connection connection, string reason)
        {
            if (connection.TryClose())
            {
                _connections.TryRemove(new KeyValuePair<int, Connection>(connection.PeerId, connection));
                if (!_disposed)
                {
                    _events.Enqueue(NetEvent.Disconnected(connection.PeerId, reason));
                }
            }
        }

        private static void StartThread(Action action, string name)
        {
            new Thread(() => action())
            {
                IsBackground = true,
                Name = name,
            }.Start();
        }
    }
}
