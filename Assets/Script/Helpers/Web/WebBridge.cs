#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace YARG.Helpers.Web
{
    /// <summary>
    /// Calls into the browser (see YargWeb.jslib). Outside of a web build every method is a stub.
    /// </summary>
    internal static class WebBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "YargWeb_GetQueryString")]
        private static extern string GetQueryStringNative();

        [DllImport("__Internal", EntryPoint = "YargWeb_StorageGet")]
        private static extern string StorageGetNative(string key);

        [DllImport("__Internal", EntryPoint = "YargWeb_StorageSet")]
        private static extern int StorageSetNative(string key, string value);

        [DllImport("__Internal", EntryPoint = "YargWeb_DownloadText")]
        private static extern void DownloadTextNative(string fileName, string text);

        [DllImport("__Internal", EntryPoint = "YargWeb_PickTextFile")]
        private static extern void PickTextFileNative(string accept, string gameObjectName, string callbackMethod);

        [DllImport("__Internal", EntryPoint = "YargWeb_OnPageHide")]
        private static extern void OnPageHideNative(string gameObjectName, string callbackMethod);

        [DllImport("__Internal", EntryPoint = "YargWeb_Reload")]
        private static extern void ReloadNative();

        public static string GetQueryString() => GetQueryStringNative();

        public static string StorageGet(string key) => StorageGetNative(key);

        public static bool StorageSet(string key, string value) => StorageSetNative(key, value) != 0;

        public static void DownloadText(string fileName, string text) => DownloadTextNative(fileName, text);

        public static void PickTextFile(string accept, string gameObjectName, string callbackMethod)
            => PickTextFileNative(accept, gameObjectName, callbackMethod);

        /// <summary>Calls back (SendMessage) when the tab is hidden, closed or reloaded.</summary>
        public static void OnPageHide(string gameObjectName, string callbackMethod)
            => OnPageHideNative(gameObjectName, callbackMethod);

        public static void Reload() => ReloadNative();
#else
        public static string GetQueryString() => null;

        public static string StorageGet(string key) => null;

        public static bool StorageSet(string key, string value) => false;

        public static void DownloadText(string fileName, string text)
        {
        }

        public static void PickTextFile(string accept, string gameObjectName, string callbackMethod)
        {
        }

        public static void OnPageHide(string gameObjectName, string callbackMethod)
        {
        }

        public static void Reload()
        {
        }
#endif
    }
}
