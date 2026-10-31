using System.Collections.Generic;
using System.Linq;

namespace YARG.Multiplayer.Sync
{
    /// <summary>
    /// Estimates the offset between the host's clock and the local clock from ping/pong
    /// round trips. Only monotonic clocks should be fed in, never wall-clock time.
    /// </summary>
    public sealed class ClockSync
    {
        public const int WindowSize = 20;
        public const int BestSamples = 5;

        private readonly Queue<(double Rtt, double Offset)> _samples = new();

        public bool HasEstimate => _samples.Count > 0;

        /// <summary>
        /// Host time minus local time.
        /// </summary>
        public double OffsetSeconds { get; private set; }

        public void AddSample(double pingSentLocal, double hostReplyTime, double pongReceivedLocal)
        {
            double rtt = pongReceivedLocal - pingSentLocal;
            double offset = hostReplyTime - (pingSentLocal + pongReceivedLocal) / 2;

            _samples.Enqueue((rtt, offset));
            if (_samples.Count > WindowSize)
            {
                _samples.Dequeue();
            }

            // The fastest round trips have the least room for asymmetric delay
            OffsetSeconds = _samples
                .OrderBy(s => s.Rtt)
                .Take(BestSamples)
                .Average(s => s.Offset);
        }

        public double HostToLocal(double hostTime) => hostTime - OffsetSeconds;
    }
}
