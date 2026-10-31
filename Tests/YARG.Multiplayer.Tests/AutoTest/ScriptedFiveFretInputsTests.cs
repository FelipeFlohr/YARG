using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Multiplayer.AutoTest;
using YARG.Multiplayer.Tests.Gameplay;

namespace YARG.Multiplayer.Tests.AutoTest
{
    public class ScriptedFiveFretInputsTests
    {
        private const double Step = 1.0 / 60.0;

        private static readonly float[] StarThresholds = { 0.06f, 0.12f, 0.2f, 0.45f, 0.75f, 1.09f };
        private static readonly float[] SoloThresholds = { 0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f };

        private sealed class Run
        {
            public List<GuitarNote> Notes = null!;
            public List<GameInput>  Inputs = null!;
            public List<double>     HitTimes = new();
            public List<double>     DroppedSustains = new();
        }

        private static Run Play(double missFrom, double missTo, double notBefore = double.NegativeInfinity)
        {
            var chart = TestCharts.Load();
            var track = chart.FiveFretGuitar.GetDifficulty(Difficulty.Expert);
            var parms = EnginePreset.Default.FiveFretGuitar.Create(StarThresholds, SoloThresholds, false);
            var engine = new YargFiveFretGuitarEngine(track, chart.SyncTrack, parms, false);

            var run = new Run
            {
                Notes = track.Notes,
                Inputs = ScriptedFiveFretInputs.Generate(track.Notes, missFrom, missTo, notBefore),
            };
            engine.OnNoteHit += (_, note) => run.HitTimes.Add(note.Time);
            engine.OnSustainEnd += (_, time, finished) =>
            {
                if (!finished)
                {
                    run.DroppedSustains.Add(time);
                }
            };

            double end = track.GetEndTime();
            int next = 0;
            for (double t = 0; t <= end + 1; t += Step)
            {
                while (next < run.Inputs.Count && run.Inputs[next].Time <= t)
                {
                    var input = run.Inputs[next++];
                    engine.QueueInput(ref input);
                }
                engine.Update(t);
            }
            return run;
        }

        [Test]
        public void Generate_HitsAlmostEverythingOutsideTheWindow()
        {
            var run = Play(30, 40);

            int outside = run.Notes.Count(n => n.Time < 30 || n.Time > 40);
            int hitsOutside = run.HitTimes.Count(t => t < 30 || t > 40);

            Assert.That(outside, Is.GreaterThan(0));
            Assert.That((double) hitsOutside / outside, Is.GreaterThanOrEqualTo(0.99));
        }

        [Test]
        public void Generate_KeepsHoldingSustainsBeforeTheWindow()
        {
            var run = Play(30, 40);

            Assert.That(run.DroppedSustains.Where(t => t < 30), Is.Empty);
        }

        [Test]
        public void Generate_KeepsTheSustainedFretPressedWhileAHigherNoteIsPlayed()
        {
            var notes = new List<GuitarNote>
            {
                new(FiveFretGuitarFret.Green, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 1.0, 0.7, 100, 70),
                new(FiveFretGuitarFret.Red, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 1.3, 0, 130, 0),
                new(FiveFretGuitarFret.Yellow, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 2.0, 0, 200, 0),
            };

            var inputs = ScriptedFiveFretInputs.Generate(notes, 50, 60);

            var greenReleases = inputs.Where(i => i.Action == (int) GuitarAction.Fret1 && !i.Button).ToList();
            Assert.That(greenReleases, Has.Count.EqualTo(1));
            Assert.That(greenReleases[0].Time, Is.GreaterThanOrEqualTo(1.7));
        }

        [Test]
        public void Generate_OnlyKeepsFretsOfNotesThatAreStillSustaining()
        {
            var notes = new List<GuitarNote>
            {
                new(FiveFretGuitarFret.Green, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 1.0, 0.7, 100, 70),
                new(FiveFretGuitarFret.Red, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 1.3, 0, 130, 0),
                new(FiveFretGuitarFret.Yellow, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 1.5, 0, 150, 0),
                new(FiveFretGuitarFret.Orange, GuitarNoteType.Strum, GuitarNoteFlags.None, NoteFlags.None, 3.0, 0, 300, 0),
            };

            var inputs = ScriptedFiveFretInputs.Generate(notes, 50, 60);

            var redReleases = inputs.Where(i => i.Action == (int) GuitarAction.Fret2 && !i.Button).ToList();
            Assert.That(redReleases, Has.Count.EqualTo(1));
            Assert.That(redReleases[0].Time, Is.LessThanOrEqualTo(1.5), "red is not a sustain, so it is let go for the next note");
            var greenReleases = inputs.Where(i => i.Action == (int) GuitarAction.Fret1 && !i.Button).ToList();
            Assert.That(greenReleases[0].Time, Is.GreaterThanOrEqualTo(1.7), "green is still sustaining");
        }

        [Test]
        public void Generate_HitsNothingInsideTheWindow()
        {
            var run = Play(30, 40);

            Assert.That(run.Notes.Count(n => n.Time >= 30 && n.Time <= 40), Is.GreaterThan(0));
            Assert.That(run.HitTimes.Count(t => t < 30 || t > 40), Is.GreaterThan(0), "hits outside the window");
            Assert.That(run.HitTimes.Where(t => t >= 30 && t <= 40), Is.Empty);
        }

        [Test]
        public void Generate_FirstNoteAfterTheWindowIsHit()
        {
            var run = Play(30, 40);

            var first = run.Notes.First(n => n.Time > 40);
            Assert.That(run.HitTimes, Does.Contain(first.Time));
        }

        [Test]
        public void Generate_DropsInputsBeforeNotBefore()
        {
            var chart = TestCharts.Load();
            var notes = chart.FiveFretGuitar.GetDifficulty(Difficulty.Expert).Notes;

            var inputs = ScriptedFiveFretInputs.Generate(notes, 30, 40, 20);

            Assert.That(inputs, Is.Not.Empty);
            Assert.That(inputs.Min(i => i.Time), Is.GreaterThanOrEqualTo(20));
        }

        [Test]
        public void Generate_NotBefore_StillHitsAfterItAndOutsideTheWindow()
        {
            var run = Play(30, 40, 20);

            Assert.That(run.HitTimes.Count(t => t > 20 && t < 30), Is.GreaterThan(0), "hits between notBefore and the window");
            Assert.That(run.HitTimes.Count(t => t > 40), Is.GreaterThan(0), "hits after the window");
            Assert.That(run.HitTimes.Where(t => t < 20), Is.Empty);
        }

        [Test]
        public void Generate_NotBeforeInsideTheWindow_StillHitsFirstNoteAfterIt()
        {
            var run = Play(30, 40, 35);

            var first = run.Notes.First(n => n.Time > 40);
            Assert.That(run.HitTimes, Does.Contain(first.Time));
        }

        [Test]
        public void Generate_IsSortedByTime()
        {
            var chart = TestCharts.Load();
            var notes = chart.FiveFretGuitar.GetDifficulty(Difficulty.Expert).Notes;

            var inputs = ScriptedFiveFretInputs.Generate(notes, 30, 40);

            for (int i = 1; i < inputs.Count; i++)
            {
                Assert.That(inputs[i].Time, Is.GreaterThanOrEqualTo(inputs[i - 1].Time), $"index {i}");
            }
        }
    }
}
