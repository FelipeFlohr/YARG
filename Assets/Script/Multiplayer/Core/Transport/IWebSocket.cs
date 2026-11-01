using System;

namespace YARG.Multiplayer.Transport
{
    public enum WebSocketStatus
    {
        Connecting,
        Open,
        Closed,
    }

    public readonly struct WebSocketFrame
    {
        public readonly string? Text;
        public readonly byte[]? Data;

        private WebSocketFrame(string? text, byte[]? data)
        {
            Text = text;
            Data = data;
        }

        public bool IsText => Text != null;

        public static WebSocketFrame FromText(string text) => new(text, null);

        public static WebSocketFrame FromData(byte[] data) => new(null, data);
    }

    /// <summary>
    /// The little a <see cref="WebSocketTransport"/> needs from a WebSocket. Polled, never calls back:
    /// in the browser everything runs on the main thread, so received frames wait in a queue until read.
    /// </summary>
    public interface IWebSocket : IDisposable
    {
        WebSocketStatus Status { get; }

        /// <summary>
        /// Why the socket closed, once <see cref="Status"/> is <see cref="WebSocketStatus.Closed"/>.
        /// </summary>
        string? CloseReason { get; }

        /// <summary>
        /// Bytes passed to the send methods that haven't been handed to the network yet.
        /// </summary>
        long BufferedAmount { get; }

        void Open(string url);

        /// <summary>
        /// Only valid while open; sends while connecting or closed are dropped.
        /// </summary>
        void SendText(string text);

        /// <inheritdoc cref="SendText"/>
        void SendBinary(byte[] data);

        /// <summary>
        /// Takes the oldest received frame. Frames received before closing are still returned after it.
        /// </summary>
        bool TryReceive(out WebSocketFrame frame);

        /// <summary>
        /// Closes after the frames already sent are flushed.
        /// </summary>
        void Close();
    }
}
