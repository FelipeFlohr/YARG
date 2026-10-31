using System.Collections.Generic;
using NUnit.Framework;
using YARG.Core;
using YARG.Multiplayer.Gameplay;

namespace YARG.Multiplayer.Tests.Gameplay
{
    public class PartRulesTests
    {
        // A fake song: which parts exist and which difficulties each one charts
        private sealed class FakeSong
        {
            private readonly Dictionary<Instrument, HashSet<Difficulty>> _parts = new();

            public FakeSong With(Instrument instrument, params Difficulty[] difficulties)
            {
                _parts[instrument] = new HashSet<Difficulty>(difficulties);
                return this;
            }

            public bool HasInstrument(Instrument instrument) => _parts.ContainsKey(instrument);

            public bool HasChart(Instrument instrument, Difficulty difficulty) =>
                _parts.TryGetValue(instrument, out var set) && set.Contains(difficulty);

            public bool PlayablePart(Instrument instrument) =>
                PartRules.HasPlayablePart(HasInstrument, instrument);

            public bool PlayableDifficulty(Instrument instrument, Difficulty difficulty) =>
                PartRules.HasPlayableDifficulty(HasInstrument, HasChart, instrument, difficulty);
        }

        private static readonly Difficulty[] AllButPlus =
        {
            Difficulty.Beginner, Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert,
        };

        [Test]
        public void Vocals_WithoutThePart_AreNotPlayable()
        {
            var song = new FakeSong().With(Instrument.FiveFretGuitar, AllButPlus);

            Assert.That(song.PlayablePart(Instrument.Vocals), Is.False);
            Assert.That(song.PlayableDifficulty(Instrument.Vocals, Difficulty.Expert), Is.False);
        }

        [Test]
        public void Harmony_WithoutHarmonies_IsNotPlayable_EvenWithSoloVocals()
        {
            var song = new FakeSong().With(Instrument.Vocals, Difficulty.Expert);

            Assert.That(song.PlayablePart(Instrument.Vocals), Is.True);
            Assert.That(song.PlayablePart(Instrument.Harmony), Is.False);
            Assert.That(song.PlayableDifficulty(Instrument.Harmony, Difficulty.Medium), Is.False);
            Assert.That(song.PlayableDifficulty(Instrument.PartyVocals, Difficulty.Medium), Is.False);
        }

        [Test]
        public void Vocals_WithThePart_AllowEveryDifficultyButExpertPlus()
        {
            var song = new FakeSong().With(Instrument.Harmony);

            foreach (var difficulty in AllButPlus)
            {
                Assert.That(song.PlayableDifficulty(Instrument.Harmony, difficulty), Is.True, difficulty.ToString());
            }
            Assert.That(song.PlayableDifficulty(Instrument.Harmony, Difficulty.ExpertPlus), Is.False);
        }

        [Test]
        public void ProKeys_DisallowBeginner()
        {
            var song = new FakeSong().With(Instrument.ProKeys, AllButPlus);

            Assert.That(song.PlayableDifficulty(Instrument.ProKeys, Difficulty.Beginner), Is.False);
            Assert.That(song.PlayableDifficulty(Instrument.ProKeys, Difficulty.Easy), Is.True);
        }

        [Test]
        public void Guitar_FollowsTheChartedDifficulties()
        {
            var song = new FakeSong().With(Instrument.FiveFretGuitar, Difficulty.Hard, Difficulty.Expert);

            Assert.That(song.PlayableDifficulty(Instrument.FiveFretGuitar, Difficulty.Hard), Is.True);
            Assert.That(song.PlayableDifficulty(Instrument.FiveFretGuitar, Difficulty.Easy), Is.False);
            Assert.That(song.PlayablePart(Instrument.FiveFretGuitar), Is.True);
            Assert.That(song.PlayablePart(Instrument.FiveFretBass), Is.False);
        }

        [Test]
        public void FiveLaneDrums_ConvertToFourLaneAndPro()
        {
            var song = new FakeSong().With(Instrument.FiveLaneDrums, Difficulty.Expert);

            foreach (var drums in new[] { Instrument.FourLaneDrums, Instrument.ProDrums })
            {
                Assert.That(song.PlayablePart(drums), Is.True, drums.ToString());
                Assert.That(song.PlayableDifficulty(drums, Difficulty.Expert), Is.True, drums.ToString());
                Assert.That(song.PlayableDifficulty(drums, Difficulty.Easy), Is.False, drums.ToString());
            }
        }

        [Test]
        public void ProDrums_ConvertToFiveLane()
        {
            var song = new FakeSong().With(Instrument.ProDrums, Difficulty.Hard);

            Assert.That(song.PlayablePart(Instrument.FiveLaneDrums), Is.True);
            Assert.That(song.PlayableDifficulty(Instrument.FiveLaneDrums, Difficulty.Hard), Is.True);
            Assert.That(song.PlayableDifficulty(Instrument.FiveLaneDrums, Difficulty.Expert), Is.False);
        }

        [Test]
        public void Drums_WithNoDrumPart_AreNotPlayable()
        {
            var song = new FakeSong().With(Instrument.FiveFretGuitar, Difficulty.Expert);

            Assert.That(song.PlayablePart(Instrument.ProDrums), Is.False);
            Assert.That(song.PlayablePart(Instrument.FiveLaneDrums), Is.False);
        }
    }
}
