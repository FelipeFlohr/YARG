using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Multiplayer.Audio
{
    public enum StemMuteRule
    {
        /// <summary>The audible fraction is the weighted share of participants that are not muted.</summary>
        Proportional,

        /// <summary>The stem is silent only when every participant is muted.</summary>
        AllMustMiss,
    }

    /// <summary>
    /// Tracks which participants (players) have muted a stem after a miss, and derives how audible
    /// the stem should be. Pure logic: applying the fraction to the audio mixer is up to the caller.
    /// </summary>
    public sealed class StemMuteTracker
    {
        private readonly Dictionary<SongStem, Dictionary<int, (int Weight, bool Muted)>> _stems = new();

        public void AddParticipant(SongStem stem, int participantId, int weight = 1)
        {
            if (!_stems.TryGetValue(stem, out var participants))
            {
                participants = new Dictionary<int, (int Weight, bool Muted)>();
                _stems[stem] = participants;
            }

            // A new participant starts audible; re-adding replaces the weight and keeps the mute state
            bool muted = participants.TryGetValue(participantId, out var existing) && existing.Muted;
            participants[participantId] = (weight, muted);
        }

        public void RemoveParticipant(int participantId)
        {
            var emptied = new List<SongStem>();
            foreach (var pair in _stems)
            {
                pair.Value.Remove(participantId);
                if (pair.Value.Count == 0)
                {
                    emptied.Add(pair.Key);
                }
            }

            foreach (var stem in emptied)
            {
                _stems.Remove(stem);
            }
        }

        /// <returns>True if the muted state changed.</returns>
        public bool SetMuted(SongStem stem, int participantId, bool muted)
        {
            if (!_stems.TryGetValue(stem, out var participants) ||
                !participants.TryGetValue(participantId, out var entry) ||
                entry.Muted == muted)
            {
                return false;
            }

            participants[participantId] = (entry.Weight, muted);
            return true;
        }

        public bool HasParticipants(SongStem stem)
        {
            return _stems.TryGetValue(stem, out var participants) && participants.Count > 0;
        }

        public IReadOnlyList<SongStem> StemsOf(int participantId)
        {
            var result = new List<SongStem>();
            foreach (var pair in _stems)
            {
                if (pair.Value.ContainsKey(participantId))
                {
                    result.Add(pair.Key);
                }
            }

            return result;
        }

        /// <returns>1 when fully audible, 0 when fully muted. Stems without participants are audible.</returns>
        public double GetAudibleFraction(SongStem stem, StemMuteRule rule)
        {
            if (!_stems.TryGetValue(stem, out var participants) || participants.Count == 0)
            {
                return 1.0;
            }

            int total = 0;
            int audible = 0;
            foreach (var entry in participants.Values)
            {
                total += entry.Weight;
                if (!entry.Muted)
                {
                    audible += entry.Weight;
                }
            }

            switch (rule)
            {
                case StemMuteRule.AllMustMiss:
                    return audible > 0 || total == 0 ? 1.0 : 0.0;
                default:
                    return total == 0 ? 1.0 : (double) audible / total;
            }
        }
    }
}
