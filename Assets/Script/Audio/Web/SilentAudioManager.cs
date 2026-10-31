#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Audio manager without any output: no devices, no mixers, no samples. It lets the web build boot
    /// before the real web audio backend exists. Every sample slot stays null, which the
    /// <see cref="GlobalAudioHandler"/> sound effect calls already treat as "nothing to play".
    /// </summary>
    public sealed class SilentAudioManager : AudioManager
    {
        protected override ReadOnlySpan<string> SupportedFormats => Array.Empty<string>();

        protected override StemMixer? CreateMixer(string name, float speed, double volume, bool clampStemVolume,
            bool normalize) => null;

        protected override List<InputDeviceInfo> GetAllInputDevices() => new List<InputDeviceInfo>();

        protected override MicDevice? CreateInputDevice(InputDeviceInfo device) => null;

        protected override OutputChannel? CreateOutputChannel(int channelId) => null;

        public override PushStream CreatePushStream(int sampleRate, int channelCount) =>
            throw new NotSupportedException("The silent audio manager does not stream live audio");

        protected override List<(int id, string name)> GetAllOutputDevices() => new List<(int id, string name)>();

        protected override int GetOutputChannelCount() => 0;

        protected override bool SetOutputDevice(string name) => false;

        protected override void SetMasterVolume(double volume)
        {
        }

        protected override void SetBufferLength_Internal(int length)
        {
        }

        public override void LoadVenueSample(string name, byte[] sampleData, OutputChannel? outputChannel = null)
        {
        }

        public override void ClearVenueSamples()
        {
        }

        protected override void PlayMetronomeSoundEffectToChannel(MetronomeSample sample, MetronomePitch pitch,
            int channelId)
        {
        }
    }
}
