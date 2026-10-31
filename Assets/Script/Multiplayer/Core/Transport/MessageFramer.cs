using System;
using System.Buffers.Binary;
using System.IO;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// Length-prefixed framing for a byte stream: each message is a little-endian
    /// <c>uint32</c> length followed by the payload.
    /// </summary>
    public sealed class MessageFramer
    {
        private const int HEADER_SIZE = sizeof(uint);

        private byte[] _buffer = new byte[4096];
        private int _start;
        private int _count;

        public static byte[] Frame(byte[] payload)
        {
            var framed = new byte[HEADER_SIZE + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint) payload.Length);
            payload.CopyTo(framed, HEADER_SIZE);
            return framed;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.AsSpan(_start + _count));
            _count += data.Length;

            // Validate as soon as the header is available, so a hostile peer
            // can't make us buffer an oversized message
            if (_count >= HEADER_SIZE)
            {
                PeekLength();
            }
        }

        public bool TryRead(out byte[] message)
        {
            message = Array.Empty<byte>();
            if (_count < HEADER_SIZE)
            {
                return false;
            }

            int length = PeekLength();
            if (_count < HEADER_SIZE + length)
            {
                return false;
            }

            message = _buffer.AsSpan(_start + HEADER_SIZE, length).ToArray();
            _start += HEADER_SIZE + length;
            _count -= HEADER_SIZE + length;
            if (_count == 0)
            {
                _start = 0;
            }
            return true;
        }

        private int PeekLength()
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_start, HEADER_SIZE));
            if (length > ProtocolInfo.MaxMessageSize)
            {
                throw new InvalidDataException($"Message length {length} exceeds the maximum of {ProtocolInfo.MaxMessageSize}");
            }
            return (int) length;
        }

        private void EnsureCapacity(int extra)
        {
            if (_start + _count + extra <= _buffer.Length)
            {
                return;
            }

            // Compact first, then grow if that's still not enough
            int needed = _count + extra;
            var target = needed <= _buffer.Length ? _buffer : new byte[Math.Max(needed, _buffer.Length * 2)];
            Buffer.BlockCopy(_buffer, _start, target, 0, _count);
            _buffer = target;
            _start = 0;
        }
    }
}
