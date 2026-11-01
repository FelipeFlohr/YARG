#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace YARG.Audio.Web
{
    /// <summary>
    /// Calls into the web audio module (YargWebAudio.jslib and YargWebAudio.jspre). Handles are positive integers;
    /// 0 means failure. Outside of a web build every call fails, so nothing here is reachable on the desktop.
    /// </summary>
    internal static class WebAudioNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const string LIB = "__Internal";

        [DllImport(LIB)] public static extern int    WebAudio_Init();
        [DllImport(LIB)] public static extern int    WebAudio_State();
        [DllImport(LIB)] public static extern double WebAudio_Latency();
        [DllImport(LIB)] public static extern int    WebAudio_SampleRate();
        [DllImport(LIB)] public static extern void   WebAudio_SetMasterVolume(double volume);
        [DllImport(LIB)] public static extern void   WebAudio_Tick();

        [DllImport(LIB)] public static extern int    WebAudio_MixerCreate(double volume, double speed);
        [DllImport(LIB)] public static extern void   WebAudio_MixerFree(int mixer);
        [DllImport(LIB)] public static extern int    WebAudio_StemCreate(int mixer, int stemId, double volume);
        [DllImport(LIB)] public static extern void   WebAudio_StemFree(int stem);
        [DllImport(LIB)] public static extern void   WebAudio_StemSetVolume(int stem, double volume);

        [DllImport(LIB)]
        public static extern int WebAudio_MixerAddData(int mixer, byte[] data, int length, int[] stemHandles,
            float[] gains, int[] indexCounts, int[] indices, float[] panning, int targetCount);

        [DllImport(LIB)] public static extern int    WebAudio_MixerPlay(int mixer);
        [DllImport(LIB)] public static extern int    WebAudio_MixerPause(int mixer);
        [DllImport(LIB)] public static extern void   WebAudio_MixerSetPosition(int mixer, double position);
        [DllImport(LIB)] public static extern double WebAudio_MixerGetPosition(int mixer);
        [DllImport(LIB)] public static extern void   WebAudio_MixerSetSpeed(int mixer, double speed);
        [DllImport(LIB)] public static extern void   WebAudio_MixerSetVolume(int mixer, double volume);
        [DllImport(LIB)] public static extern void   WebAudio_MixerFade(int mixer, double target, double seconds);
        [DllImport(LIB)] public static extern int    WebAudio_MixerIsLoading(int mixer);
        [DllImport(LIB)] public static extern double WebAudio_MixerGetLength(int mixer);
        [DllImport(LIB)] public static extern string WebAudio_MixerGetError(int mixer);
        [DllImport(LIB)] public static extern int    WebAudio_MixerTakeEnded(int mixer);
        [DllImport(LIB)] public static extern int    WebAudio_MixerGetSamples(int mixer, float[] buffer, int count);
        [DllImport(LIB)] public static extern int    WebAudio_MixerGetFft(int mixer, float[] buffer, int count, int log2Size);
        [DllImport(LIB)] public static extern double WebAudio_MixerGetPeak(int mixer);

        [DllImport(LIB)]
        public static extern int WebAudio_OneShotCreate(int mixer, int sample, double[] times, int count, double lead);

        [DllImport(LIB)] public static extern void WebAudio_OneShotSetSchedule(int shot, double[] times, int count);
        [DllImport(LIB)] public static extern void WebAudio_OneShotSetEnabled(int shot, int enabled);
        [DllImport(LIB)] public static extern void WebAudio_OneShotSetVolume(int shot, double volume);
        [DllImport(LIB)] public static extern void WebAudio_OneShotFree(int shot);

        [DllImport(LIB)] public static extern int  WebAudio_SampleLoad(byte[] data, int length);
        [DllImport(LIB)] public static extern int  WebAudio_SamplePlay(int sample, double volume, int loop, int queued);
        [DllImport(LIB)] public static extern void WebAudio_SampleStop(int sample, double fade);
        [DllImport(LIB)] public static extern void WebAudio_SamplePause(int sample);
        [DllImport(LIB)] public static extern void WebAudio_SampleResume(int sample);
        [DllImport(LIB)] public static extern void WebAudio_SampleSetVolume(int sample, double volume);
        [DllImport(LIB)] public static extern int  WebAudio_SampleState(int sample);
        [DllImport(LIB)] public static extern void WebAudio_SampleFree(int sample);

        [DllImport(LIB)] public static extern void   WebAudio_MicRefreshDevices();
        [DllImport(LIB)] public static extern int    WebAudio_MicDeviceCount();
        [DllImport(LIB)] public static extern string WebAudio_MicDeviceName(int index);
        [DllImport(LIB)] public static extern string WebAudio_MicDeviceId(int index);
        [DllImport(LIB)] public static extern int    WebAudio_MicOpen(string deviceId);
        [DllImport(LIB)] public static extern void   WebAudio_MicClose(int mic);
        [DllImport(LIB)] public static extern int    WebAudio_MicState(int mic);
        [DllImport(LIB)] public static extern string WebAudio_MicError(int mic);
        [DllImport(LIB)] public static extern int    WebAudio_MicRead(int mic, float[] buffer, int count);
        [DllImport(LIB)] public static extern int    WebAudio_MicAvailable(int mic);
        [DllImport(LIB)] public static extern void   WebAudio_MicReset(int mic);
        [DllImport(LIB)] public static extern void   WebAudio_MicSetMonitoring(int mic, double volume);
        [DllImport(LIB)] public static extern void   WebAudio_MicReportPitch(int mic, double pitch, double db);

        [DllImport(LIB)] public static extern int WebAudio_DebugTakeRequest();
        [DllImport(LIB)] public static extern int WebAudio_DebugToneWavLength();
        [DllImport(LIB)] public static extern int WebAudio_DebugCopyToneWav(byte[] buffer, int length);
#else
        public static int    WebAudio_Init() => 0;
        public static int    WebAudio_State() => -1;
        public static double WebAudio_Latency() => 0;
        public static int    WebAudio_SampleRate() => 0;
        public static void   WebAudio_SetMasterVolume(double volume) { }
        public static void   WebAudio_Tick() { }

        public static int    WebAudio_MixerCreate(double volume, double speed) => 0;
        public static void   WebAudio_MixerFree(int mixer) { }
        public static int    WebAudio_StemCreate(int mixer, int stemId, double volume) => 0;
        public static void   WebAudio_StemFree(int stem) { }
        public static void   WebAudio_StemSetVolume(int stem, double volume) { }

        public static int WebAudio_MixerAddData(int mixer, byte[] data, int length, int[] stemHandles,
            float[] gains, int[] indexCounts, int[] indices, float[] panning, int targetCount) => 0;

        public static int    WebAudio_MixerPlay(int mixer) => -1;
        public static int    WebAudio_MixerPause(int mixer) => -1;
        public static void   WebAudio_MixerSetPosition(int mixer, double position) { }
        public static double WebAudio_MixerGetPosition(int mixer) => 0;
        public static void   WebAudio_MixerSetSpeed(int mixer, double speed) { }
        public static void   WebAudio_MixerSetVolume(int mixer, double volume) { }
        public static void   WebAudio_MixerFade(int mixer, double target, double seconds) { }
        public static int    WebAudio_MixerIsLoading(int mixer) => 0;
        public static double WebAudio_MixerGetLength(int mixer) => 0;
        public static string WebAudio_MixerGetError(int mixer) => "Web audio is only available in the web build";
        public static int    WebAudio_MixerTakeEnded(int mixer) => 0;
        public static int    WebAudio_MixerGetSamples(int mixer, float[] buffer, int count) => -1;
        public static int    WebAudio_MixerGetFft(int mixer, float[] buffer, int count, int log2Size) => -1;
        public static double WebAudio_MixerGetPeak(int mixer) => 0;

        public static int WebAudio_OneShotCreate(int mixer, int sample, double[] times, int count, double lead) => 0;

        public static void WebAudio_OneShotSetSchedule(int shot, double[] times, int count) { }
        public static void WebAudio_OneShotSetEnabled(int shot, int enabled) { }
        public static void WebAudio_OneShotSetVolume(int shot, double volume) { }
        public static void WebAudio_OneShotFree(int shot) { }

        public static int  WebAudio_SampleLoad(byte[] data, int length) => 0;
        public static int  WebAudio_SamplePlay(int sample, double volume, int loop, int queued) => 0;
        public static void WebAudio_SampleStop(int sample, double fade) { }
        public static void WebAudio_SamplePause(int sample) { }
        public static void WebAudio_SampleResume(int sample) { }
        public static void WebAudio_SampleSetVolume(int sample, double volume) { }
        public static int  WebAudio_SampleState(int sample) => 0;
        public static void WebAudio_SampleFree(int sample) { }

        public static void   WebAudio_MicRefreshDevices() { }
        public static int    WebAudio_MicDeviceCount() => 0;
        public static string WebAudio_MicDeviceName(int index) => null;
        public static string WebAudio_MicDeviceId(int index) => null;
        public static int    WebAudio_MicOpen(string deviceId) => 0;
        public static void   WebAudio_MicClose(int mic) { }
        public static int    WebAudio_MicState(int mic) => -1;
        public static string WebAudio_MicError(int mic) => null;
        public static int    WebAudio_MicRead(int mic, float[] buffer, int count) => -1;
        public static int    WebAudio_MicAvailable(int mic) => -1;
        public static void   WebAudio_MicReset(int mic) { }
        public static void   WebAudio_MicSetMonitoring(int mic, double volume) { }
        public static void   WebAudio_MicReportPitch(int mic, double pitch, double db) { }

        public static int WebAudio_DebugTakeRequest() => 0;
        public static int WebAudio_DebugToneWavLength() => 0;
        public static int WebAudio_DebugCopyToneWav(byte[] buffer, int length) => 0;
#endif
    }
}
