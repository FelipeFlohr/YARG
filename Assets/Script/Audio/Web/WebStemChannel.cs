#nullable enable
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// One stem of a <see cref="WebStemMixer"/>: an AudioBuffer source into its own GainNode. Volume changes ramp
    /// over a few milliseconds, so muting a stem on a miss does not click. Whammy and reverb do nothing on the web.
    /// </summary>
    public sealed class WebStemChannel : StemChannel
    {
        internal readonly int Handle;

        internal WebStemChannel(AudioManager manager, SongStem stem, bool clampStemVolume, int handle)
            : base(manager, stem, clampStemVolume)
        {
            Handle = handle;
        }

        /// <summary>
        /// The volume a new stem starts at, the same as the BASS stems.
        /// </summary>
        internal static double InitialVolume(SongStem stem, bool clampStemVolume)
        {
            double volume = GlobalAudioHandler.GetTrueVolume(stem);
            if (clampStemVolume && volume < MINIMUM_STEM_VOLUME)
            {
                volume = MINIMUM_STEM_VOLUME;
            }

            return volume;
        }

        protected override void SetWhammyPitch_Internal(float percent)
        {
        }

        protected override float GetWhammyPitch_Internal() => 0f;

        // Stems share the mixer's clock, so the mixer positions all of them at once
        protected override void SetPosition_Internal(double position)
        {
        }

        protected override void SetVolume_Internal(double newVolume) =>
            WebAudioNative.WebAudio_StemSetVolume(Handle, newVolume);

        protected override void SetReverb_Internal(bool reverb)
        {
        }

        protected override void DisposeUnmanagedResources() => WebAudioNative.WebAudio_StemFree(Handle);
    }
}
