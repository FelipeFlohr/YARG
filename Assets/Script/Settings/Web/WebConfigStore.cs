using System.IO;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Helpers.Web;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Player;

namespace YARG.Settings.Web
{
    public enum WebConfigFile
    {
        Settings,
        Profiles,
        Bindings,
    }

    /// <summary>
    /// Mirrors the settings and profiles files (which live in the in-memory filesystem of the web build)
    /// to the browser's localStorage so they survive page reloads.
    /// </summary>
    public static class WebConfigStore
    {
        private const string SCHEMA_KEY = "yarg.schemaVersion";
        private const string SCHEMA_VERSION = "1";

        private static bool _warnedStorageBlocked;

        public static void Restore()
        {
            if (!PlatformInfo.IsWeb)
            {
                return;
            }

            string schema = WebBridge.StorageGet(SCHEMA_KEY);
            if (schema != null && schema != SCHEMA_VERSION)
            {
                YargLogger.LogFormatWarning("Unknown stored config schema version `{0}`; not restoring it.", schema);
                return;
            }

            foreach (var file in new[] { WebConfigFile.Settings, WebConfigFile.Profiles, WebConfigFile.Bindings })
            {
                string text = WebBridge.StorageGet(GetKey(file));
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                try
                {
                    WriteFile(file, text);
                }
                catch (System.Exception e)
                {
                    YargLogger.LogException(e, $"Failed to restore {file} from browser storage!");
                }
            }
        }

        /// <summary>Returns false only when the browser refused to store the data.</summary>
        public static bool Persist(WebConfigFile file)
        {
            if (!PlatformInfo.IsWeb)
            {
                return true;
            }

            string path = GetPath(file);
            if (!File.Exists(path))
            {
                YargLogger.LogWarning($"Cannot persist {file}: `{path}` does not exist.");
                return true;
            }

            bool success;
            try
            {
                success = WebBridge.StorageSet(GetKey(file), File.ReadAllText(path)) &&
                    WebBridge.StorageSet(SCHEMA_KEY, SCHEMA_VERSION);
            }
            catch (System.Exception e)
            {
                YargLogger.LogException(e, $"Failed to persist {file} to browser storage!");
                success = false;
            }

            if (!success && !_warnedStorageBlocked)
            {
                _warnedStorageBlocked = true;
                ToastManager.ToastWarning(() => Localize.Key("Web.ConfigNotSaved"));
            }

            return success;
        }

        public static string ReadFile(WebConfigFile file)
        {
            string path = GetPath(file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public static void WriteFile(WebConfigFile file, string text)
        {
            string path = GetPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static string GetKey(WebConfigFile file) => file switch
        {
            WebConfigFile.Settings => "yarg.settings",
            WebConfigFile.Profiles => "yarg.profiles",
            _ => "yarg.bindings",
        };

        private static string GetPath(WebConfigFile file) => file switch
        {
            WebConfigFile.Settings => SettingsManager.SettingsFilePath,
            WebConfigFile.Profiles => PlayerContainer.ProfilesFilePath,
            _ => BindingsContainer.BindingsFilePath,
        };
    }
}
