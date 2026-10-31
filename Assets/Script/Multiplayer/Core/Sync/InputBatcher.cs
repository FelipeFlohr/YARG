using System.Collections.Generic;
using YARG.Core.Input;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.Sync
{
    /// <summary>
    /// Collects the local player's inputs and emits an <see cref="InputBatch"/> at a fixed interval,
    /// even when empty, so receivers keep learning how far the sender has played.
    /// </summary>
    public sealed class InputBatcher
    {
        public const double IntervalSeconds = 0.05;

        private readonly int             _playerId;
        private readonly List<GameInput> _pending = new();

        private double  _watermark = double.NegativeInfinity;
        private double? _lastFlush;

        public InputBatcher(int playerId)
        {
            _playerId = playerId;
        }

        public void Add(GameInput input)
        {
            _pending.Add(input);
        }

        /// <summary>
        /// Declares that no input added from now on will have a time earlier than <paramref name="engineTime"/>.
        /// </summary>
        public void SetWatermark(double engineTime)
        {
            if (engineTime > _watermark)
            {
                _watermark = engineTime;
            }
        }

        public InputBatch? TryFlush(double now)
        {
            if (_lastFlush.HasValue && now - _lastFlush.Value < IntervalSeconds)
            {
                return null;
            }

            _lastFlush = now;
            var batch = new InputBatch(_playerId, _watermark, _pending.ToArray());
            _pending.Clear();
            return batch;
        }
    }
}
