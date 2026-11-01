#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Metronome click, with its high and low samples.
    /// </summary>
    public sealed class WebMetronomeSampleChannel : MetronomeSampleChannel
    {
        private readonly WebSampleHandle _hi;
        private readonly WebSampleHandle _lo;
        private          double          _playVolume = 1;

        private WebMetronomeSampleChannel(MetronomeSample sample, string hiPath, WebSampleHandle hi, string loPath,
            WebSampleHandle lo) : base(sample, hiPath, loPath)
        {
            _hi = hi;
            _lo = lo;
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Metronome));
        }

        internal static WebMetronomeSampleChannel? Create(MetronomeSample sample, string hiPath, string loPath)
        {
            var hi = WebSampleHandle.Load(hiPath);
            if (hi == null)
            {
                return null;
            }

            var lo = WebSampleHandle.Load(loPath);
            if (lo == null)
            {
                hi.Dispose();
                return null;
            }

            return new WebMetronomeSampleChannel(sample, hiPath, hi, loPath, lo);
        }

        internal void Play(MetronomePitch pitch, double volume) =>
            (pitch == MetronomePitch.Hi ? _hi : _lo).Play(volume);

        protected override void PlayHi_Internal() => _hi.Play(_playVolume);

        protected override void PlayLo_Internal() => _lo.Play(_playVolume);

        protected override int CreateStream_Internal(MetronomePitch pitch) =>
            pitch == MetronomePitch.Hi ? _hi.Handle : _lo.Handle;

        protected override void SetVolume_Internal(double volume) =>
            _playVolume = volume * AudioHelpers.MetronomeSamples[(int) Sample].Volume;

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override void DisposeUnmanagedResources()
        {
            _hi.Dispose();
            _lo.Dispose();
        }
    }
}
