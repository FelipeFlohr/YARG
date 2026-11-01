#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Sound effect (note miss, star power, crowd...) played from a preloaded AudioBuffer.
    /// </summary>
    public sealed class WebSampleChannel : SampleChannel
    {
        private readonly WebSampleHandle _sample;
        private readonly bool            _canLoop;

        private WebSampleChannel(SfxSample sample, string path, WebSampleHandle handle, bool canLoop)
            : base(sample, path)
        {
            _sample = handle;
            _canLoop = canLoop;
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Sfx));
        }

        internal static WebSampleChannel? Create(SfxSample sample, string path, bool canLoop)
        {
            var handle = WebSampleHandle.Load(path);
            return handle == null ? null : new WebSampleChannel(sample, path, handle, canLoop);
        }

        internal int Handle => _sample.Handle;

        protected override void Play_Internal(double duration)
        {
            if (_sample.Play(1, _canLoop, minimumGap: PLAYBACK_SUPPRESS_THRESHOLD))
            {
                AudioHelpers.SfxSamples[(int) Sample].IsPlaying = true;
            }
        }

        // The web one-shot channels play the sample itself, so the "stream" is the sample handle
        protected override int CreateStream_Internal() => _sample.Handle;

        protected override void Stop_Internal(double duration)
        {
            _sample.Stop(duration);
            AudioHelpers.SfxSamples[(int) Sample].IsPlaying = false;
        }

        protected override void Pause_Internal() => _sample.Pause();

        protected override void Resume_Internal()
        {
            if (AudioHelpers.SfxSamples[(int) Sample].IsPlaying)
            {
                _sample.Resume();
            }
        }

        protected override void SetVolume_Internal(double volume) =>
            _sample.SetVolume(volume * AudioHelpers.SfxSamples[(int) Sample].Volume);

        // There is a single stereo output on the web
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override void DisposeUnmanagedResources() => _sample.Dispose();
    }
}
