using NUnit.Framework;
using YARG.Core;
using YARG.Core.Engine.Vocals.Engines;
using YARG.Core.Game;

namespace YARG.Multiplayer.Tests.Gameplay
{
    public class VocalBotTests
    {
        private static readonly float[] StarThresholds = { 0.06f, 0.12f, 0.2f, 0.45f, 0.75f, 1.09f };
        private static readonly float[] SoloThresholds = { 0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f };

        private static (int Hit, int Total) PlayBot(double updatesPerSecond)
        {
            var chart = TestCharts.Load();
            var track = chart.Vocals.Parts[0].CloneAsInstrumentDifficulty();
            var parms = EnginePreset.Default.Vocals.Create(StarThresholds, SoloThresholds, Difficulty.Expert, 20f, false);
            var engine = new YargVocalsEngine(track, chart.SyncTrack, parms, true);

            double end = track.GetEndTime();
            for (double t = 0; t <= end + 1; t += 1.0 / updatesPerSecond)
            {
                engine.Update(t);
            }
            return (engine.EngineStats.NotesHit, engine.EngineStats.TotalNotes);
        }

        [Test]
        public void Bot_HitsEveryPhraseWithGameLikeFrameRates()
        {
            var (hit, total) = PlayBot(60);

            Assert.That(total, Is.GreaterThan(0));
            Assert.That(hit, Is.EqualTo(total));
        }

        // Documents why the headless band scenarios show the vocal bot at 0/N phrases: the Core's bot only sings on
        // the updates the game gives it (YARG.Core VocalsEngine.GenerateQueuedUpdates computes the extra bot
        // updates with `index * ApproximateVocalFps` instead of `index * timeForFrame`, so none are queued).
        [Test]
        public void Bot_MissesPhrasesWhenTheGameRunsFarBelowRealFrameRates()
        {
            var (hit, total) = PlayBot(1);

            Assert.That(hit, Is.LessThan(total));
        }
    }
}
