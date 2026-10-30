using System;
using System.IO;
using NUnit.Framework;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Tests.Transport
{
    public class MessageFramerTests
    {
        [Test]
        public void Frame_PrefixesLittleEndianLength()
        {
            Assert.That(MessageFramer.Frame(new byte[] { 1, 2, 3 }), Is.EqualTo(new byte[] { 3, 0, 0, 0, 1, 2, 3 }));
        }

        [Test]
        public void TryRead_ReassemblesMessageSplitIntoSingleBytes()
        {
            var framed = MessageFramer.Frame(new byte[] { 9, 8, 7 });
            var framer = new MessageFramer();
            for (int i = 0; i < framed.Length - 1; i++)
            {
                framer.Append(framed.AsSpan(i, 1));
                Assert.That(framer.TryRead(out _), Is.False);
            }
            framer.Append(framed.AsSpan(framed.Length - 1, 1));
            Assert.That(framer.TryRead(out var msg), Is.True);
            Assert.That(msg, Is.EqualTo(new byte[] { 9, 8, 7 }));
        }

        [Test]
        public void TryRead_SplitsTwoConcatenatedMessages()
        {
            var a = MessageFramer.Frame(new byte[] { 1, 2 });
            var b = MessageFramer.Frame(new byte[] { 3, 4, 5 });
            var both = new byte[a.Length + b.Length];
            a.CopyTo(both, 0);
            b.CopyTo(both, a.Length);

            var framer = new MessageFramer();
            framer.Append(both);

            Assert.That(framer.TryRead(out var first), Is.True);
            Assert.That(first, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(framer.TryRead(out var second), Is.True);
            Assert.That(second, Is.EqualTo(new byte[] { 3, 4, 5 }));
            Assert.That(framer.TryRead(out _), Is.False);
        }

        [Test]
        public void TryRead_AllowsEmptyMessage()
        {
            var framer = new MessageFramer();
            framer.Append(MessageFramer.Frame(Array.Empty<byte>()));
            Assert.That(framer.TryRead(out var msg), Is.True);
            Assert.That(msg, Is.Empty);
        }

        [Test]
        public void Append_ThrowsWhenDeclaredLengthExceedsMax()
        {
            var header = BitConverter.GetBytes(ProtocolInfo.MaxMessageSize + 1);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }
            var framer = new MessageFramer();
            Assert.Throws<InvalidDataException>(() => framer.Append(header));
        }
    }
}
