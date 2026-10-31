using YARG.Core;
using YARG.Core.Song;
using YARG.Multiplayer.Gameplay;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Which parts and difficulties of a song can be played, shared by every place that lists them.
    /// The rules live in <see cref="PartRules"/> so they can be tested outside Unity.
    /// </summary>
    public static class PartDifficulty
    {
        /// <summary>
        /// Whether the song has a part that <paramref name="instrument"/> can play,
        /// counting the 4-lane/5-lane drum conversions.
        /// </summary>
        public static bool HasPlayablePart(SongEntry entry, in Instrument instrument)
        {
            return PartRules.HasPlayablePart(entry.HasInstrument, instrument);
        }

        /// <summary>
        /// Whether <paramref name="difficulty"/> can be played on <paramref name="instrument"/> in the song.
        /// </summary>
        public static bool HasPlayableDifficulty(SongEntry entry, in Instrument instrument, in Difficulty difficulty)
        {
            return PartRules.HasPlayableDifficulty(entry.HasInstrument, (i, d) => entry[i][d], instrument, difficulty);
        }
    }
}
