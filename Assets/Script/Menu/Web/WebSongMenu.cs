using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Player;
using YARG.Song.Web;

namespace YARG.Menu.Web
{
    /// <summary>
    /// The web build's song screen, in place of the music library: the player loads one song from disk (picker or
    /// drag and drop), sees its cover, name, artist and parts, and plays it. After the score screen it offers
    /// "play again" and "load another". Built in code because the web port adds no prefabs.
    /// </summary>
    public sealed class WebSongMenu : MonoBehaviour
    {
        private enum Mode
        {
            Empty,
            Song,
            Result,
        }

        private static readonly Color BackgroundColor = UiBuilder.BackgroundColor;
        private static readonly Color PanelColor = UiBuilder.PanelColor;
        private static readonly Color ButtonColor = UiBuilder.ButtonColor;
        private static readonly Color PrimaryButtonColor = UiBuilder.PrimaryButtonColor;
        private static readonly Color TextColor = UiBuilder.TextColor;
        private static readonly Color DimTextColor = UiBuilder.DimTextColor;
        private static readonly Color ErrorColor = UiBuilder.ErrorColor;

        private static WebSongMenu _instance;

        /// <summary>Whether the screen was opened from the practice menu.</summary>
        private static bool _practice;

        private UiBuilder _ui;

        private TextMeshProUGUI _header;
        private GameObject _emptyPanel;
        private GameObject _songPanel;
        private RawImage _cover;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _artist;
        private TextMeshProUGUI _details;
        private TextMeshProUGUI _parts;
        private TextMeshProUGUI _status;

        private Button _playButton;
        private TextMeshProUGUI _playLabel;
        private TextMeshProUGUI _loadFileLabel;

        private Mode _mode;
        private bool _picking;
        private CancellationTokenSource _pickCancel;
        private CancellationTokenSource _coverCancel;
        private bool _schemePushed;

        /// <summary>
        /// Adds the (inactive) screen under the menu manager so it can be pushed as <see cref="MenuManager.Menu.WebSong"/>.
        /// </summary>
        public static MenuObject Create(Transform parent)
        {
            var go = UiBuilder.CreateCanvasObject("WebSongMenu", parent);

            var menuObject = go.AddComponent<MenuObject>();
            menuObject.SetUp(MenuManager.Menu.WebSong, true);

            var menu = go.AddComponent<WebSongMenu>();
            menu._ui = new UiBuilder(parent.GetComponentInChildren<TMP_Text>(true)?.font);
            menu.Build((RectTransform) go.transform);
            _instance = menu;

            // Static handlers, so the screen opens on a drop even before it was ever shown
            WebSongLoader.AcceptsDrops = AcceptsDrops;
            WebSongLoader.SongDropped -= OnSongDropped;
            WebSongLoader.SongDropped += OnSongDropped;
            WebSongLoader.SongDropFailed -= OnSongDropFailed;
            WebSongLoader.SongDropFailed += OnSongDropFailed;
            return menuObject;
        }

        /// <summary>Opens the screen from the main menu (quickplay) or the game modes menu (practice).</summary>
        public static void Open(bool practice)
        {
            _practice = practice;
            MenuManager.Instance.PushMenu(MenuManager.Menu.WebSong);
        }

        /// <summary>Starts <paramref name="song"/> with the players' current part and difficulty choices.</summary>
        public static void StartGameplay(SongEntry song)
        {
            SetCurrentSong(song);
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private static void SetCurrentSong(SongEntry song)
        {
            GlobalVariables.State.CurrentSong = song;
            // DifficultySelectMenu reads the song list even for a single song
            GlobalVariables.State.ShowSongs.Clear();
            GlobalVariables.State.ShowSongs.Add(song);
            GlobalVariables.State.PlayingAShow = false;
            GlobalVariables.State.ShowIndex = 0;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
            _coverCancel?.Cancel();
        }

        private void OnEnable()
        {
            GlobalVariables.State.IsPractice = _practice;
            GlobalVariables.State.CurrentReplay = null;
            GlobalVariables.State.PlayingWithReplay = false;

            _header.text = Localize.Key("Menu.Main.Options", _practice ? "Practice" : "Quickplay");

            // Coming back from the score screen of this same song. The score screen resets GlobalVariables.State
            // when it closes, but leaves the song it showed in MusicLibraryMenu.CurrentlyPlaying.
            var song = WebSongLoader.Current;
            bool afterResult = song != null && ReferenceEquals(MusicLibraryMenu.CurrentlyPlaying, song);
            MusicLibraryMenu.CurrentlyPlaying = null;
            SetStatus(null, false);
            Show(song, afterResult ? Mode.Result : null);
        }

        private void OnDisable()
        {
            _pickCancel?.Cancel();
            _pickCancel = null;
            _picking = false;

            if (_schemePushed)
            {
                Navigator.Instance?.PopScheme();
                _schemePushed = false;
            }
        }

        private void Show(SongEntry song, Mode? mode = null)
        {
            _mode = mode ?? (song == null ? Mode.Empty : Mode.Song);

            _emptyPanel.SetActive(song == null);
            _songPanel.SetActive(song != null);
            if (song != null)
            {
                FillSong(song);
            }

            _playButton.gameObject.SetActive(_mode != Mode.Empty);
            _playLabel.text = Localize.Key(_mode == Mode.Result ? "WebSong.PlayAgain" : "WebSong.Play");
            _loadFileLabel.text = Localize.Key(_mode == Mode.Empty ? "WebSong.LoadFile" : "WebSong.LoadAnother");

            UpdateScheme();
            YargLogger.LogFormatInfo("[WebSong] Screen: {0}", _mode);
        }

        private void FillSong(SongEntry song)
        {
            _name.text = song.Name.Original;
            _artist.text = song.Artist.Original;

            var details = new List<string>();
            if (!string.IsNullOrEmpty(song.Album.Original))
            {
                details.Add(song.Album.Original);
            }
            if (!string.IsNullOrEmpty(song.Charter.Original))
            {
                details.Add(Localize.KeyFormat("WebSong.Charter", song.Charter.Original));
            }
            if (song.SongLengthMilliseconds > 0)
            {
                long seconds = song.SongLengthMilliseconds / 1000;
                details.Add($"{seconds / 60}:{seconds % 60:00}");
            }
            _details.text = string.Join("  ·  ", details);

            var lines = new List<string>();
            foreach (var instrument in EnumExtensions<Instrument>.Values)
            {
                // The band "part" is only the song's overall intensity, not something to play
                if (instrument == Instrument.Band || !song.HasInstrument(instrument))
                {
                    continue;
                }

                // Vocals have no per-difficulty charts (the singer picks how strict the pitch is), so their
                // difficulty flags say nothing useful
                var part = song[instrument];
                var difficulties = new List<string>();
                foreach (var difficulty in EnumExtensions<Difficulty>.Values)
                {
                    if (instrument.ToNativeGameMode() != GameMode.Vocals && part[difficulty])
                    {
                        difficulties.Add(difficulty.ToLocalizedName());
                    }
                }

                string line = $"<b>{instrument.ToLocalizedName()}</b>";
                if (difficulties.Count > 0)
                {
                    line += $"  <color=#A6B0C7>{string.Join(", ", difficulties)}</color>";
                }
                lines.Add(line);
            }
            _parts.text = lines.Count > 0 ? string.Join("\n", lines) : Localize.Key("WebSong.NoParts");

            _coverCancel?.Cancel();
            _coverCancel = new CancellationTokenSource();
            _cover.LoadAlbumCover(song, _coverCancel.Token);
        }

        private void SetStatus(string text, bool error)
        {
            _status.text = text ?? string.Empty;
            _status.color = error ? ErrorColor : DimTextColor;
        }

        private void UpdateScheme()
        {
            if (!isActiveAndEnabled || Navigator.Instance == null)
            {
                return;
            }

            if (_schemePushed)
            {
                Navigator.Instance.PopScheme();
            }

            var entries = new List<NavigationScheme.Entry>();
            if (_mode == Mode.Empty)
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Green, "WebSong.LoadFile", LoadFile));
            }
            else
            {
                entries.Add(new NavigationScheme.Entry(MenuAction.Green,
                    _mode == Mode.Result ? "WebSong.PlayAgain" : "WebSong.Play", Play));
                entries.Add(new NavigationScheme.Entry(MenuAction.Yellow, "WebSong.LoadAnother", LoadFile));
            }
            entries.Add(new NavigationScheme.Entry(MenuAction.Blue, "WebSong.LoadFolder", LoadFolder));
            entries.Add(new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back));

            Navigator.Instance.PushSchemeImmediate(new NavigationScheme(entries, false));
            _schemePushed = true;
        }

        private void Play()
        {
            var song = WebSongLoader.Current;
            if (song == null || _picking)
            {
                return;
            }

            if (PlayerContainer.Players.Count <= 0)
            {
                SetStatus(Localize.Key("WebSong.NoPlayers"), true);
                return;
            }

            if (_mode == Mode.Result)
            {
                // Same players, parts and speed as the run that just ended
                StartGameplay(song);
                return;
            }

            SetCurrentSong(song);
            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
        }

        // Both are called straight from the click or key press, so the browser allows the picker
        private void LoadFile() => Pick(false).Forget();

        private void LoadFolder() => Pick(true).Forget();

        private async UniTaskVoid Pick(bool folder)
        {
            if (_picking)
            {
                return;
            }

            _picking = true;
            _pickCancel = new CancellationTokenSource();
            var token = _pickCancel.Token;
            SetStatus(Localize.Key("WebSong.Picking"), false);

            try
            {
                var song = folder
                    ? await WebSongLoader.PickFolderAsync(token)
                    : await WebSongLoader.PickAsync(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SetStatus(null, false);
                if (song != null)
                {
                    Show(song);
                }
            }
            catch (WebSongLoadException e)
            {
                if (!token.IsCancellationRequested)
                {
                    SetStatus(e.Message, true);
                }
            }
            finally
            {
                _picking = false;
            }
        }

        private void Back()
        {
            MenuManager.Instance.PopMenu();
        }

        /// <summary>
        /// Drops are taken on this screen and on the menus that open it, never while another menu is using the
        /// current song (difficulty select would start a song whose files were just deleted).
        /// </summary>
        private static bool AcceptsDrops()
        {
            var manager = MenuManager.Instance;
            return manager != null && manager.CurrentMenu is MenuManager.Menu.WebSong or MenuManager.Menu.MainMenu
                or MenuManager.Menu.GameModes;
        }

        private static void OnSongDropped(SongEntry song)
        {
            var manager = MenuManager.Instance;
            if (_instance != null && _instance.isActiveAndEnabled)
            {
                _instance.SetStatus(null, false);
                _instance.Show(song);
            }
            else if (manager != null)
            {
                Open(manager.CurrentMenu == MenuManager.Menu.GameModes);
            }
        }

        private static void OnSongDropFailed(WebSongLoadException e)
        {
            if (_instance != null && _instance.isActiveAndEnabled)
            {
                _instance.SetStatus(e.Message, true);
            }
            else
            {
                ToastManager.ToastError(e.Message);
            }
        }

        #region Layout

        private void Build(RectTransform root)
        {
            var background = AddImage(CreateRect("Background", root, Vector2.zero, Vector2.one), BackgroundColor);
            background.raycastTarget = true;

            _header = CreateText("Header", root, new Vector2(0.05f, 0.88f), new Vector2(0.95f, 0.96f), 56,
                FontStyles.Bold, TextAlignmentOptions.Left, TextColor);

            // Empty state: what to do
            _emptyPanel = CreateRect("Empty", root, new Vector2(0.15f, 0.32f), new Vector2(0.85f, 0.82f)).gameObject;
            AddImage((RectTransform) _emptyPanel.transform, PanelColor);
            var prompt = CreateText("Prompt", _emptyPanel.transform, new Vector2(0.05f, 0.5f), new Vector2(0.95f, 0.85f),
                64, FontStyles.Bold, TextAlignmentOptions.Center, TextColor);
            prompt.text = Localize.Key("WebSong.LoadFile");
            var hint = CreateText("Hint", _emptyPanel.transform, new Vector2(0.05f, 0.15f), new Vector2(0.95f, 0.48f),
                34, FontStyles.Normal, TextAlignmentOptions.Center, DimTextColor);
            hint.text = Localize.Key("WebSong.Hint");

            // Song state: cover on the left, info on the right
            _songPanel = CreateRect("Song", root, new Vector2(0.05f, 0.3f), new Vector2(0.95f, 0.85f)).gameObject;
            var songRect = (RectTransform) _songPanel.transform;

            var coverFrame = CreateRect("CoverFrame", songRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            coverFrame.sizeDelta = new Vector2(560, 560);
            coverFrame.anchoredPosition = new Vector2(280, 0);
            AddImage(coverFrame, PanelColor);
            var coverRect = CreateRect("Cover", coverFrame, Vector2.zero, Vector2.one);
            _cover = coverRect.gameObject.AddComponent<RawImage>();
            _cover.color = Color.clear;
            _cover.raycastTarget = false;

            var info = CreateRect("Info", songRect, new Vector2(0f, 0f), new Vector2(1f, 1f));
            info.offsetMin = new Vector2(620, 0);
            _name = CreateText("Name", info, new Vector2(0f, 0.8f), new Vector2(1f, 1f), 72, FontStyles.Bold,
                TextAlignmentOptions.BottomLeft, TextColor);
            _artist = CreateText("Artist", info, new Vector2(0f, 0.68f), new Vector2(1f, 0.8f), 44, FontStyles.Normal,
                TextAlignmentOptions.TopLeft, TextColor);
            _details = CreateText("Details", info, new Vector2(0f, 0.6f), new Vector2(1f, 0.68f), 30,
                FontStyles.Normal, TextAlignmentOptions.TopLeft, DimTextColor);
            _parts = CreateText("Parts", info, new Vector2(0f, 0f), new Vector2(1f, 0.57f), 32, FontStyles.Normal,
                TextAlignmentOptions.TopLeft, TextColor);
            _parts.enableAutoSizing = true;
            _parts.fontSizeMin = 18;
            _parts.fontSizeMax = 32;

            _status = CreateText("Status", root, new Vector2(0.05f, 0.215f), new Vector2(0.95f, 0.28f), 34,
                FontStyles.Bold, TextAlignmentOptions.Center, DimTextColor);

            // Buttons along the bottom, above the menus' hint bar
            var buttons = CreateRect("Buttons", root, new Vector2(0.05f, 0.11f), new Vector2(0.95f, 0.2f));
            var layout = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 32;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            _playButton = CreateButton("Play", buttons, PrimaryButtonColor, Play, out _playLabel);
            CreateButton("LoadFile", buttons, ButtonColor, LoadFile, out _loadFileLabel);
            CreateButton("LoadFolder", buttons, ButtonColor, LoadFolder, out var folderLabel);
            folderLabel.text = Localize.Key("WebSong.LoadFolder");
            CreateButton("Back", buttons, ButtonColor, Back, out var backLabel);
            backLabel.text = Localize.Key("Menu.Common.Back");
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax) =>
            UiBuilder.Rect(name, parent, anchorMin, anchorMax);

        private static Image AddImage(RectTransform rect, Color color) => UiBuilder.Image(rect, color);

        private TextMeshProUGUI CreateText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            float size, FontStyles style, TextAlignmentOptions alignment, Color color) =>
            _ui.Text(name, parent, anchorMin, anchorMax, size, style, alignment, color);

        private Button CreateButton(string name, Transform parent, Color color, UnityAction onClick,
            out TextMeshProUGUI label) =>
            _ui.Button(name, parent, color, onClick, out label);

        #endregion
    }
}
