#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace YARG.Song.Web
{
    /// <summary>
    /// Calls into the browser (see YargWebSong.jslib). Outside of a web build every method is a stub.
    /// </summary>
    internal static class WebSongBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "YargWebSong_Init")]
        private static extern void InitNative(string gameObjectName);

        [DllImport("__Internal", EntryPoint = "YargWebSong_Pick")]
        private static extern void PickNative(int folder);

        [DllImport("__Internal", EntryPoint = "YargWebSong_SetDropEnabled")]
        private static extern void SetDropEnabledNative(int enabled);

        public static void Init(string gameObjectName) => InitNative(gameObjectName);

        public static void Pick(bool folder) => PickNative(folder ? 1 : 0);

        public static void SetDropEnabled(bool enabled) => SetDropEnabledNative(enabled ? 1 : 0);
#else
        public static void Init(string gameObjectName)
        {
        }

        public static void Pick(bool folder)
        {
        }

        public static void SetDropEnabled(bool enabled)
        {
        }
#endif
    }
}
