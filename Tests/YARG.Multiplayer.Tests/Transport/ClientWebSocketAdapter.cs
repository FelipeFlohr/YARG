using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Tests.Transport
{
    /// <summary>
    /// <see cref="IWebSocket"/> over <see cref="ClientWebSocket"/>, with background send and receive loops that
    /// queue frames like the browser does.
    /// </summary>
    public sealed class ClientWebSocketAdapter : IWebSocket
    {
        private readonly ClientWebSocket                                _socket   = new();
        private readonly ConcurrentQueue<WebSocketFrame>                _received = new();
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _outgoing =
            Channel.CreateUnbounded<(byte[], WebSocketMessageType)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource                        _cancel   = new();

        private          long    _buffered;
        private volatile int     _status = (int) WebSocketStatus.Connecting;
        private volatile string? _closeReason;

        public WebSocketStatus Status         => (WebSocketStatus) _status;
        public string?         CloseReason    => _closeReason;
        public long            BufferedAmount => Interlocked.Read(ref _buffered);

        /// <param name="forwardedFor">Sent as X-Forwarded-For, so each test client looks like its own machine.</param>
        public ClientWebSocketAdapter(string? forwardedFor = null)
        {
            if (forwardedFor != null)
            {
                _socket.Options.SetRequestHeader("X-Forwarded-For", forwardedFor);
            }
        }

        public void Open(string url)
        {
            Task.Run(() => RunAsync(new Uri(url)));
        }

        public void SendText(string text) => Enqueue(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

        public void SendBinary(byte[] data) => Enqueue(data, WebSocketMessageType.Binary);

        public bool TryReceive(out WebSocketFrame frame) => _received.TryDequeue(out frame);

        public void Close()
        {
            if (Status == WebSocketStatus.Connecting)
            {
                _cancel.Cancel();
            }
            _outgoing.Writer.TryComplete();
        }

        public void Dispose()
        {
            Close();
            _cancel.Cancel();
            _socket.Abort();
            _socket.Dispose();
        }

        private void Enqueue(byte[] data, WebSocketMessageType type)
        {
            if (Status != WebSocketStatus.Open)
            {
                return;
            }
            Interlocked.Add(ref _buffered, data.Length);
            if (!_outgoing.Writer.TryWrite((data, type)))
            {
                Interlocked.Add(ref _buffered, -data.Length);
            }
        }

        private async Task RunAsync(Uri uri)
        {
            try
            {
                await _socket.ConnectAsync(uri, _cancel.Token);
            }
            catch (Exception e)
            {
                SetClosed($"could not connect: {e.Message}");
                return;
            }

            _status = (int) WebSocketStatus.Open;
            var sending = SendLoop();
            await ReceiveLoop();
            await sending;
        }

        private async Task SendLoop()
        {
            try
            {
                await foreach (var (data, type) in _outgoing.Reader.ReadAllAsync(_cancel.Token))
                {
                    await _socket.SendAsync(data, type, true, _cancel.Token);
                    Interlocked.Add(ref _buffered, -data.Length);
                }

                // Closed locally once everything was sent
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _cancel.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException ||
                e is ObjectDisposedException || e is InvalidOperationException)
            {
                // The receive loop reports the close
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveAsync(buffer, _cancel.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetClosed(result.CloseStatusDescription ?? $"closed ({result.CloseStatus})");
                        _outgoing.Writer.TryComplete();
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var bytes = message.ToArray();
                    message.SetLength(0);
                    _received.Enqueue(result.MessageType == WebSocketMessageType.Text
                        ? WebSocketFrame.FromText(Encoding.UTF8.GetString(bytes))
                        : WebSocketFrame.FromData(bytes));
                }
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException ||
                e is ObjectDisposedException || e is InvalidOperationException)
            {
                SetClosed(e.Message);
                _outgoing.Writer.TryComplete();
            }
        }

        private void SetClosed(string reason)
        {
            _closeReason ??= reason;
            _status = (int) WebSocketStatus.Closed;
        }
    }
}
