#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Venue sound loaded from memory.
    /// </summary>
    public sealed class WebVenueSampleChannel : VenueSampleChannel
    {
        private readonly WebSampleHandle _sample;

        private WebVenueSampleChannel(string name, byte[] sampleData, WebSampleHandle handle)
            : base(name, sampleData)
        {
            _sample = handle;
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.VoxSample));
        }

        internal static WebVenueSampleChannel? Create(string name, byte[] sampleData)
        {
            var handle = WebSampleHandle.Load(sampleData, name);
            return handle == null ? null : new WebVenueSampleChannel(name, sampleData, handle);
        }

        protected override void Play_Internal() => _sample.Play(1);

        protected override void Stop_Internal() => _sample.Stop(0);

        protected override void Pause_Internal() => _sample.Pause();

        protected override void Resume_Internal() => _sample.Resume();

        protected override void SetVolume_Internal(double volume) => _sample.SetVolume(volume);

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override bool IsPlaying_Internal() => _sample.IsPlaying;

        protected override bool IsPaused_Internal() => _sample.IsPaused;

        protected override void DisposeUnmanagedResources() => _sample.Dispose();
    }
}
