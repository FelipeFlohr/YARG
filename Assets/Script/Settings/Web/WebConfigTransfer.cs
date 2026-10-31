using UnityEngine;
using YARG.Core.IO;
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
                WebConfigStore.ReadFile(WebConfigFile.Profiles));
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

            if (!ConfigBundle.TryParse(text, out string settingsJson, out string profilesJson, out _))
            {
                ToastManager.ToastError(Localize.Key("Web.ImportInvalid"));
                return;
            }

            if (settingsJson != null)
            {
                WebConfigStore.WriteFile(WebConfigFile.Settings, settingsJson);
                WebConfigStore.Persist(WebConfigFile.Settings);
            }

            if (profilesJson != null)
            {
                WebConfigStore.WriteFile(WebConfigFile.Profiles, profilesJson);
                WebConfigStore.Persist(WebConfigFile.Profiles);
            }

            WebBridge.Reload();
        }
    }
}
