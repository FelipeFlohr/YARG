#nullable enable
using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Sample plays at fixed song times (metronome, crowd claps). The web audio module schedules the ones in the
    /// next half second on the context clock, and reschedules after seeks, pauses and speed changes.
    /// </summary>
    internal sealed class WebOneShotChannel : OneShotChannel
    {
        private int _handle;

        private WebOneShotChannel(int handle)
        {
            _handle = handle;
        }

        public static WebOneShotChannel? Create(int mixer, int sample, IReadOnlyList<double> scheduledPlays,
            double outputLeadTime)
        {
            var times = ToArray(scheduledPlays);
            int handle = WebAudioNative.WebAudio_OneShotCreate(mixer, sample, times, times.Length, outputLeadTime);
            return handle == 0 ? null : new WebOneShotChannel(handle);
        }

        public override void SetEnabled(bool enabled) =>
            WebAudioNative.WebAudio_OneShotSetEnabled(_handle, enabled ? 1 : 0);

        public override void SetVolume(double volume) => WebAudioNative.WebAudio_OneShotSetVolume(_handle, volume);

        public override void UpdateSchedule(IReadOnlyList<double> scheduledPlays)
        {
            var times = ToArray(scheduledPlays);
            WebAudioNative.WebAudio_OneShotSetSchedule(_handle, times, times.Length);
        }

        public override void Dispose()
        {
            if (_handle != 0)
            {
                WebAudioNative.WebAudio_OneShotFree(_handle);
                _handle = 0;
            }
        }

        private static double[] ToArray(IReadOnlyList<double> values)
        {
            var array = new double[values.Count];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = values[i];
            }

            return array;
        }
    }

    /// <summary>
    /// Returned when the one-shot could not be created, so the schedulers can still call it.
    /// </summary>
    internal sealed class NullOneShotChannel : OneShotChannel
    {
        public override void SetEnabled(bool enabled)
        {
        }

        public override void SetVolume(double volume)
        {
        }

        public override void UpdateSchedule(IReadOnlyList<double> scheduledPlays)
        {
        }

        public override void Dispose()
        {
        }
    }
}
