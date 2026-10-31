using System;
using System.IO;
using YARG.Core.IO;
using YARG.Core.Replays;

namespace YARG.Multiplayer.Protocol
{
    /// <summary>
    /// Serializes a <see cref="ReplayFrame"/> (profile, engine parameters, stats, inputs) with the
    /// same format replays use, so remote players can be reconstructed exactly.
    /// </summary>
    public static class ReplayFrameCodec
    {
        public static byte[] Encode(ReplayFrame frame)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            frame.Serialize(writer);
            writer.Flush();
            return stream.ToArray();
        }

        public static ReplayFrame Decode(byte[] data)
        {
            try
            {
                using var array = FixedArray.Read(new MemoryStream(data, false), data.Length);
                var stream = array.ToValueStream();
                return new ReplayFrame(ref stream, ReplayIO.REPLAY_VERSIONS.CURRENT);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                throw new InvalidDataException($"Malformed replay frame: {e.Message}", e);
            }
        }
    }
}
