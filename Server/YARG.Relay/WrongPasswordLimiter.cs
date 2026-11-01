using System;
using System.Collections.Generic;

namespace YARG.Relay
{
    /// <summary>
    /// Counts wrong passwords per IP in a sliding window. Thread-safe.
    /// </summary>
    public sealed class WrongPasswordLimiter
    {
        private readonly Dictionary<string, Queue<DateTime>> _failures = new();
        private readonly int                                 _limit;
        private readonly TimeSpan                            _window;
        private readonly Func<DateTime>                      _clock;

        public WrongPasswordLimiter(int limit, TimeSpan window, Func<DateTime> clock)
        {
            _limit = limit;
            _window = window;
            _clock = clock;
        }

        /// <summary>
        /// Whether the IP used up its wrong tries; it can't try any password until some expire.
        /// </summary>
        public bool IsLimited(string ip)
        {
            lock (_failures)
            {
                return _failures.TryGetValue(ip, out var times) && Prune(times) >= _limit;
            }
        }

        public void RecordFailure(string ip)
        {
            lock (_failures)
            {
                if (!_failures.TryGetValue(ip, out var times))
                {
                    times = new Queue<DateTime>();
                    _failures[ip] = times;
                }
                times.Enqueue(_clock());
            }
        }

        /// <summary>
        /// Drops the failures that left the window, and every IP with none, so the table doesn't grow forever.
        /// </summary>
        public void Sweep()
        {
            lock (_failures)
            {
                var empty = new List<string>();
                foreach (var (ip, times) in _failures)
                {
                    if (Prune(times) == 0)
                    {
                        empty.Add(ip);
                    }
                }
                foreach (string ip in empty)
                {
                    _failures.Remove(ip);
                }
            }
        }

        private int Prune(Queue<DateTime> times)
        {
            var oldest = _clock() - _window;
            while (times.Count > 0 && times.Peek() <= oldest)
            {
                times.Dequeue();
            }
            return times.Count;
        }
    }
}
