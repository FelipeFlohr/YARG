using System.IO;
using Melanchall.DryWetMidi.Core;
using NUnit.Framework;
using YARG.Core.Chart;

namespace YARG.Multiplayer.Tests.Gameplay
{
    public static class TestCharts
    {
        /// <summary>
        /// Loads a fresh chart each call: engines mutate note state, so two engines can't share one.
        /// </summary>
        public static SongChart Load()
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestCharts", "drawntotheflame.mid");
            var midi = MidiFile.Read(path);
            return SongChart.FromMidi(in ParseSettings.Default_Midi, midi);
        }
    }
}
