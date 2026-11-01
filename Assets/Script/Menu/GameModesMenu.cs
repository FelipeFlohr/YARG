using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Helpers;
using YARG.Menu.Career;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Web;

namespace YARG.Menu
{
    public class GameModesMenu : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _versionText;

        private void Start()
        {
            _versionText.text = GlobalVariables.Instance.CurrentVersion;
        }

        private void OnEnable()
        {
            // Set navigation scheme
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", () => MenuManager.Instance.PopMenu())
            }, true));
        }

        public void CareerList()
        {
            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.CareerList, false);
            menu.gameObject.SetActive(true);
        }

        public void Practice()
        {
            // The web build has no library: the player loads a song from disk
            if (PlatformInfo.IsWeb)
            {
                WebSongMenu.Open(true);
                return;
            }

            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);

            MusicLibraryMenu.LibraryMode = MusicLibraryMode.Practice;

            menu.gameObject.SetActive(true);
        }

        public void Replays()
        {
            MenuManager.Instance.PushMenu(MenuManager.Menu.History);
        }

        public void Back()
        {
            MenuManager.Instance.PopMenu();
        }

        private void OnDisable()
        {
            Navigator.Instance?.PopScheme();
        }
    }
}