using NUnit.Framework;
using YARG.Core.Audio;
using YARG.Multiplayer.Audio;

namespace YARG.Multiplayer.Tests.Audio
{
    [TestFixture]
    public class StemMuteTrackerTests
    {
        private static StemMuteTracker Two()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Drums1, 1);
            t.AddParticipant(SongStem.Drums1, 2);
            return t;
        }

        [Test]
        public void Proportional_OneOfTwoMuted_IsHalf()
        {
            var t = Two();
            t.SetMuted(SongStem.Drums1, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(0.5));
        }

        [Test]
        public void AllMustMiss_OneOfTwoMuted_IsOne()
        {
            var t = Two();
            t.SetMuted(SongStem.Drums1, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(1.0));
        }

        [Test]
        public void AllMustMiss_BothMuted_IsZero()
        {
            var t = Two();
            t.SetMuted(SongStem.Drums1, 1, true);
            t.SetMuted(SongStem.Drums1, 2, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(0.0));
        }

        [Test]
        public void AllMustMiss_ThreeParticipants_OnlyZeroWhenAllMuted()
        {
            var t = Two();
            t.AddParticipant(SongStem.Drums1, 3);
            t.SetMuted(SongStem.Drums1, 1, true);
            t.SetMuted(SongStem.Drums1, 2, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(1.0));
            t.SetMuted(SongStem.Drums1, 3, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(0.0));
        }

        [Test]
        public void MissThenHit_Unmutes()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Drums1, 1);
            t.SetMuted(SongStem.Drums1, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(0.0));
            t.SetMuted(SongStem.Drums1, 1, false);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(1.0));
        }

        [Test]
        public void SetMuted_SameStateTwice_ReturnsFalseAndKeepsFraction()
        {
            var t = Two();
            Assert.That(t.SetMuted(SongStem.Drums1, 1, true), Is.True);
            Assert.That(t.SetMuted(SongStem.Drums1, 1, true), Is.False);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(0.5));
        }

        [Test]
        public void RemoveParticipant_WhileMuted_LeavesOthersInCharge()
        {
            var t = Two();
            t.SetMuted(SongStem.Drums1, 1, true);
            t.RemoveParticipant(2);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(0.0));

            var u = Two();
            u.SetMuted(SongStem.Drums1, 1, true);
            u.RemoveParticipant(1);
            Assert.That(u.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(1.0));
        }

        [Test]
        public void RemoveParticipant_RemovesFromEveryStem()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Drums1, 1);
            t.AddParticipant(SongStem.Drums2, 1);
            Assert.That(t.StemsOf(1), Has.Count.EqualTo(2));
            t.RemoveParticipant(1);
            Assert.That(t.StemsOf(1), Is.Empty);
            Assert.That(t.HasParticipants(SongStem.Drums1), Is.False);
            Assert.That(t.HasParticipants(SongStem.Drums2), Is.False);
        }

        [Test]
        public void GetAudibleFraction_NoParticipants_IsOne()
        {
            var t = new StemMuteTracker();
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(1.0));
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.AllMustMiss), Is.EqualTo(1.0));
        }

        [Test]
        public void UnknownParticipant_IsIgnored()
        {
            var t = Two();
            Assert.That(t.SetMuted(SongStem.Drums1, 99, true), Is.False);
            Assert.That(t.SetMuted(SongStem.Guitar, 1, true), Is.False);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(1.0));
        }

        [Test]
        public void Weight_CountsInProportional()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Drums1, 1, 2);
            t.AddParticipant(SongStem.Drums1, 2, 1);
            t.SetMuted(SongStem.Drums1, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(1.0 / 3).Within(1e-9));
        }

        [Test]
        public void AddParticipant_Twice_ReplacesWeightWithoutDuplicating()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Drums1, 1, 1);
            t.AddParticipant(SongStem.Drums1, 1, 3);
            t.AddParticipant(SongStem.Drums1, 2, 1);
            t.SetMuted(SongStem.Drums1, 2, true);
            Assert.That(t.StemsOf(1), Has.Count.EqualTo(1));
            Assert.That(t.GetAudibleFraction(SongStem.Drums1, StemMuteRule.Proportional), Is.EqualTo(0.75).Within(1e-9));
        }
    }
}
