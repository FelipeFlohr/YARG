#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using YARG.Core.Logging;

namespace YARG.Audio.Web
{
    /// <summary>
    /// One preloaded AudioBuffer in the web audio module. Decoding finishes asynchronously; plays requested before
    /// that are dropped, which only matters for the first milliseconds after boot.
    /// </summary>
    internal sealed class WebSampleHandle : IDisposable
    {
        private readonly Stopwatch _sinceLastPlay = new();

        public readonly int Handle;

        private WebSampleHandle(int handle)
        {
            Handle = handle;
        }

        public static WebSampleHandle? Load(string path)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                YargLogger.LogError($"Failed to read sample {path}: {e.Message}");
                return null;
            }

            return Load(data, path);
        }

        public static WebSampleHandle? Load(byte[] data, string name)
        {
            int handle = WebAudioNative.WebAudio_SampleLoad(data, data.Length);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to load sample {0}", name);
                return null;
            }

            return new WebSampleHandle(handle);
        }

        public bool IsPlaying => WebAudioNative.WebAudio_SampleState(Handle) == 1;
        public bool IsPaused  => WebAudioNative.WebAudio_SampleState(Handle) == 2;

        /// <summary>
        /// Plays a new voice. With a minimum gap, a play that comes sooner than that after the previous one is
        /// dropped (like the BASS sample MinGap).
        /// </summary>
        public bool Play(double volume, bool loop = false, bool queued = false, double minimumGap = 0)
        {
            if (minimumGap > 0 && _sinceLastPlay.IsRunning && _sinceLastPlay.Elapsed.TotalSeconds < minimumGap)
            {
                return false;
            }

            _sinceLastPlay.Restart();
            return WebAudioNative.WebAudio_SamplePlay(Handle, volume, loop ? 1 : 0, queued ? 1 : 0) != 0;
        }

        public void Stop(double fade) => WebAudioNative.WebAudio_SampleStop(Handle, fade);

        public void Pause() => WebAudioNative.WebAudio_SamplePause(Handle);

        public void Resume() => WebAudioNative.WebAudio_SampleResume(Handle);

        /// <summary>
        /// Volume of every voice of this sample, including the ones already playing.
        /// </summary>
        public void SetVolume(double volume) => WebAudioNative.WebAudio_SampleSetVolume(Handle, volume);

        public void Dispose() => WebAudioNative.WebAudio_SampleFree(Handle);
    }
}
