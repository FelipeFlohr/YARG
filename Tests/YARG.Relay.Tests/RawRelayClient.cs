using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace YARG.Relay.Tests
{
    /// <summary>
    /// A bare WebSocket speaking the relay protocol, one awaited frame at a time.
    /// </summary>
    public sealed class RawRelayClient : IAsyncDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        public readonly struct Frame
        {
            public readonly bool                  IsClose;
            public readonly WebSocketCloseStatus? CloseStatus;
            public readonly JsonElement           Json;
            public readonly byte[]?               Data;

            public Frame(bool isClose, WebSocketCloseStatus? closeStatus, JsonElement json, byte[]? data)
            {
                IsClose = isClose;
                CloseStatus = closeStatus;
                Json = json;
                Data = data;
            }

            public bool IsText => !IsClose && Data == null;
        }

        private readonly ClientWebSocket _socket;

        private RawRelayClient(ClientWebSocket socket)
        {
            _socket = socket;
        }

        public WebSocketState State => _socket.State;

        /// <param name="ip">Sent as X-Forwarded-For, which the test relay trusts, to act as another machine.</param>
        public static async Task<RawRelayClient> ConnectAsync(Uri uri, string ip = "10.0.0.1")
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Forwarded-For", ip);
            using var timeout = new CancellationTokenSource(DefaultTimeout);
            await socket.ConnectAsync(uri, timeout.Token);
            return new RawRelayClient(socket);
        }

        public Task SendJsonAsync(object message) =>
            SendTextAsync(JsonSerializer.Serialize(message));

        public Task SendTextAsync(string text) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        public Task SendBinaryAsync(ushort peerId, byte[] payload)
        {
            var frame = new byte[payload.Length + 2];
            frame[0] = (byte) peerId;
            frame[1] = (byte) (peerId >> 8);
            payload.CopyTo(frame, 2);
            return _socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public async Task<Frame> ReceiveAsync(TimeSpan? timeout = null)
        {
            using var cancel = new CancellationTokenSource(timeout ?? DefaultTimeout);
            using var data = new MemoryStream();
            var buffer = new byte[64 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancel.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    return new Frame(true, result.CloseStatus, default, null);
                }
                data.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                using var document = JsonDocument.Parse(data.ToArray());
                return new Frame(false, null, document.RootElement.Clone(), null);
            }
            return new Frame(false, null, default, data.ToArray());
        }

        public async Task<JsonElement> ReceiveJsonAsync()
        {
            var frame = await ReceiveAsync();
            Assert.That(frame.IsText, Is.True, "expected a text frame");
            return frame.Json;
        }

        /// <returns>The peer ID and payload of a binary frame.</returns>
        public async Task<(ushort PeerId, byte[] Payload)> ReceiveDataAsync()
        {
            var frame = await ReceiveAsync();
            Assert.That(frame.Data, Is.Not.Null, "expected a binary frame, got " + (frame.IsClose ? "close" : frame.Json.ToString()));
            return ((ushort) (frame.Data![0] | (frame.Data[1] << 8)), frame.Data.AsSpan(2).ToArray());
        }

        public async Task<JsonElement> RequestAsync(object message)
        {
            await SendJsonAsync(message);
            return await ReceiveJsonAsync();
        }

        /// <returns>The room ID.</returns>
        public async Task<string> CreateAsync(string name = "Sala", string password = "segredo", int max = 4)
        {
            var reply = await RequestAsync(new { type = "create", name, password, max });
            Assert.That(reply.GetProperty("type").GetString(), Is.EqualTo("created"), reply.ToString());
            return reply.GetProperty("room").GetString()!;
        }

        /// <returns>The peer ID.</returns>
        public async Task<int> JoinAsync(string room, string password = "segredo")
        {
            var reply = await RequestAsync(new { type = "join", room, password });
            Assert.That(reply.GetProperty("type").GetString(), Is.EqualTo("joined"), reply.ToString());
            return reply.GetProperty("peer").GetInt32();
        }

        public async Task CloseAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(DefaultTimeout);
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }

        public ValueTask DisposeAsync()
        {
            _socket.Abort();
            _socket.Dispose();
            return default;
        }
    }
}
