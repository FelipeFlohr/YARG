#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Vocal announcer clip. Clips are queued one after the other in the web audio module, like the BASS version.
    /// </summary>
    public sealed class WebVoxSampleChannel : VoxSampleChannel
    {
        private readonly WebSampleHandle _sample;

        private WebVoxSampleChannel(VoxSample sample, string path, WebSampleHandle handle) : base(sample, path)
        {
            _sample = handle;
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.VoxSample));
        }

        internal static WebVoxSampleChannel? Create(VoxSample sample, string path)
        {
            var handle = WebSampleHandle.Load(path);
            return handle == null ? null : new WebVoxSampleChannel(sample, path, handle);
        }

        protected override void Play_Internal() => _sample.Play(1, queued: true);

        protected override void SetVolume_Internal(double volume) => _sample.SetVolume(volume);

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override bool IsPlaying_Internal() => _sample.IsPlaying;

        protected override void DisposeUnmanagedResources() => _sample.Dispose();
    }
}
