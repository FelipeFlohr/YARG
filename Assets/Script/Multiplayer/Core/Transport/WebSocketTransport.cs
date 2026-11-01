using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// Transport through the WebSocket relay (<c>Server/YARG.Relay</c>), for the web build. One socket carries
    /// the relay's JSON control messages and the game's messages, framed as <c>[peerId u16 LE][message]</c>.
    /// </summary>
    /// <remarks>
    /// Nothing here uses threads or timers: <see cref="Pump"/> (also called by <see cref="Poll"/>) reads what the
    /// socket received and completes the room requests. Whoever awaits <see cref="ListRoomsAsync"/>,
    /// <see cref="CreateRoomAsync"/> or <see cref="JoinRoomAsync"/> keeps pumping until the task is done.
    /// A room has to be created (host) or joined (client) before the session calls <see cref="StartHost"/> or
    /// <see cref="Connect"/>, whose arguments are ignored.
    /// </remarks>
    public sealed class WebSocketTransport : INetTransport
    {
        private const int HOST_PEER_ID = 0;
        private const int HEADER_SIZE  = 2;

        private enum Role
        {
            None,
            Host,
            Client,
        }

        private sealed class Request
        {
            public readonly string            Text;
            public readonly Action<JObject>   Reply;
            public readonly Action<Exception> Fail;

            public Request(string text, Action<JObject> reply, Action<Exception> fail)
            {
                Text = text;
                Reply = reply;
                Fail = fail;
            }
        }

        private readonly IWebSocket      _socket;
        private readonly Queue<NetEvent> _events  = new();
        private readonly Queue<Request>  _unsent  = new();
        private readonly Queue<Request>  _waiting = new();
        private readonly HashSet<int>    _peers   = new();

        // Messages from the host that arrive before the client session connects
        private readonly Queue<byte[]> _early = new();

        private Role    _role;
        private bool    _started;
        private bool    _closeHandled;
        private bool    _disconnectedLocally;
        private bool    _disposed;
        private string? _relayCloseReason;

        public string? RoomId      { get; private set; }
        public int     LocalPeerId { get; private set; } = -1;

        public bool IsHost   => _role == Role.Host;
        public bool IsInRoom => _role != Role.None;
        public bool IsClosed => _socket.Status == WebSocketStatus.Closed;

        public string CloseReason => _relayCloseReason ?? _socket.CloseReason ?? "connection to the relay closed";

        /// <summary>
        /// Opens <paramref name="socket"/> to <paramref name="url"/> (the relay's <c>/ws</c> endpoint).
        /// </summary>
        public WebSocketTransport(IWebSocket socket, string url)
        {
            _socket = socket;
            _socket.Open(url);
        }

        public Task<IReadOnlyList<RelayRoom>> ListRoomsAsync()
        {
            return SendRequest<IReadOnlyList<RelayRoom>>(new JObject { ["type"] = "list" }, "rooms", reply =>
            {
                var rooms = new List<RelayRoom>();
                foreach (var room in (JArray) reply["rooms"]!)
                {
                    rooms.Add(new RelayRoom((string) room["id"]!, (string) room["name"]!, (int) room["players"]!,
                        (int) room["max"]!, (bool) room["playing"]!));
                }
                return rooms;
            });
        }

        /// <returns>The new room's ID. This transport is its host from then on.</returns>
        public Task<string> CreateRoomAsync(string name, string password, int maxPlayers)
        {
            EnsureNotInRoom();
            var message = new JObject
            {
                ["type"] = "create",
                ["name"] = name,
                ["password"] = password,
                ["max"] = maxPlayers,
            };
            return SendRequest<string>(message, "created", reply =>
            {
                string id = (string) reply["room"]!;
                _role = Role.Host;
                RoomId = id;
                LocalPeerId = HOST_PEER_ID;
                return id;
            });
        }

        /// <summary>
        /// Joins a room; this transport is a client of its host from then on.
        /// </summary>
        public Task JoinRoomAsync(string roomId, string password)
        {
            EnsureNotInRoom();
            var message = new JObject
            {
                ["type"] = "join",
                ["room"] = roomId,
                ["password"] = password,
            };
            return SendRequest<bool>(message, "joined", reply =>
            {
                _role = Role.Client;
                RoomId = (string) reply["room"]!;
                LocalPeerId = (int) reply["peer"]!;
                return true;
            });
        }

        /// <summary>
        /// Host only: shows the room as in a match (or not) in the relay's list.
        /// </summary>
        public void SetRoomPlaying(bool playing)
        {
            if (IsHost && _socket.Status == WebSocketStatus.Open)
            {
                SendControl(new JObject { ["type"] = "state", ["playing"] = playing });
            }
        }

        public void StartHost(int port)
        {
            if (_role != Role.Host)
            {
                throw new InvalidOperationException("Create a relay room before hosting");
            }
            _started = true;
        }

        public void Connect(string host, int port)
        {
            if (_role != Role.Client)
            {
                throw new InvalidOperationException("Join a relay room before connecting");
            }

            _started = true;
            if (_closeHandled)
            {
                _events.Enqueue(NetEvent.Disconnected(HOST_PEER_ID, CloseReason));
                return;
            }

            _events.Enqueue(NetEvent.Connected(HOST_PEER_ID));
            while (_early.Count > 0)
            {
                _events.Enqueue(NetEvent.Received(HOST_PEER_ID, _early.Dequeue()));
            }
        }

        public void Send(int peerId, byte[] message)
        {
            if (message.Length > ProtocolInfo.MaxMessageSize)
            {
                throw new ArgumentException($"Message of {message.Length} bytes exceeds the maximum size");
            }

            if (_socket.Status != WebSocketStatus.Open || !IsPeer(peerId))
            {
                return;
            }

            // A client's frames always go to the host, whatever the header says
            var frame = new byte[message.Length + HEADER_SIZE];
            frame[0] = (byte) peerId;
            frame[1] = (byte) (peerId >> 8);
            Buffer.BlockCopy(message, 0, frame, HEADER_SIZE, message.Length);
            _socket.SendBinary(frame);
        }

        /// <remarks>
        /// All peers share one socket, so this is everything queued on it.
        /// </remarks>
        public long PendingSendBytes(int peerId) => IsPeer(peerId) ? _socket.BufferedAmount : 0;

        /// <summary>
        /// Nothing to do: the relay pings every socket and the browser answers outside the game's main thread.
        /// </summary>
        public void StartKeepAlive(int peerId)
        {
        }

        public void Disconnect(int peerId)
        {
            if (_role == Role.Host)
            {
                if (_peers.Remove(peerId))
                {
                    if (_socket.Status == WebSocketStatus.Open)
                    {
                        SendControl(new JObject { ["type"] = "kick", ["peer"] = peerId });
                    }
                    _events.Enqueue(NetEvent.Disconnected(peerId, "disconnected locally"));
                }
            }
            else if (_role == Role.Client && peerId == HOST_PEER_ID && _started && !_disconnectedLocally)
            {
                _disconnectedLocally = true;
                _socket.Close();
                _events.Enqueue(NetEvent.Disconnected(HOST_PEER_ID, "disconnected locally"));
            }
        }

        public void Poll(INetEventListener listener)
        {
            Pump();
            while (_events.Count > 0)
            {
                _events.Dequeue().Dispatch(listener);
            }
        }

        /// <summary>
        /// Sends the requests made while connecting, reads everything received and notices a closed socket.
        /// </summary>
        public void Pump()
        {
            if (_disposed)
            {
                return;
            }

            if (_socket.Status == WebSocketStatus.Open)
            {
                while (_unsent.Count > 0)
                {
                    var request = _unsent.Dequeue();
                    _socket.SendText(request.Text);
                    _waiting.Enqueue(request);
                }
            }

            while (_socket.TryReceive(out var frame))
            {
                if (frame.IsText)
                {
                    HandleControl(frame.Text!);
                }
                else
                {
                    HandleData(frame.Data!);
                }
            }

            if (_socket.Status == WebSocketStatus.Closed && !_closeHandled)
            {
                HandleClosed();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _socket.Close();
            _socket.Dispose();
            FailRequests(new RelayException(RelayException.CONNECTION_LOST, "transport disposed"));
            _events.Clear();
        }

        private bool IsPeer(int peerId) => _role switch
        {
            Role.Host   => _peers.Contains(peerId),
            Role.Client => peerId == HOST_PEER_ID && _started && !_disconnectedLocally,
            _           => false,
        };

        private void EnsureNotInRoom()
        {
            if (_role != Role.None)
            {
                throw new InvalidOperationException("Already in a relay room");
            }
        }

        private void SendControl(JObject message)
        {
            _socket.SendText(message.ToString(Formatting.None));
        }

        private Task<T> SendRequest<T>(JObject message, string replyType, Func<JObject, T> parse)
        {
            var completion = new TaskCompletionSource<T>();
            if (_disposed || _closeHandled)
            {
                completion.SetException(new RelayException(RelayException.CONNECTION_LOST, CloseReason));
                return completion.Task;
            }

            var request = new Request(message.ToString(Formatting.None), reply =>
            {
                string? type = (string?) reply["type"];
                if (type == "error")
                {
                    completion.TrySetException(new RelayException((string?) reply["code"] ?? "unknown"));
                    return;
                }
                if (type != replyType)
                {
                    completion.TrySetException(new RelayException(RelayException.BAD_REQUEST,
                        $"expected {replyType}, got {type}"));
                    return;
                }

                T result;
                try
                {
                    result = parse(reply);
                }
                catch (Exception e) when (e is InvalidCastException || e is NullReferenceException ||
                    e is FormatException || e is ArgumentException || e is OverflowException)
                {
                    completion.TrySetException(new RelayException(RelayException.BAD_REQUEST, e.Message));
                    return;
                }
                completion.TrySetResult(result);
            }, error => completion.TrySetException(error));

            if (_socket.Status == WebSocketStatus.Open && _unsent.Count == 0)
            {
                _socket.SendText(request.Text);
                _waiting.Enqueue(request);
            }
            else
            {
                _unsent.Enqueue(request);
            }
            return completion.Task;
        }

        private void HandleControl(string text)
        {
            JObject message;
            try
            {
                message = JObject.Parse(text);
            }
            catch (JsonException)
            {
                return;
            }

            switch ((string?) message["type"])
            {
                case "rooms":
                case "created":
                case "joined":
                case "error":
                    if (_waiting.Count > 0)
                    {
                        _waiting.Dequeue().Reply(message);
                    }
                    else if ((string?) message["type"] == "error")
                    {
                        // Not an answer: the relay is about to drop the socket (a frame too large)
                        _relayCloseReason = $"relay error: {(string?) message["code"]}";
                    }
                    break;
                case "peer_joined":
                    if (_role == Role.Host && TryGetPeer(message, out int joined) && _peers.Add(joined))
                    {
                        _events.Enqueue(NetEvent.Connected(joined));
                    }
                    break;
                case "peer_left":
                    if (_role == Role.Host && TryGetPeer(message, out int left) && _peers.Remove(left))
                    {
                        _events.Enqueue(NetEvent.Disconnected(left, "connection closed"));
                    }
                    break;
                case "closed":
                    _relayCloseReason = (string?) message["reason"] switch
                    {
                        "host_left" => "the host left",
                        "idle"      => "the room was idle for too long",
                        "kicked"    => "removed by the host",
                        var other   => $"room closed ({other})",
                    };
                    break;
            }
        }

        private static bool TryGetPeer(JObject message, out int peerId)
        {
            var token = message["peer"];
            if (token != null && token.Type == JTokenType.Integer)
            {
                peerId = (int) token;
                return true;
            }
            peerId = -1;
            return false;
        }

        private void HandleData(byte[] frame)
        {
            if (frame.Length < HEADER_SIZE)
            {
                return;
            }

            int peerId = frame[0] | (frame[1] << 8);
            var message = new byte[frame.Length - HEADER_SIZE];
            Buffer.BlockCopy(frame, HEADER_SIZE, message, 0, message.Length);

            if (_role == Role.Host)
            {
                if (_peers.Contains(peerId))
                {
                    _events.Enqueue(NetEvent.Received(peerId, message));
                }
            }
            else if (_role == Role.Client && !_disconnectedLocally)
            {
                if (_started)
                {
                    _events.Enqueue(NetEvent.Received(HOST_PEER_ID, message));
                }
                else
                {
                    _early.Enqueue(message);
                }
            }
        }

        private void HandleClosed()
        {
            _closeHandled = true;
            string reason = CloseReason;
            FailRequests(new RelayException(RelayException.CONNECTION_LOST, reason));

            if (_role == Role.Host)
            {
                foreach (int peerId in _peers)
                {
                    _events.Enqueue(NetEvent.Disconnected(peerId, reason));
                }
                _peers.Clear();
            }
            else if (_role == Role.Client && _started && !_disconnectedLocally)
            {
                _events.Enqueue(NetEvent.Disconnected(HOST_PEER_ID, reason));
            }
        }

        private void FailRequests(Exception error)
        {
            var requests = new List<Request>(_waiting);
            requests.AddRange(_unsent);
            _waiting.Clear();
            _unsent.Clear();
            foreach (var request in requests)
            {
                request.Fail(error);
            }
        }
    }
}
