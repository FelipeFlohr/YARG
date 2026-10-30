using NUnit.Framework;
using YARG.Multiplayer.Sync;

namespace YARG.Multiplayer.Tests.Sync
{
    public class ClockSyncTests
    {
        // Simulates a ping whose host reply is stamped exactly halfway (symmetric) or at a given point
        private static void AddSample(ClockSync sync, double sentLocal, double rtt, double offset, double replyFraction = 0.5)
        {
            double hostReply = sentLocal + rtt * replyFraction + offset;
            sync.AddSample(sentLocal, hostReply, sentLocal + rtt);
        }

        [Test]
        public void NoSamples_HasNoEstimate()
        {
            Assert.That(new ClockSync().HasEstimate, Is.False);
        }

        [Test]
        public void SymmetricLatency_RecoversExactOffset()
        {
            // Host clock is 1000 s ahead: a wrong system clock must not matter (Review Focus 2)
            var sync = new ClockSync();
            AddSample(sync, 10.0, 0.04, 1000.0);
            Assert.That(sync.HasEstimate, Is.True);
            Assert.That(sync.OffsetSeconds, Is.EqualTo(1000.0).Within(1e-9));
        }

        [Test]
        public void HighRttOutliers_AreIgnored()
        {
            var sync = new ClockSync();
            for (int i = 0; i < 15; i++)
            {
                AddSample(sync, i, 0.02, 5.0);
            }
            for (int i = 0; i < 5; i++)
            {
                // Asymmetric slow path, which skews the naive offset to 5.2
                AddSample(sync, 20 + i, 0.5, 5.2);
            }
            Assert.That(sync.OffsetSeconds, Is.EqualTo(5.0).Within(0.001));
        }

        [Test]
        public void OnlyLast20SamplesCount()
        {
            var sync = new ClockSync();
            for (int i = 0; i < 20; i++)
            {
                AddSample(sync, i, 0.01, 1.0);
            }
            for (int i = 0; i < 20; i++)
            {
                AddSample(sync, 20 + i, 0.03, 2.0);
            }
            Assert.That(sync.OffsetSeconds, Is.EqualTo(2.0).Within(1e-9));
        }

        [Test]
        public void HostToLocal_SubtractsOffset()
        {
            var sync = new ClockSync();
            AddSample(sync, 0, 0.02, 3.0);
            Assert.That(sync.HostToLocal(10.0), Is.EqualTo(7.0).Within(1e-9));
        }
    }
}
