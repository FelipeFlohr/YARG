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

        [Test]
        public void MutedWeight_KeepsAFloorWhenMuted()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Song, 1, weight: 2, mutedWeight: 1);
            t.SetMuted(SongStem.Song, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Song, StemMuteRule.Proportional), Is.EqualTo(0.5));
        }

        [Test]
        public void MutedWeight_AllMustMiss_StillSilentWhenEveryoneMuted()
        {
            var t = new StemMuteTracker();
            t.AddParticipant(SongStem.Song, 1, weight: 2, mutedWeight: 1);
            Assert.That(t.GetAudibleFraction(SongStem.Song, StemMuteRule.AllMustMiss), Is.EqualTo(1.0));
            t.SetMuted(SongStem.Song, 1, true);
            Assert.That(t.GetAudibleFraction(SongStem.Song, StemMuteRule.AllMustMiss), Is.EqualTo(0.0));
        }

        [TestCase(0, 0)]
        [TestCase(-1, 0)]
        [TestCase(2, -1)]
        [TestCase(2, 3)]
        public void AddParticipant_InvalidWeights_Throw(int weight, int mutedWeight)
        {
            var t = new StemMuteTracker();
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => t.AddParticipant(SongStem.Song, 1, weight, mutedWeight));
        }

        // The single-player StemState/SetMute logic as it was before the tracker existed
        private sealed class LegacyStemState
        {
            public int Total;
            public int Audible;

            public double SetMute(bool muted, bool keepAudibleUntilAllMiss)
            {
                if (muted)
                {
                    --Audible;
                }
                else if (Audible < Total)
                {
                    ++Audible;
                }

                if (keepAudibleUntilAllMiss)
                {
                    return Audible > 0 ? 1 : 0;
                }

                return (double) Audible / Total;
            }
        }

        [TestCase(1, false)]
        [TestCase(2, false)]
        [TestCase(3, true)]
        [TestCase(4, true)]
        public void ParityWithLegacySingleplayerLogic(int seed, bool allMustMiss)
        {
            var rng = new System.Random(seed);
            for (int round = 0; round < 50; round++)
            {
                int players = rng.Next(1, 5);
                var legacy = new LegacyStemState();
                var tracker = new StemMuteTracker();
                var muted = new bool[players];
                for (int i = 0; i < players; i++)
                {
                    // Players without a stem of their own count twice but only mute once
                    bool background = rng.Next(2) == 0;
                    legacy.Total += background ? 2 : 1;
                    if (background)
                    {
                        tracker.AddParticipant(SongStem.Song, i, weight: 2, mutedWeight: 1);
                    }
                    else
                    {
                        tracker.AddParticipant(SongStem.Song, i);
                    }
                }

                legacy.Audible = legacy.Total;

                for (int step = 0; step < 200; step++)
                {
                    int who = rng.Next(players);
                    bool mute = !muted[who];
                    muted[who] = mute;

                    double expected = legacy.SetMute(mute, allMustMiss);
                    tracker.SetMuted(SongStem.Song, who, mute);
                    var rule = allMustMiss ? StemMuteRule.AllMustMiss : StemMuteRule.Proportional;
                    double actual = tracker.GetAudibleFraction(SongStem.Song, rule);

                    if (allMustMiss)
                    {
                        // The legacy all-miss path only ever applied to stems with weight-1 players
                        // here; a background player still holds weight, so compare on "anyone unmuted"
                        bool anyUnmuted = false;
                        foreach (bool m in muted) { anyUnmuted |= !m; }
                        Assert.That(actual, Is.EqualTo(anyUnmuted ? 1.0 : 0.0));
                    }
                    else
                    {
                        Assert.That(actual, Is.EqualTo(expected).Within(1e-9), $"seed {seed} round {round} step {step}");
                    }
                }
            }
        }
    }
}
