using System;
using System.Collections.Generic;
using YARG.Core.Input;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.Sync
{
    /// <summary>
    /// Inputs received from a remote player, released to its local engine only up to a target
    /// time that never passes what the sender has confirmed (the watermark). The engine only applies
    /// inputs with a time up to the one it updates to, so feeding it past the watermark would
    /// judge inputs that haven't arrived yet as missing.
    /// </summary>
    public sealed class RemoteInputBuffer
    {
        // Sorted by time; ties keep arrival order. Items before _head were already dequeued.
        private readonly List<GameInput> _inputs = new();
        private int _head;

        private double _lastTarget = double.NegativeInfinity;
        private double _lastDequeueTime = double.NegativeInfinity;

        public double DelaySeconds { get; }
        public double Watermark    { get; private set; } = double.NegativeInfinity;

        /// <summary>
        /// Inputs that arrived with a time earlier than what was already released to the engine.
        /// They are still delivered (first), but the engine judges them late.
        /// </summary>
        public int LateInputs { get; private set; }

        public RemoteInputBuffer(double delaySeconds)
        {
            DelaySeconds = delaySeconds;
        }

        public void AddBatch(InputBatch batch)
        {
            foreach (var input in batch.Inputs)
            {
                if (input.Time < _lastDequeueTime)
                {
                    LateInputs++;
                }
                Insert(input);
            }

            if (batch.Watermark > Watermark)
            {
                Watermark = batch.Watermark;
            }
        }

        public double GetTargetTime(double localTime)
        {
            double target = Math.Min(localTime - DelaySeconds, Watermark);
            if (target > _lastTarget)
            {
                _lastTarget = target;
            }
            return _lastTarget;
        }

        public bool TryDequeue(double upToTime, out GameInput input)
        {
            if (upToTime > _lastDequeueTime)
            {
                _lastDequeueTime = upToTime;
            }

            if (_head < _inputs.Count && _inputs[_head].Time <= upToTime)
            {
                input = _inputs[_head++];
                Compact();
                return true;
            }

            input = default;
            return false;
        }

        private void Insert(GameInput input)
        {
            // Upper bound: after every queued input with a time <= this one
            int low = _head;
            int high = _inputs.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (_inputs[mid].Time <= input.Time)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            _inputs.Insert(low, input);
        }

        private void Compact()
        {
            if (_head == _inputs.Count)
            {
                _inputs.Clear();
                _head = 0;
            }
            else if (_head > 1024)
            {
                _inputs.RemoveRange(0, _head);
                _head = 0;
            }
        }
    }
}
