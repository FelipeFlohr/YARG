using System;
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
        private readonly Dictionary<SongStem, Dictionary<int, (int Weight, int MutedWeight, bool Muted)>> _stems = new();

        /// <param name="weight">How much the participant counts while audible (at least 1).</param>
        /// <param name="mutedWeight">
        /// How much of the weight is taken away while the participant is muted, from 0 to
        /// <paramref name="weight"/>. Defaults to the whole weight. The background stem registers
        /// weight 2 / mutedWeight 1 so that it keeps a 50% floor even if every player misses.
        /// </param>
        public void AddParticipant(SongStem stem, int participantId, int weight = 1, int? mutedWeight = null)
        {
            int floor = mutedWeight ?? weight;
            if (weight < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be at least 1");
            }

            if (floor < 0 || floor > weight)
            {
                throw new ArgumentOutOfRangeException(nameof(mutedWeight), mutedWeight,
                    "Muted weight must be between 0 and the weight");
            }

            if (!_stems.TryGetValue(stem, out var participants))
            {
                participants = new Dictionary<int, (int Weight, int MutedWeight, bool Muted)>();
                _stems[stem] = participants;
            }

            // A new participant starts audible; re-adding replaces the weight and keeps the mute state
            bool muted = participants.TryGetValue(participantId, out var existing) && existing.Muted;
            participants[participantId] = (weight, floor, muted);
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

            participants[participantId] = (entry.Weight, entry.MutedWeight, muted);
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
            bool anyUnmuted = false;
            foreach (var entry in participants.Values)
            {
                total += entry.Weight;
                audible += entry.Muted ? entry.Weight - entry.MutedWeight : entry.Weight;
                anyUnmuted |= !entry.Muted;
            }

            switch (rule)
            {
                // Silent only when every participant is muted; the partial muted weight does not apply here
                case StemMuteRule.AllMustMiss:
                    return anyUnmuted ? 1.0 : 0.0;
                default:
                    return total == 0 ? 1.0 : (double) audible / total;
            }
        }
    }
}
