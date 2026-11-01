using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YARG.Relay
{
    /// <summary>
    /// One client socket. Everything sent to it goes through a queue drained by a single writer loop, since a
    /// WebSocket allows only one send at a time.
    /// </summary>
    /// <remarks>
    /// Control messages are never held back. Forwarded data waits for one of a fixed number of slots, so a
    /// socket that reads slowly makes its senders wait instead of filling the relay's memory, and one that
    /// stops reading altogether is dropped after the stall timeout.
    /// </remarks>
    public sealed class RelayConnection
    {
        private readonly struct Outgoing
        {
            public readonly byte[]                Data;
            public readonly WebSocketMessageType  Type;
            public readonly WebSocketCloseStatus? Close;

            public Outgoing(byte[] data, WebSocketMessageType type, WebSocketCloseStatus? close)
            {
                Data = data;
                Type = type;
                Close = close;
            }
        }

        private static readonly TimeSpan CLOSE_HANDSHAKE_TIMEOUT = TimeSpan.FromSeconds(5);

        private readonly WebSocket         _socket;
        private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim     _dataSlots;
        private readonly TimeSpan          _stallTimeout;
        private          int               _closing;

        public string Ip { get; }

        // Guarded by the registry's lock
        public Room?  Room   { get; set; }
        public ushort PeerId { get; set; }

        public bool IsHost => Room != null && Room.Host == this;

        public RelayConnection(WebSocket socket, string ip, RelayOptions options)
        {
            _socket = socket;
            Ip = ip;
            _dataSlots = new SemaphoreSlim(options.SendQueueFrames, options.SendQueueFrames);
            _stallTimeout = options.SendStallTimeout;
        }

        public void SendJson(object message)
        {
            _outgoing.Writer.TryWrite(new Outgoing(JsonSerializer.SerializeToUtf8Bytes(message),
                WebSocketMessageType.Text, null));
        }

        public void SendError(string code) => SendJson(new { type = "error", code });

        /// <summary>
        /// Queues forwarded data, waiting while this socket's queue is full. A socket that stays full past the
        /// stall timeout is aborted.
        /// </summary>
        public async Task SendDataAsync(byte[] frame)
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }

            if (!await _dataSlots.WaitAsync(_stallTimeout))
            {
                Abort();
                return;
            }

            if (!_outgoing.Writer.TryWrite(new Outgoing(frame, WebSocketMessageType.Binary, null)))
            {
                _dataSlots.Release();
            }
        }

        /// <summary>
        /// Sends what's queued, then the close frame; nothing queued afterwards is sent.
        /// </summary>
        public void CloseAfterSending(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
            {
                return;
            }
            _outgoing.Writer.TryWrite(new Outgoing(Array.Empty<byte>(), WebSocketMessageType.Close, status));
            _outgoing.Writer.TryComplete();
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _closing, 1);
            _outgoing.Writer.TryComplete();
            _socket.Abort();
        }

        public async Task RunWriterAsync()
        {
            try
            {
                await foreach (var frame in _outgoing.Reader.ReadAllAsync())
                {
                    if (frame.Close.HasValue)
                    {
                        if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                        {
                            await _socket.CloseOutputAsync(frame.Close.Value, null, CancellationToken.None);
                        }

                        // The peer answers the close; one that doesn't is cut off
                        _ = Task.Delay(CLOSE_HANDSHAKE_TIMEOUT).ContinueWith(_ => _socket.Abort(),
                            TaskScheduler.Default);
                        return;
                    }

                    await _socket.SendAsync(frame.Data, frame.Type, true, CancellationToken.None);
                    if (frame.Type == WebSocketMessageType.Binary)
                    {
                        _dataSlots.Release();
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException ||
                e is ObjectDisposedException || e is IOException || e is InvalidOperationException)
            {
                Abort();
            }
        }
    }
}
