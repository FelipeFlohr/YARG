#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Drum hit sample. Every hit is its own voice with the volume set right before it.
    /// </summary>
    public sealed class WebDrumSampleChannel : DrumSampleChannel
    {
        private readonly WebSampleHandle _sample;
        private          double          _hitVolume = 1;

        private WebDrumSampleChannel(DrumSfxSample sample, string path, WebSampleHandle handle) : base(sample, path)
        {
            _sample = handle;
        }

        internal static WebDrumSampleChannel? Create(DrumSfxSample sample, string path)
        {
            var handle = WebSampleHandle.Load(path);
            return handle == null ? null : new WebDrumSampleChannel(sample, path, handle);
        }

        protected override void Play_Internal() => _sample.Play(_hitVolume);

        protected override void SetVolume_Internal(double volume) => _hitVolume = volume;

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override void DisposeUnmanagedResources() => _sample.Dispose();
    }
}
