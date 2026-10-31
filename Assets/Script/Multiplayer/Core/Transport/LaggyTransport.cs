using System;
using System.Collections.Generic;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// Test decorator that delays everything received from the inner transport by a latency
    /// plus random jitter, while keeping per-peer order (like TCP).
    /// </summary>
    public sealed class LaggyTransport : INetTransport, INetEventListener
    {
        private readonly INetTransport _inner;
        private readonly Func<double>  _clock;
        private readonly double        _latency;
        private readonly double        _jitter;
        private readonly Random        _random;

        private readonly List<(double DeliverAt, NetEvent Event)> _pending      = new();
        private readonly Dictionary<int, double>                  _lastDeliverAt = new();

        public LaggyTransport(INetTransport inner, Func<double> clock, double latencySeconds, double jitterSeconds,
            int seed)
        {
            _inner = inner;
            _clock = clock;
            _latency = latencySeconds;
            _jitter = jitterSeconds;
            _random = new Random(seed);
        }

        public void StartHost(int port) => _inner.StartHost(port);

        public void Connect(string host, int port) => _inner.Connect(host, port);

        public void Send(int peerId, byte[] message) => _inner.Send(peerId, message);

        public long PendingSendBytes(int peerId) => _inner.PendingSendBytes(peerId);

        public void StartKeepAlive(int peerId) => _inner.StartKeepAlive(peerId);

        public void Disconnect(int peerId) => _inner.Disconnect(peerId);

        public void Poll(INetEventListener listener)
        {
            _inner.Poll(this);

            double now = _clock();
            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var (deliverAt, e) = _pending[i];
                if (deliverAt <= now)
                {
                    e.Dispatch(listener);
                }
                else
                {
                    _pending[kept++] = _pending[i];
                }
            }
            _pending.RemoveRange(kept, _pending.Count - kept);
        }

        public void Dispose() => _inner.Dispose();

        void INetEventListener.OnPeerConnected(int peerId) => Delay(NetEvent.Connected(peerId));

        void INetEventListener.OnPeerDisconnected(int peerId, string reason) =>
            Delay(NetEvent.Disconnected(peerId, reason));

        void INetEventListener.OnMessage(int peerId, byte[] message) => Delay(NetEvent.Received(peerId, message));

        private void Delay(NetEvent e)
        {
            double deliverAt = _clock() + _latency + _random.NextDouble() * _jitter;
            if (_lastDeliverAt.TryGetValue(e.PeerId, out double last))
            {
                deliverAt = Math.Max(deliverAt, last);
            }
            _lastDeliverAt[e.PeerId] = deliverAt;
            _pending.Add((deliverAt, e));
        }
    }
}
