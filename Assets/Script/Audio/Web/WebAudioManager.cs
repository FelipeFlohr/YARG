#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Audio.Web
{
    /// <summary>
    /// Audio for the web build, on one <c>AudioContext</c> (YargWebAudio.jspre). The context starts suspended and
    /// resumes on the first user gesture, the click on "Click to start". There is a single stereo output: output
    /// devices, output channels and buffer lengths do nothing here.
    /// </summary>
    public sealed class WebAudioManager : AudioManager
    {
        private const string DEFAULT_OUTPUT_DEVICE = "Default";
        private const string DEFAULT_MICROPHONE    = "Default microphone";

        // Requests from window.yargAudioDebug (YargWebAudio.jspre debugRequests)
        private const int DEBUG_TONE_START = 1;
        private const int DEBUG_TONE_STOP  = 2;
        private const int DEBUG_MIC_START  = 3;
        private const int DEBUG_MIC_STOP   = 4;

        // What decodeAudioData reads in every major browser (AIFF is not one of them)
        private static readonly string[] FORMATS =
        {
            ".ogg",
            ".mogg",
            ".wav",
            ".mp3",
            ".opus",
        };

        private readonly List<WebStemMixer> _mixers = new();
        private readonly List<WebMicDevice> _mics   = new();

        private WebStemMixer? _debugTone;
        private WebMicDevice? _debugMic;

        public WebAudioManager()
        {
            if (WebAudioNative.WebAudio_Init() == 0)
            {
                YargLogger.LogError("Web Audio is not available in this browser");
                return;
            }

            MinimumBufferLength = 0;
            MaximumBufferLength = 0;
            UpdatePlaybackLatency();
            WebAudioNative.WebAudio_MicRefreshDevices();

            LoadSamples();
            YargLogger.LogFormatInfo("Web Audio initialized: {0} Hz", WebAudioNative.WebAudio_SampleRate());
        }

        protected override ReadOnlySpan<string> SupportedFormats => FORMATS;

        /// <summary>
        /// The BASS volume curve, so mixer volumes sound the same as on the desktop.
        /// </summary>
        internal static double ExponentialVolume(double volume) => Math.Pow(2, volume) - 1;

        protected override void Update()
        {
            WebAudioNative.WebAudio_Tick();
            UpdatePlaybackLatency();

            for (int i = _mixers.Count - 1; i >= 0; i--)
            {
                if (_mixers[i].Handle == 0)
                {
                    _mixers.RemoveAt(i);
                }
            }

            // A song end handler may dispose mixers or create new ones
            foreach (var mixer in _mixers.ToArray())
            {
                mixer.Update();
            }

            for (int i = _mics.Count - 1; i >= 0; i--)
            {
                var mic = _mics[i];
                if (mic.IsClosed)
                {
                    _mics.RemoveAt(i);
                    continue;
                }

                mic.Update();
            }

            if (_debugMic != null)
            {
                // Nobody plays the test microphone: its pitch only goes to the debug hooks
                _debugMic.ClearOutputQueue();
            }

            ServeDebugRequests();
        }

        // outputLatency + baseLatency, added to the calibration when AccountForHardwareLatency is on (the default)
        private void UpdatePlaybackLatency() =>
            PlaybackLatency = (int) Math.Round(WebAudioNative.WebAudio_Latency() * 1000);

        protected override StemMixer? CreateMixer(string name, float speed, double volume, bool clampStemVolume,
            bool normalize)
        {
            var mixer = WebStemMixer.Create(name, this, speed, volume, clampStemVolume);
            if (mixer != null)
            {
                _mixers.Add(mixer);
            }

            return mixer;
        }

        protected override List<InputDeviceInfo> GetAllInputDevices()
        {
            WebAudioNative.WebAudio_MicRefreshDevices();

            var devices = new List<InputDeviceInfo>();
            int count = WebAudioNative.WebAudio_MicDeviceCount();
            for (int i = 0; i < count; i++)
            {
                string? name = WebAudioNative.WebAudio_MicDeviceName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    devices.Add(new InputDeviceInfo(i, name!, 0, 1));
                }
            }

            if (devices.Count == 0)
            {
                devices.Add(new InputDeviceInfo(0, DEFAULT_MICROPHONE, 0, 1));
            }

            return devices;
        }

        protected override MicDevice? CreateInputDevice(InputDeviceInfo device)
        {
            var mic = WebMicDevice.Open(device.Name, FindMicrophoneId(device.Name));
            if (mic != null)
            {
                _mics.Add(mic);
            }

            return mic;
        }

        // The browser's id of a microphone by name, or "" for the default one (also when the saved name is gone)
        private static string FindMicrophoneId(string name)
        {
            int count = WebAudioNative.WebAudio_MicDeviceCount();
            for (int i = 0; i < count; i++)
            {
                if (WebAudioNative.WebAudio_MicDeviceName(i) == name)
                {
                    return WebAudioNative.WebAudio_MicDeviceId(i) ?? "";
                }
            }

            return "";
        }

        protected override OutputChannel? CreateOutputChannel(int channelId) => null;

        public override PushStream CreatePushStream(int sampleRate, int channelCount) =>
            throw new NotSupportedException("The web audio backend does not stream live audio");

        protected override List<(int id, string name)> GetAllOutputDevices() =>
            new List<(int id, string name)> { (0, DEFAULT_OUTPUT_DEVICE) };

        protected override int GetOutputChannelCount() => 0;

        // The browser picks the output device
        protected override bool SetOutputDevice(string name) => true;

        protected override bool ReinitializeOutput() => true;

        protected override void SetMasterVolume(double volume) => WebAudioNative.WebAudio_SetMasterVolume(volume);

        protected override void SetBufferLength_Internal(int length)
        {
        }

        public override void LoadVenueSample(string name, byte[] sampleData, OutputChannel? outputChannel = null)
        {
            if (VenueSamples.TryGetValue(name, out var existing))
            {
                existing.Dispose();
                VenueSamples.Remove(name);
            }

            var sample = WebVenueSampleChannel.Create(name, sampleData);
            if (sample != null)
            {
                VenueSamples[name] = sample;
            }
        }

        public override void ClearVenueSamples()
        {
            foreach (var sample in VenueSamples.Values)
            {
                sample.Stop();
                sample.Dispose();
            }

            VenueSamples.Clear();
        }

        protected override void PlayMetronomeSoundEffectToChannel(MetronomeSample sample, MetronomePitch pitch,
            int channelId)
        {
            if ((int) sample < 0 || (int) sample >= MetronomeSamples.Length ||
                MetronomeSamples[(int) sample] is not WebMetronomeSampleChannel channel)
            {
                return;
            }

            double volume = GlobalAudioHandler.GetTrueVolume(SongStem.Metronome) *
                AudioHelpers.MetronomeSamples[(int) sample].Volume;
            channel.Play(pitch, volume);
        }

        private void LoadSamples()
        {
            string root = PathHelper.StreamingAssetsPath;

            string sfxFolder = Path.Combine(root, "sfx");
            foreach (var sample in AudioHelpers.SfxSamples)
            {
                string? path = FindSamplePath(sfxFolder, sample.File);
                if (path != null)
                {
                    SfxSamples[(int) sample.Kind] = WebSampleChannel.Create(sample.Kind, path, sample.CanLoop)!;
                }
            }

            string drumFolder = Path.Combine(root, "drumSfx");
            foreach (var sample in AudioHelpers.DrumSamples)
            {
                string? path = FindSamplePath(drumFolder, sample.File);
                if (path != null)
                {
                    DrumSfxSamples[(int) sample.Kind] = WebDrumSampleChannel.Create(sample.Kind, path)!;
                }
            }

            string voxFolder = Path.Combine(root, "vox");
            foreach (var sample in AudioHelpers.VoxSamples)
            {
                string? path = FindSamplePath(voxFolder, sample.File);
                if (path != null)
                {
                    VoxSamples[(int) sample.Kind] = WebVoxSampleChannel.Create(sample.Kind, path)!;
                }
            }

            string metronomeFolder = Path.Combine(root, "metronome");
            foreach (var sample in AudioHelpers.MetronomeSamples)
            {
                string? hiPath = FindSamplePath(metronomeFolder, sample.File);
                string? loPath = FindSamplePath(metronomeFolder, sample.AlternateFile);
                if (hiPath != null && loPath != null)
                {
                    MetronomeSamples[(int) sample.Kind] =
                        WebMetronomeSampleChannel.Create(sample.Kind, hiPath, loPath)!;
                }
            }

            YargLogger.LogInfo("Loaded web audio samples");
        }

        private static string? FindSamplePath(string folder, string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }

            string pathWithoutExtension = Path.Combine(folder, file);
            foreach (string format in FORMATS)
            {
                string path = pathWithoutExtension + format;
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private void ServeDebugRequests()
        {
            int request;
            while ((request = WebAudioNative.WebAudio_DebugTakeRequest()) != 0)
            {
                switch (request)
                {
                    case DEBUG_TONE_START:
                        StartToneTest();
                        break;
                    case DEBUG_TONE_STOP:
                        _debugTone?.Dispose();
                        _debugTone = null;
                        break;
                    case DEBUG_MIC_START:
                        StartMicTest();
                        break;
                    case DEBUG_MIC_STOP:
                        _debugMic?.Dispose();
                        _debugMic = null;
                        break;
                }
            }
        }

        // Plays a tone generated in JS through the same path as a song: AddChannel, decode, Play
        private void StartToneTest()
        {
            _debugTone?.Dispose();
            _debugTone = null;

            int length = WebAudioNative.WebAudio_DebugToneWavLength();
            if (length <= 0)
            {
                return;
            }

            var data = new byte[length];
            WebAudioNative.WebAudio_DebugCopyToneWav(data, length);

            var mixer = CreateMixer("Web audio tone test", 1f, 1.0, false, false) as WebStemMixer;
            if (mixer == null)
            {
                YargLogger.LogError("[WebAudio] Tone test: failed to create the mixer");
                return;
            }

            if (!mixer.AddChannel(new MemoryStream(data), SongStem.Song))
            {
                YargLogger.LogError("[WebAudio] Tone test: failed to add the stem");
                mixer.Dispose();
                return;
            }

            mixer.Play();
            _debugTone = mixer;
            YargLogger.LogFormatInfo("[WebAudio] Tone test playing on mixer {0}", mixer.Handle);
        }

        private void StartMicTest()
        {
            _debugMic?.Dispose();
            _debugMic = CreateInputDevice(new InputDeviceInfo(0, DEFAULT_MICROPHONE, 0, 1)) as WebMicDevice;
            if (_debugMic == null)
            {
                YargLogger.LogError("[WebAudio] Mic test: failed to open the microphone");
                return;
            }

            _debugMic.IsRecordingOutput = true;
            YargLogger.LogInfo("[WebAudio] Mic test started");
        }

        protected override void DisposeManagedResources()
        {
            _debugTone?.Dispose();
            _debugTone = null;

            foreach (var mic in _mics.ToArray())
            {
                mic.Dispose();
            }

            _mics.Clear();
            _debugMic = null;
        }
    }
}
