using System;
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
            var args = new string[parsed.Length + 1];
            args[0] = "YARG";
            Array.Copy(parsed, 0, args, 1, parsed.Length);
            return args;
        }
    }
}
