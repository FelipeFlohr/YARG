using System;
using UnityEngine;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Helpers.Web;
using YARG.Localization;
using YARG.Menu.Persistent;

namespace YARG.Settings.Web
{
    /// <summary>
    /// Exports the settings and profiles to a downloaded file and imports them back (web build).
    /// The GameObject is named so the browser can call back into <see cref="OnImportFile"/>.
    /// </summary>
    public sealed class WebConfigTransfer : MonoBehaviour
    {
        private const string OBJECT_NAME = "WebConfigTransfer";

        private static WebConfigTransfer _instance;

        public static void Export()
        {
            string bundle = ConfigBundle.Create(
                WebConfigStore.ReadFile(WebConfigFile.Settings),
                WebConfigStore.ReadFile(WebConfigFile.Profiles),
                WebConfigStore.ReadFile(WebConfigFile.Bindings));
            WebBridge.DownloadText("yarg-config.json", bundle);
        }

        /// <summary>
        /// Must be called synchronously from the click handler so the browser still
        /// considers the file picker as triggered by a user gesture.
        /// </summary>
        public static void Import()
        {
            EnsureInstance();
            WebBridge.PickTextFile(".json", OBJECT_NAME, nameof(OnImportFile));
        }

        private static void EnsureInstance()
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject(OBJECT_NAME);
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<WebConfigTransfer>();
        }

        // Called by the browser through SendMessage.
        private void OnImportFile(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Validate everything before touching any file
            if (!ConfigBundle.TryParse(text, out string settingsJson, out string profilesJson,
                out string bindingsJson, out _))
            {
                ToastManager.ToastError(Localize.Key("Web.ImportInvalid"));
                return;
            }

            var parts = new (WebConfigFile File, string Json)[]
            {
                (WebConfigFile.Settings, settingsJson),
                (WebConfigFile.Profiles, profilesJson),
                (WebConfigFile.Bindings, bindingsJson),
            };

            try
            {
                foreach (var (file, json) in parts)
                {
                    if (json != null)
                    {
                        WebConfigStore.WriteFile(file, json);
                    }
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to write the imported settings!");
                ToastManager.ToastError(Localize.Key("Web.ImportInvalid"));
                return;
            }

            bool persisted = true;
            foreach (var (file, json) in parts)
            {
                if (json != null)
                {
                    persisted &= WebConfigStore.Persist(file);
                }
            }

            if (!persisted)
            {
                ToastManager.ToastError(Localize.Key("Web.ConfigNotSaved"));
                return;
            }

            WebBridge.Reload();
        }
    }
}
