using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Input;

namespace YARG.Multiplayer.AutoTest
{
    /// <summary>
    /// Inputs of a five-fret player that hits every note except the ones inside a time window,
    /// used by automated tests that need a human who goes silent for a while.
    /// </summary>
    public static class ScriptedFiveFretInputs
    {
        private const double FretLead = 0.005;
        private const double StrumLength = 0.02;

        // Green..Orange are bits 0..4 of the note mask (open notes have none of them set).
        private const int FretBits = 5;

        private static readonly GuitarAction[] FretActions =
        {
            GuitarAction.Fret1, GuitarAction.Fret2, GuitarAction.Fret3, GuitarAction.Fret4, GuitarAction.Fret5,
        };

        /// <param name="notes">The parent notes of the track, in time order.</param>
        public static List<GameInput> Generate(IReadOnlyList<GuitarNote> notes, double missFrom, double missTo,
            double notBefore = double.NegativeInfinity)
        {
            var inputs = new List<GameInput>();
            int held = 0;
            double lastFretTime = double.NegativeInfinity;
            bool leftWindowEntry = false;

            foreach (var note in notes)
            {
                if (!leftWindowEntry && note.Time >= missFrom)
                {
                    leftWindowEntry = true;
                    if (held != 0)
                    {
                        // Entering the window: let go of everything that is still pressed.
                        double releaseTime = System.Math.Max(missFrom, lastFretTime);
                        SetFrets(inputs, releaseTime, held, 0);
                        held = 0;
                        lastFretTime = releaseTime;
                    }
                }

                if (note.Time >= missFrom && note.Time <= missTo)
                {
                    continue;
                }

                double fretTime = note.Time - FretLead;
                if (fretTime < notBefore)
                {
                    continue;
                }

                int mask = note.NoteMask & ((1 << FretBits) - 1);
                SetFrets(inputs, fretTime, held, mask);
                held = mask;
                lastFretTime = fretTime;

                inputs.Add(GameInput.Create(note.Time, GuitarAction.StrumDown, true));
                inputs.Add(GameInput.Create(note.Time + StrumLength, GuitarAction.StrumDown, false));
            }

            // List<T>.Sort is unstable, so order by time with the insertion index as tie-breaker.
            var indexed = new List<(GameInput Input, int Index)>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
            {
                indexed.Add((inputs[i], i));
            }
            indexed.Sort((a, b) =>
            {
                int byTime = a.Input.Time.CompareTo(b.Input.Time);
                return byTime != 0 ? byTime : a.Index.CompareTo(b.Index);
            });

            var sorted = new List<GameInput>(indexed.Count);
            foreach (var item in indexed)
            {
                sorted.Add(item.Input);
            }
            return sorted;
        }

        private static void SetFrets(List<GameInput> inputs, double time, int from, int to)
        {
            int changed = from ^ to;
            for (int bit = 0; bit < FretBits; bit++)
            {
                if ((changed & (1 << bit)) != 0)
                {
                    inputs.Add(GameInput.Create(time, FretActions[bit], (to & (1 << bit)) != 0));
                }
            }
        }
    }
}
