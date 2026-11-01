#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.Web
{
    /// <summary>
    /// A song on the web: every stem is an AudioBuffer started at the same context time, and the song position is
    /// <c>(ctx.currentTime - t0) * rate + offset</c> (the clock lives in YargWebAudio.jspre). Seeking and pausing
    /// recreate the sources; speed is the sources' playbackRate, so it changes the pitch.
    /// </summary>
    /// <remarks>
    /// Stems decode asynchronously (see <see cref="IDeferredStemMixer"/>). To save memory, a stem that no player
    /// plays (<see cref="StemUsageHint"/>) gets no channel of its own: it is summed into the background stem as
    /// soon as it is decoded, before the next one starts decoding.
    /// </remarks>
    public sealed class WebStemMixer : StemMixer, IDeferredStemMixer
    {
        private const float MIN_PLAYBACK_SPEED = 0.05f;
        private const float MAX_PLAYBACK_SPEED = 16f;

        // A start is scheduled this far ahead (YargWebAudio.jspre START_LEAD), so a seek is heard that much later
        private const double START_LEAD = 0.05;

        private readonly HashSet<SongStem>? _playedStems;
        private readonly float[]            _levelBuffer = new float[2048];

        private int             _handle;
        private double          _volume;
        private float           _songSpeed;
        private float           _speed;
        private double          _outputLatency;
        private WebStemChannel? _background;
        private string?         _loadError;

        private WebStemMixer(string name, AudioManager manager, int handle, float speed, double volume,
            bool clampStemVolume) : base(name, manager, clampStemVolume)
        {
            _handle = handle;
            _volume = volume;
            _songSpeed = speed;
            _speed = speed;

            var played = StemUsageHint.PlayedStems;
            _playedStems = played == null ? null : new HashSet<SongStem>(played);
        }

        internal static WebStemMixer? Create(string name, AudioManager manager, float speed, double volume,
            bool clampStemVolume)
        {
            float clampedSpeed = Math.Clamp(speed, MIN_PLAYBACK_SPEED, MAX_PLAYBACK_SPEED);
            int handle = WebAudioNative.WebAudio_MixerCreate(WebAudioManager.ExponentialVolume(volume), clampedSpeed);
            if (handle == 0)
            {
                YargLogger.LogError("Failed to create web audio mixer");
                return null;
            }

            return new WebStemMixer(name, manager, handle, clampedSpeed, volume, clampStemVolume);
        }

        internal int Handle => _handle;

        public override event Action SongEnd
        {
            add => _songEnd += value;
            remove => _songEnd -= value;
        }

        public bool IsLoading
        {
            get
            {
                if (_handle == 0)
                {
                    return false;
                }

                bool loading = WebAudioNative.WebAudio_MixerIsLoading(_handle) != 0;
                if (!loading)
                {
                    RefreshAfterLoad();
                }

                return loading;
            }
        }

        public string? LoadError
        {
            get
            {
                if (_handle != 0 && _loadError == null)
                {
                    _loadError = WebAudioNative.WebAudio_MixerGetError(_handle);
                }

                return _loadError;
            }
        }

        /// <summary>
        /// Called by <see cref="WebAudioManager"/> once per frame.
        /// </summary>
        internal void Update()
        {
            if (_handle == 0)
            {
                return;
            }

            if (_length <= 0 && WebAudioNative.WebAudio_MixerIsLoading(_handle) == 0)
            {
                RefreshAfterLoad();
            }

            if (WebAudioNative.WebAudio_MixerTakeEnded(_handle) != 0)
            {
                _songEnd?.Invoke();
            }
        }

        private void RefreshAfterLoad()
        {
            _length = WebAudioNative.WebAudio_MixerGetLength(_handle);
        }

        public override OneShotChannel CreateOneShotChannel(int sampleStream, IReadOnlyList<double> scheduledPlays,
            double outputLeadTime = 0, OutputChannel? outputChannel = null)
        {
            return (OneShotChannel?) WebOneShotChannel.Create(_handle, sampleStream, scheduledPlays, outputLeadTime)
                ?? new NullOneShotChannel();
        }

        // The guide tone needs a synthesizer on the audio thread; the web build plays without it
        public override ToneChannel? CreateToneChannel(double volume, double fadeDuration) => null;

        protected override int Play_Internal() => WebAudioNative.WebAudio_MixerPlay(_handle);

        protected override int Pause_Internal() => WebAudioNative.WebAudio_MixerPause(_handle);

        protected override void FadeIn_Internal(double maxVolume, double duration)
        {
            _volume = maxVolume;
            WebAudioNative.WebAudio_MixerFade(_handle, WebAudioManager.ExponentialVolume(maxVolume), duration);
        }

        protected override void FadeOut_Internal(double duration)
        {
            _volume = 0;
            WebAudioNative.WebAudio_MixerFade(_handle, 0, duration);
        }

        protected override double GetPosition_Internal() => WebAudioNative.WebAudio_MixerGetPosition(_handle);

        protected override SyncPosition GetSyncPosition_Internal()
        {
            double position = GetPosition_Internal();
            return new SyncPosition(position, position - _outputLatency * _songSpeed);
        }

        protected override double GetTransportLatency_Internal() => START_LEAD;

        protected override double GetVolume_Internal() => _volume;

        protected override void SetPosition_Internal(double position) =>
            WebAudioNative.WebAudio_MixerSetPosition(_handle, position);

        protected override void SetVolume_Internal(double volume)
        {
            _volume = volume;
            WebAudioNative.WebAudio_MixerSetVolume(_handle, WebAudioManager.ExponentialVolume(volume));
        }

        protected override int GetSampleData_Internal(float[] buffer)
        {
            int count = WebAudioNative.WebAudio_MixerGetSamples(_handle, buffer, buffer.Length);
            return count < 0 ? -1 : count * sizeof(float);
        }

        protected override int GetFFTData_Internal(float[] buffer, int fftSize, bool complex)
        {
            if (complex)
            {
                return -1;
            }

            int bins = Math.Min(buffer.Length, (1 << fftSize) / 2);
            int count = WebAudioNative.WebAudio_MixerGetFft(_handle, buffer, bins, fftSize);
            return count < 0 ? -1 : (1 << fftSize) * sizeof(float);
        }

        // Mono RMS of the latest output, like the BASS level query
        protected override int GetLevel_Internal(float[] level)
        {
            int count = WebAudioNative.WebAudio_MixerGetSamples(_handle, _levelBuffer, _levelBuffer.Length);
            if (count <= 0)
            {
                return -1;
            }

            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += _levelBuffer[i] * _levelBuffer[i];
            }

            float rms = (float) Math.Sqrt(sum / count);
            for (int i = 0; i < level.Length; i++)
            {
                level[i] = rms;
            }

            return 0;
        }

        protected override void SetPlaybackSpeed_Internal(float songSpeed, float syncAdjustment, bool shiftPitch)
        {
            float effectiveSpeed = Math.Clamp(songSpeed + syncAdjustment, MIN_PLAYBACK_SPEED, MAX_PLAYBACK_SPEED);
            _songSpeed = songSpeed;
            if (_speed == effectiveSpeed)
            {
                return;
            }

            _speed = effectiveSpeed;
            WebAudioNative.WebAudio_MixerSetSpeed(_handle, effectiveSpeed);
        }

        protected override void SetOutputLatency_Internal(double latency) => _outputLatency = latency;

        protected override bool AddChannels_Internal(Stream stream, params StemInfo[] stemInfos)
        {
            if (_handle == 0 || stemInfos.Length == 0)
            {
                return false;
            }

            byte[] data;
            try
            {
                data = ReadAll(stream);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to read audio stream");
                return false;
            }

            var created = new List<WebStemChannel>();
            var stemHandles = new int[stemInfos.Length];
            var gains = new float[stemInfos.Length];
            var indexCounts = new int[stemInfos.Length];
            var indices = new List<int>();
            var panning = new List<float>();

            for (int i = 0; i < stemInfos.Length; i++)
            {
                var info = stemInfos[i];
                var channel = GetTargetChannel(info.Stem, created);
                if (channel == null)
                {
                    DisposeCreated(created);
                    return false;
                }

                stemHandles[i] = channel.Handle;
                gains[i] = 1f;
                if (info.Indices != null && info.Indices.Length > 0)
                {
                    indexCounts[i] = info.Indices.Length;
                    for (int j = 0; j < info.Indices.Length; j++)
                    {
                        indices.Add(info.Indices[j]);
                        bool hasPanning = info.Panning != null && info.Panning.Length > 2 * j + 1;
                        panning.Add(hasPanning ? info.Panning![2 * j] : 1f);
                        panning.Add(hasPanning ? info.Panning![2 * j + 1] : 1f);
                    }
                }
            }

            int added = WebAudioNative.WebAudio_MixerAddData(_handle, data, data.Length, stemHandles, gains,
                indexCounts, indices.ToArray(), panning.ToArray(), stemInfos.Length);
            if (added == 0)
            {
                DisposeCreated(created);
                return false;
            }

            _channels.AddRange(created);
            stream.Dispose();
            return true;
        }

        /// <summary>
        /// The channel a stem's audio goes into: its own new channel, or the background stem when no player plays
        /// it. The background is the song stem, created on demand.
        /// </summary>
        private WebStemChannel? GetTargetChannel(SongStem stem, List<WebStemChannel> created)
        {
            bool merge = stem != SongStem.Crowd && (_playedStems == null || !_playedStems.Contains(stem));
            if (stem == SongStem.Song || merge)
            {
                if (_background == null)
                {
                    _background = CreateChannel(SongStem.Song);
                    if (_background == null)
                    {
                        return null;
                    }

                    created.Add(_background);
                }

                if (stem != SongStem.Song)
                {
                    YargLogger.LogFormatDebug("Mixing unplayed stem {0} into the background stem", stem);
                }

                return _background;
            }

            var channel = CreateChannel(stem);
            if (channel != null)
            {
                created.Add(channel);
            }

            return channel;
        }

        private WebStemChannel? CreateChannel(SongStem stem)
        {
            double volume = WebStemChannel.InitialVolume(stem, _clampStemVolume);
            int handle = WebAudioNative.WebAudio_StemCreate(_handle, (int) stem, volume);
            if (handle == 0)
            {
                return null;
            }

            return new WebStemChannel(_manager, stem, _clampStemVolume, handle);
        }

        private void DisposeCreated(List<WebStemChannel> created)
        {
            foreach (var channel in created)
            {
                if (channel == _background)
                {
                    _background = null;
                }

                channel.Dispose();
            }

            created.Clear();
        }

        private static byte[] ReadAll(Stream stream)
        {
            if (stream is MemoryStream memory && memory.Position == 0)
            {
                return memory.ToArray();
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        protected override bool RemoveChannel_Internal(SongStem stemToRemove)
        {
            bool removed = false;
            for (int i = _channels.Count - 1; i >= 0; i--)
            {
                var channel = _channels[i];
                if (channel.Stem != stemToRemove)
                {
                    continue;
                }

                if (channel == _background)
                {
                    _background = null;
                }

                channel.Dispose();
                _channels.RemoveAt(i);
                removed = true;
            }

            return removed;
        }

        // A single output on the web: nothing to buffer, route or move
        protected override void SetBufferLength_Internal(int length)
        {
        }

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
        }

        protected override void DisposeManagedResources()
        {
            foreach (var channel in _channels)
            {
                channel.Dispose();
            }

            _channels.Clear();
            _background = null;
        }

        protected override void DisposeUnmanagedResources()
        {
            if (_handle != 0)
            {
                WebAudioNative.WebAudio_MixerFree(_handle);
                _handle = 0;
            }
        }
    }
}
