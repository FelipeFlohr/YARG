using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Helpers.Web;
using YARG.Input.Serialization;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Player;

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

        /// <summary>
        /// Saves the settings and profiles whenever the tab is hidden, closed or reloaded: the browser gives
        /// Unity no quit or disable callbacks then, and settings are otherwise saved only when their menu closes.
        /// </summary>
        public static void Initialize()
        {
            EnsureInstance();
            WebBridge.OnPageHide(OBJECT_NAME, nameof(FlushToStorage));
        }

        public static void Export()
        {
            // The files are written only on save, and Export is clicked from inside the settings menu
            SaveAll();

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

        private static void SaveAll()
        {
            SettingsManager.SaveSettings();
            PlayerContainer.SaveProfiles();
        }

        // Called by the browser through SendMessage.
        private void FlushToStorage()
        {
            SaveAll();
        }

        private static bool CanLoadBindings(string json)
        {
            try
            {
                // An object without a version would load as empty bindings and wipe the current ones
                if (JObject.Parse(json)["Version"] is not { Type: JTokenType.Integer })
                {
                    return false;
                }

                string path = Path.Combine(PathHelper.PersistentDataPath, "import-bindings.json");
                try
                {
                    File.WriteAllText(path, json);
                    return BindingSerialization.DeserializeBindings(path) != null;
                }
                finally
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                YargLogger.LogFormatWarning("Imported bindings are invalid: {0}", e.Message);
                return false;
            }
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
                out string bindingsJson, out _) ||
                (settingsJson != null && !SettingsManager.CanLoadSettings(settingsJson)) ||
                (profilesJson != null && !PlayerContainer.CanLoadProfiles(profilesJson)) ||
                (bindingsJson != null && !CanLoadBindings(bindingsJson)))
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
