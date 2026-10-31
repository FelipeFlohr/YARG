using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Chart;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Drums.Engines;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Engine.Keys.Engines;
using YARG.Core.Engine.Vocals.Engines;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Sync;

namespace YARG.Multiplayer.Tests.Gameplay
{
    /// <summary>
    /// An engine fed through the network path (batcher, codec, remote buffer) must end with the same
    /// stats as the engine that received the inputs directly.
    /// </summary>
    public class RemoteFidelityTests
    {
        private const double Step = 1.0 / 60.0;

        private static readonly float[] StarThresholds = { 0.06f, 0.12f, 0.2f, 0.45f, 0.75f, 1.09f };
        private static readonly float[] SoloThresholds = { 0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f };

        private sealed class Subject
        {
            public BaseEngine Engine = null!;
            public double     End;
            // Input generator for a human player, given the chart end time.
            public Func<Random, double, List<GameInput>> Human = null!;
        }

        public static IEnumerable<TestCaseData> Instruments()
        {
            foreach (var name in new[] { "FiveFretGuitar", "FiveFretBass", "ProDrums", "Keys", "Vocals" })
            {
                yield return new TestCaseData(name).SetArgDisplayNames(name);
            }
        }

        private static Subject? Create(string instrument, bool isBot)
        {
            var chart = TestCharts.Load();
            switch (instrument)
            {
                case "FiveFretGuitar":
                case "FiveFretBass":
                {
                    bool bass = instrument == "FiveFretBass";
                    var notes = (bass ? chart.FiveFretBass : chart.FiveFretGuitar).GetDifficulty(Difficulty.Expert);
                    if (notes.Notes.Count == 0) return null;
                    var parms = EnginePreset.Default.FiveFretGuitar.Create(StarThresholds, SoloThresholds, bass);
                    return new Subject
                    {
                        Engine = new YargFiveFretGuitarEngine(notes, chart.SyncTrack, parms, isBot),
                        End = notes.GetEndTime(),
                        Human = RandomGuitarInputs,
                    };
                }
                case "ProDrums":
                {
                    var notes = chart.ProDrums.GetDifficulty(Difficulty.Expert);
                    if (notes.Notes.Count == 0) return null;
                    var parms = EnginePreset.Default.Drums.Create(StarThresholds, SoloThresholds,
                        DrumsEngineParameters.DrumMode.ProFourLane);
                    return new Subject
                    {
                        Engine = new YargDrumsEngine(notes, chart.SyncTrack, parms, isBot, false),
                        End = notes.GetEndTime(),
                        Human = RandomDrumInputs,
                    };
                }
                case "Keys":
                {
                    var notes = chart.Keys.GetDifficulty(Difficulty.Expert);
                    if (notes.Notes.Count == 0) return null;
                    var parms = EnginePreset.Default.ProKeys.Create(StarThresholds, SoloThresholds, false);
                    return new Subject
                    {
                        Engine = new YargFiveLaneKeysEngine(notes, chart.SyncTrack, parms, isBot),
                        End = notes.GetEndTime(),
                        Human = RandomKeysInputs,
                    };
                }
                case "Vocals":
                {
                    if (chart.Vocals.Parts.Count == 0) return null;
                    var notes = chart.Vocals.Parts[0].CloneAsInstrumentDifficulty();
                    if (notes.Notes.Count == 0) return null;
                    var parms = EnginePreset.Default.Vocals.Create(StarThresholds, SoloThresholds,
                        Difficulty.Expert, 60f, false);
                    return new Subject
                    {
                        Engine = new YargVocalsEngine(notes, chart.SyncTrack, parms, isBot),
                        End = notes.GetEndTime(),
                        Human = (random, end) => ScriptedVocalInputs(random, end, notes),
                    };
                }
            }
            throw new ArgumentException(instrument);
        }

        private static readonly GuitarAction[] Frets =
            { GuitarAction.Fret1, GuitarAction.Fret2, GuitarAction.Fret3, GuitarAction.Fret4, GuitarAction.Fret5 };

        private static List<GameInput> RandomGuitarInputs(Random random, double end)
        {
            var list = new List<GameInput>();
            for (double t = 0; t < end; t += Step)
            {
                int roll = random.Next(4);
                if (roll == 0)
                {
                    list.Add(GameInput.Create(t, Frets[random.Next(Frets.Length)], random.Next(2) == 0));
                }
                else if (roll == 1)
                {
                    list.Add(GameInput.Create(t, GuitarAction.StrumDown, true));
                    list.Add(GameInput.Create(t + 0.02, GuitarAction.StrumDown, false));
                }
            }
            return list.OrderBy(i => i.Time).ToList();
        }

        private static List<GameInput> RandomDrumInputs(Random random, double end)
        {
            var list = new List<GameInput>();
            for (double t = 0; t < end; t += Step)
            {
                if (random.Next(3) == 0)
                {
                    list.Add(GameInput.Create(t, (DrumsAction) random.Next(8), 1f));
                }
            }
            return list;
        }

        private static readonly ProKeysAction[] Keys =
        {
            ProKeysAction.GreenKey, ProKeysAction.RedKey, ProKeysAction.YellowKey,
            ProKeysAction.BlueKey, ProKeysAction.OrangeKey,
        };

        private static List<GameInput> RandomKeysInputs(Random random, double end)
        {
            var list = new List<GameInput>();
            for (double t = 0; t < end; t += Step)
            {
                if (random.Next(3) == 0)
                {
                    list.Add(GameInput.Create(t, Keys[random.Next(Keys.Length)], random.Next(2) == 0));
                }
            }
            return list;
        }

        // Sings the chart's pitches with a little jitter, and random pitches between phrases, so the
        // engine hits some notes and misses others.
        private static List<GameInput> ScriptedVocalInputs(Random random, double end,
            InstrumentDifficulty<VocalNote> notes)
        {
            var lyrics = notes.Notes.SelectMany(n => n.ChildNotes).Where(n => !n.IsPercussion).ToList();
            var list = new List<GameInput>();
            int index = 0;
            for (double t = 0; t < end; t += Step)
            {
                while (index < lyrics.Count && lyrics[index].TotalTimeEnd < t)
                {
                    index++;
                }

                float pitch;
                if (index < lyrics.Count && lyrics[index].Time <= t && random.Next(5) != 0)
                {
                    pitch = lyrics[index].Pitch + (float) (random.NextDouble() - 0.5);
                }
                else
                {
                    pitch = 40f + (float) random.NextDouble() * 40f;
                }
                list.Add(GameInput.Create(t, VocalsAction.Pitch, pitch));
            }
            return list;
        }

        // The engines' scores depend on the times Update is called with (sustain ticks round per update),
        // so "the same stats" only makes sense for the same update times. The original replays the
        // update times the remote engine ran with; what differs is only how the inputs got there.
        private static void RunOriginal(BaseEngine engine, List<GameInput> inputs, IEnumerable<double> updateTimes)
        {
            int next = 0;
            foreach (double t in updateTimes)
            {
                while (next < inputs.Count && inputs[next].Time <= t)
                {
                    var input = inputs[next++];
                    engine.QueueInput(ref input);
                }
                engine.Update(t);
            }
        }

        private static List<double> RunRemote(BaseEngine engine, List<GameInput> inputs, double end)
        {
            var updates = new List<double>();
            // Sender side: the same loop as the original, but inputs go through the batcher and codec.
            var batcher = new InputBatcher(1);
            var batches = new List<(double FlushTime, InputBatch Batch)>();
            int next = 0;
            for (double t = 0; t <= end + 1; t += Step)
            {
                while (next < inputs.Count && inputs[next].Time <= t)
                {
                    batcher.Add(inputs[next++]);
                }
                batcher.SetWatermark(t);
                var batch = batcher.TryFlush(t);
                if (batch != null)
                {
                    var decoded = (InputBatch) MessageCodec.Decode(MessageCodec.Encode(batch));
                    batches.Add((t, decoded));
                }
            }

            // Receiver side, same as BasePlayer.UpdateRemoteInputs, with irregular frame times.
            var buffer = new RemoteInputBuffer(0.2);
            int[] frameMs = { 7, 23, 41, 16 };
            int deliver = 0;
            double engineTime = double.NegativeInfinity;
            double localTime = 0;
            for (int frame = 0; localTime <= end + 1.5; frame++)
            {
                localTime += frameMs[frame % frameMs.Length] / 1000.0;
                while (deliver < batches.Count && batches[deliver].FlushTime <= localTime)
                {
                    buffer.AddBatch(batches[deliver++].Batch);
                }

                double target = buffer.GetTargetTime(localTime);
                if (double.IsNegativeInfinity(target)) continue;

                while (buffer.TryDequeue(target, out var input))
                {
                    engine.QueueInput(ref input);
                }
                if (target > engineTime)
                {
                    engineTime = target;
                    updates.Add(target);
                    engine.Update(target);
                }
            }

            while (deliver < batches.Count)
            {
                buffer.AddBatch(batches[deliver++].Batch);
            }
            while (buffer.TryDequeue(end + 1, out var input))
            {
                engine.QueueInput(ref input);
            }
            updates.Add(end + 1);
            engine.Update(end + 1);
            return updates;
        }

        private static void AssertSameStats(BaseStats expected, BaseStats actual)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.TotalScore, Is.EqualTo(expected.TotalScore), nameof(BaseStats.TotalScore));
                Assert.That(actual.NotesHit, Is.EqualTo(expected.NotesHit), nameof(BaseStats.NotesHit));
                Assert.That(actual.NotesMissed, Is.EqualTo(expected.NotesMissed), nameof(BaseStats.NotesMissed));
                Assert.That(actual.MaxCombo, Is.EqualTo(expected.MaxCombo), nameof(BaseStats.MaxCombo));
                Assert.That(actual.StarPowerPhrasesHit, Is.EqualTo(expected.StarPowerPhrasesHit),
                    nameof(BaseStats.StarPowerPhrasesHit));
                Assert.That(actual.BandBonusScore, Is.EqualTo(expected.BandBonusScore), nameof(BaseStats.BandBonusScore));
            });
        }

        private static void Compare(string instrument, bool isBot)
        {
            var original = Create(instrument, isBot);
            var remote = Create(instrument, isBot);
            if (original == null || remote == null)
            {
                // These five instruments must exist in the test chart; anything else would be ignored.
                Assert.Fail($"The test chart has no Expert notes for required instrument {instrument}");
                return;
            }

            var inputs = isBot ? new List<GameInput>() : original.Human(new Random(1234), original.End);

            var updateTimes = RunRemote(remote.Engine, inputs, remote.End);
            RunOriginal(original.Engine, inputs, updateTimes);

            if (!isBot)
            {
                Assert.That(original.Engine.BaseStats.NotesHit, Is.GreaterThan(0),
                    "Sanity: the random player should hit something, or the comparison proves nothing");
            }
            AssertSameStats(original.Engine.BaseStats, remote.Engine.BaseStats);
        }

        [TestCaseSource(nameof(Instruments))]
        public void Bot_RemoteMatchesOriginal(string instrument) => Compare(instrument, true);

        [TestCaseSource(nameof(Instruments))]
        public void Human_RemoteMatchesOriginal(string instrument) => Compare(instrument, false);
    }
}
