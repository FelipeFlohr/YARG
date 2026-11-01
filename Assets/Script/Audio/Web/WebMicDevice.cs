#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Audio.PitchDetection;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;
using YARG.Menu.Persistent;
using YARG.Settings;

namespace YARG.Audio.Web
{
    /// <summary>
    /// The browser microphone (getUserMedia, without echo cancellation, noise suppression or gain control). An
    /// AudioWorklet fills a ring buffer in JS; <see cref="WebAudioManager"/> drains it once per frame into the same
    /// analysis as <see cref="BASS.BassMicAnalyzer"/>: 40 ms frames, amplitude in dB, hits and
    /// <see cref="PitchTracker"/>.
    /// </summary>
    public sealed class WebMicDevice : MicDevice
    {
        private const float HIT_THRESHOLD_DB  = 25f;
        private const float SILENCE_FLOOR_DB  = -160f;
        private const float CALIBRATION_GAIN  = 180f;
        private const float UNAVAILABLE_VALUE = -1f;
        private const int   AMPLITUDE_STRIDE  = 4;

        // Samples per message from the worklet tap (YargWebAudio.jspre TAP_BLOCK)
        private const int TAP_BLOCK = 1024;

        private readonly Queue<MicOutputFrame> _outputFrames = new();
        private readonly string                _baseName;

        private int           _handle;
        private int           _sampleRate;
        private PitchTracker? _pitchTracker;
        private float[]       _readBuffer  = Array.Empty<float>();
        private float[]       _frameBuffer = Array.Empty<float>();
        private int           _frameSamples;
        private float?        _lastAmplitude;
        private float?        _lastPitch;
        private bool          _failureReported;

        private WebMicDevice(string displayName, string baseName, int handle) : base(displayName)
        {
            _baseName = baseName;
            _handle = handle;
        }

        internal static WebMicDevice? Open(string displayName, string deviceId)
        {
            int handle = WebAudioNative.WebAudio_MicOpen(deviceId);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to open microphone {0}", displayName);
                return null;
            }

            return new WebMicDevice(displayName, displayName, handle);
        }

        /// <summary>
        /// True once the browser gave access to the microphone.
        /// </summary>
        public bool IsOpen => _handle != 0 && WebAudioNative.WebAudio_MicState(_handle) == 1;

        internal bool IsClosed => _handle == 0;

        /// <summary>
        /// Called by <see cref="WebAudioManager"/> once per frame.
        /// </summary>
        internal void Update()
        {
            if (_handle == 0)
            {
                return;
            }

            int state = WebAudioNative.WebAudio_MicState(_handle);
            if (state < 0)
            {
                ReportFailure();
                return;
            }

            if (state == 0)
            {
                return;
            }

            if (_pitchTracker == null)
            {
                Initialize();
            }

            if (!IsRecordingOutput)
            {
                if (_frameSamples > 0 || _lastAmplitude != null || WebAudioNative.WebAudio_MicAvailable(_handle) > 0)
                {
                    WebAudioNative.WebAudio_MicReset(_handle);
                    ClearState();
                }

                return;
            }

            double readTime = InputManager.CurrentInputTime;
            int backlog = WebAudioNative.WebAudio_MicAvailable(_handle);
            int consumed = 0;
            while (backlog - consumed > 0)
            {
                int read = WebAudioNative.WebAudio_MicRead(_handle, _readBuffer, _readBuffer.Length);
                if (read <= 0)
                {
                    break;
                }

                AssembleFrames(read, readTime, backlog, consumed);
                consumed += read;
            }
        }

        private void Initialize()
        {
            _sampleRate = Math.Max(8000, WebAudioNative.WebAudio_SampleRate());
            int samplesPerFrame = _sampleRate * RECORD_PERIOD_MS / 1000;
            _readBuffer = new float[Math.Max(samplesPerFrame, TAP_BLOCK)];
            _frameBuffer = new float[samplesPerFrame];
            _pitchTracker = new PitchTracker(_sampleRate);
            YargLogger.LogFormatInfo("Microphone {0} open at {1} Hz", DisplayName, _sampleRate);
        }

        private void ReportFailure()
        {
            if (_failureReported)
            {
                return;
            }

            _failureReported = true;
            string error = WebAudioNative.WebAudio_MicError(_handle) ?? "unknown error";
            YargLogger.LogWarning($"Microphone {DisplayName} unavailable: {error}");
            ToastManager.ToastWarning(error == "NotAllowedError"
                ? "Microphone access was denied. Allow it in the browser, or switch to another instrument."
                : "The microphone could not be opened. Switch to another instrument or try another device.");
        }

        private void AssembleFrames(int sampleCount, double readTime, int backlog, int consumedBefore)
        {
            int offset = 0;
            while (offset < sampleCount)
            {
                int frameSpace = _frameBuffer.Length - _frameSamples;
                int samplesToCopy = Math.Min(sampleCount - offset, frameSpace);
                Array.Copy(_readBuffer, offset, _frameBuffer, _frameSamples, samplesToCopy);

                _frameSamples += samplesToCopy;
                offset += samplesToCopy;

                if (_frameSamples == _frameBuffer.Length)
                {
                    int samplesStillAhead = Math.Max(0, backlog - consumedBefore - offset);
                    double frameEndTime = readTime - samplesStillAhead / (double) _sampleRate;
                    AnalyzeFrame(frameEndTime);
                    _frameSamples = 0;
                }
            }
        }

        private void AnalyzeFrame(double frameEndTime)
        {
            float amplitude = MeasureAmplitude(_frameBuffer);

            if (_lastAmplitude is { } previousAmplitude && amplitude - previousAmplitude >= HIT_THRESHOLD_DB)
            {
                double midpoint = frameEndTime - RECORD_PERIOD_MS / 2000.0;
                _outputFrames.Enqueue(new MicOutputFrame(midpoint, true, UNAVAILABLE_VALUE, UNAVAILABLE_VALUE));
            }

            _lastAmplitude = amplitude;

            float sensitivity = SettingsManager.Settings?.MicrophoneSensitivity.Value ?? 2f;
            if (amplitude < sensitivity)
            {
                _lastPitch = null;
                WebAudioNative.WebAudio_MicReportPitch(_handle, 0, amplitude);
                return;
            }

            _lastPitch = _pitchTracker!.ProcessBuffer(_frameBuffer) ?? _lastPitch;

            if (_lastPitch is { } pitch)
            {
                _outputFrames.Enqueue(new MicOutputFrame(frameEndTime, false, pitch, amplitude));
            }

            WebAudioNative.WebAudio_MicReportPitch(_handle, _lastPitch ?? 0, amplitude);
        }

        private static float MeasureAmplitude(ReadOnlySpan<float> samples)
        {
            float sumOfSquares = 0f;
            int sampleCount = 0;

            for (int i = 0; i < samples.Length; i += AMPLITUDE_STRIDE)
            {
                sumOfSquares += samples[i] * samples[i];
                sampleCount++;
            }

            float rootMeanSquare = Mathf.Sqrt(sumOfSquares / sampleCount);
            float decibels = 20f * Mathf.Log10(rootMeanSquare * CALIBRATION_GAIN);

            if (decibels < SILENCE_FLOOR_DB || float.IsNaN(decibels))
            {
                return SILENCE_FLOOR_DB;
            }

            return decibels;
        }

        private void ClearState()
        {
            _lastPitch = null;
            _lastAmplitude = null;
            _frameSamples = 0;
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
            _pitchTracker?.Reset();
            _outputFrames.Clear();
        }

        public override int Reset()
        {
            if (_handle == 0)
            {
                return -1;
            }

            WebAudioNative.WebAudio_MicReset(_handle);
            ClearState();
            return 0;
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame) => _outputFrames.TryDequeue(out frame);

        public override void ClearOutputQueue() => _outputFrames.Clear();

        // Monitoring starts muted (the JS gain is 0) until the player turns it up
        public override void SetMonitoringLevel(float volume) =>
            WebAudioNative.WebAudio_MicSetMonitoring(_handle, volume);

        public override void SetReverbLevel(float wet)
        {
        }

        public override MicBufferInfo? GetBufferInfo()
        {
            if (!IsOpen)
            {
                return null;
            }

            int waiting = Math.Max(0, WebAudioNative.WebAudio_MicAvailable(_handle));
            return new MicBufferInfo(TAP_BLOCK, TAP_BLOCK * 1000.0 / _sampleRate, _sampleRate, 1, false, 0,
                waiting * sizeof(float));
        }

        public override SerializedMic Serialize() => new(_baseName, 0);

        protected override void DisposeUnmanagedResources()
        {
            if (_handle != 0)
            {
                WebAudioNative.WebAudio_MicClose(_handle);
                _handle = 0;
            }
        }
    }
}
