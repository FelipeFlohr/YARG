using System;
using YARG.Core;

namespace YARG.Multiplayer.Gameplay
{
    /// <summary>
    /// Which parts and difficulties of a song can be played. Free of Unity and of song entries so it can be tested;
    /// callers pass in what the song has.
    /// </summary>
    public static class PartRules
    {
        private static bool IsVocal(Instrument instrument) =>
            instrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals;

        /// <summary>
        /// Whether the song has a part that <paramref name="instrument"/> can play, counting the
        /// 4-lane/5-lane drum conversions.
        /// </summary>
        public static bool HasPlayablePart(Func<Instrument, bool> hasInstrument, Instrument instrument)
        {
            return hasInstrument(instrument) || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => hasInstrument(Instrument.FiveLaneDrums),
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => hasInstrument(Instrument.ProDrums),
                _ => false
            };
        }

        /// <summary>
        /// Whether <paramref name="difficulty"/> can be played on <paramref name="instrument"/>.
        /// <paramref name="hasChart"/> tells whether the song charts that instrument at that difficulty.
        /// </summary>
        public static bool HasPlayableDifficulty(Func<Instrument, bool> hasInstrument,
            Func<Instrument, Difficulty, bool> hasChart, Instrument instrument, Difficulty difficulty)
        {
            // For vocals, the part values hold a sub-track mask rather than a difficulty mask,
            // so every difficulty but Expert+ is valid, provided the song has the part at all
            if (IsVocal(instrument))
            {
                return hasInstrument(instrument) && difficulty is not Difficulty.ExpertPlus;
            }

            // For PK, disallow beginner
            if (instrument is Instrument.ProKeys && difficulty is Difficulty.Beginner)
            {
                return false;
            }

            // Otherwise, we can do this
            return hasChart(instrument, difficulty) || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => hasChart(Instrument.FiveLaneDrums, difficulty),
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => hasChart(Instrument.ProDrums, difficulty),
                _ => false
            };
        }
    }
}
