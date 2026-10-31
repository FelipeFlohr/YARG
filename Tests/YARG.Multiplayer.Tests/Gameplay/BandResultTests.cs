using NUnit.Framework;
using YARG.Multiplayer.Gameplay;

namespace YARG.Multiplayer.Tests.Gameplay
{
    [TestFixture]
    public class BandResultTests
    {
        private static readonly int[] Thresholds = { 100, 200, 300, 400, 500, 600, 700 };

        [Test]
        public void Compute_SumsScoreAndBonus()
        {
            var (score, _) = BandResult.Compute(new[] { (100, 10), (200, 0) }, Thresholds);
            Assert.That(score, Is.EqualTo(310));
        }

        [Test]
        public void Compute_StarsCountThresholdsStrictlyBelow()
        {
            Assert.That(BandResult.Compute(new[] { (300, 0) }, Thresholds).Stars, Is.EqualTo(2));
            Assert.That(BandResult.Compute(new[] { (301, 0) }, Thresholds).Stars, Is.EqualTo(3));
        }

        [Test]
        public void Compute_Empty_IsZero()
        {
            Assert.That(BandResult.Compute(new (int, int)[0], Thresholds), Is.EqualTo((0, 0)));
        }

        [Test]
        public void Compute_AboveAllThresholds_IsThresholdCount()
        {
            Assert.That(BandResult.Compute(new[] { (5000, 0) }, Thresholds).Stars, Is.EqualTo(Thresholds.Length));
        }
    }
}
