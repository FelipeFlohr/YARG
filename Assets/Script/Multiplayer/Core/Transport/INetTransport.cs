using System;

namespace YARG.Multiplayer.Transport
{
    public interface INetEventListener
    {
        void OnPeerConnected(int peerId);
        void OnPeerDisconnected(int peerId, string reason);
        void OnMessage(int peerId, byte[] message);
    }

    /// <summary>
    /// Reliable, ordered delivery of whole messages between a host and its clients.
    /// Events are queued and only delivered from <see cref="Poll"/>, on the calling thread.
    /// </summary>
    public interface INetTransport : IDisposable
    {
        /// <summary>
        /// Starts accepting clients. Client peer IDs start at 1.
        /// </summary>
        void StartHost(int port);

        /// <summary>
        /// Connects to a host, which is always peer 0. Completion (or failure) is reported
        /// through <see cref="INetEventListener"/>.
        /// </summary>
        void Connect(string host, int port);

        /// <summary>
        /// Sends a whole message; framing is the transport's job. Messages sent before the
        /// connection completes are queued.
        /// </summary>
        void Send(int peerId, byte[] message);

        /// <summary>
        /// Bytes queued for a peer that haven't been handed to the network yet (0 for unknown peers).
        /// Bulk senders use it as backpressure.
        /// </summary>
        long PendingSendBytes(int peerId);

        /// <summary>
        /// Closes the connection to a peer after the messages already queued for it are sent.
        /// A disconnect event is still raised for it.
        /// </summary>
        void Disconnect(int peerId);

        void Poll(INetEventListener listener);
    }

    internal readonly struct NetEvent
    {
        public enum EventKind
        {
            Connected,
            Disconnected,
            Message,
        }

        public readonly EventKind Kind;
        public readonly int       PeerId;
        public readonly string?   Reason;
        public readonly byte[]?   Message;

        private NetEvent(EventKind kind, int peerId, string? reason, byte[]? message)
        {
            Kind = kind;
            PeerId = peerId;
            Reason = reason;
            Message = message;
        }

        public static NetEvent Connected(int peerId) => new(EventKind.Connected, peerId, null, null);

        public static NetEvent Disconnected(int peerId, string reason) =>
            new(EventKind.Disconnected, peerId, reason, null);

        public static NetEvent Received(int peerId, byte[] message) =>
            new(EventKind.Message, peerId, null, message);

        public void Dispatch(INetEventListener listener)
        {
            switch (Kind)
            {
                case EventKind.Connected:
                    listener.OnPeerConnected(PeerId);
                    break;
                case EventKind.Disconnected:
                    listener.OnPeerDisconnected(PeerId, Reason!);
                    break;
                case EventKind.Message:
                    listener.OnMessage(PeerId, Message!);
                    break;
            }
        }
    }
}
