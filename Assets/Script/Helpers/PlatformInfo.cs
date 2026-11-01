using System;
using System.Collections.Generic;
using YARG.Core.Logging;
using YARG.Core.Utility;
using YARG.Helpers.Web;

namespace YARG.Helpers
{
    /// <summary>
    /// Platform queries. This is the only place that tests the web platform symbol for logic.
    /// </summary>
    public static class PlatformInfo
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        public static bool IsWeb => true;
#else
        public static bool IsWeb => false;
#endif

        /// <summary>
        /// Number of microphones Unity sees. Unity's <c>Microphone</c> class does not exist on the web.
        /// </summary>
        public static int UnityMicrophoneCount
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return 0;
#else
                return UnityEngine.Microphone.devices.Length;
#endif
            }
        }

        /// <summary>
        /// Clears Unity's AssetBundle cache. Returns false if some items could not be removed.
        /// Unity's <c>Caching</c> class does not exist on the web, where the browser owns the cache.
        /// </summary>
        public static bool ClearAssetBundleCache()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return UnityEngine.Caching.ClearCache();
#endif
        }

        /// <summary>
        /// Gets the command line arguments. The first element is always the application itself.
        /// On the web, the arguments come from the page query string.
        /// </summary>
        public static string[] GetArgs()
        {
            if (!IsWeb)
            {
                return Environment.GetCommandLineArgs();
            }

            var parsed = QueryStringArgs.Parse(WebBridge.GetQueryString());
            var args = new List<string> { "YARG" };
            bool keep = false;
            foreach (string arg in parsed)
            {
                if (arg.StartsWith("-"))
                {
                    keep = IsAllowedWebArg(arg);
                    if (!keep)
                    {
                        YargLogger.LogFormatWarning("Ignoring the page argument {0} in this build", arg);
                    }
                }

                if (keep)
                {
                    args.Add(arg);
                }
            }
            return args.ToArray();
        }

        // Anyone can craft a link to the public page, so the release web build only takes arguments that cannot
        // change stored data. Test builds (YARG_WEB_TEST_HOOKS, see Editor.WebBuild) take every argument.
        private static readonly string[] RELEASE_WEB_ARGS = { "-lang", "-offline", "-relay" };

        private static bool IsAllowedWebArg(string arg)
        {
#if YARG_WEB_TEST_HOOKS
            return true;
#else
            return Array.IndexOf(RELEASE_WEB_ARGS, arg) >= 0;
#endif
        }
    }
}
