using YARG.Core;
using YARG.Core.Song;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Which difficulties of a part can be played, shared by every place that lists them.
    /// </summary>
    public static class PartDifficulty
    {
        /// <summary>
        /// Whether <paramref name="difficulty"/> can be played on <paramref name="instrument"/> in the song.
        /// Callers still check that the song has the instrument at all.
        /// </summary>
        public static bool HasPlayableDifficulty(SongEntry entry, in Instrument instrument, in Difficulty difficulty)
        {
            // For vocals, the part values hold a sub-track mask rather than a difficulty mask,
            // so every difficulty but Expert+ is valid
            if (instrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals)
            {
                return difficulty is not Difficulty.ExpertPlus;
            }

            // For PK, disallow beginner
            if (instrument is Instrument.ProKeys && difficulty is Difficulty.Beginner)
            {
                return false;
            }

            // Otherwise, we can do this
            return entry[instrument][difficulty] || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry[Instrument.FiveLaneDrums][difficulty],
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry[Instrument.ProDrums][difficulty],
                _ => false
            };
        }
    }
}
