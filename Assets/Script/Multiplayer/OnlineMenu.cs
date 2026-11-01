using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Web;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer
{
    /// <summary>
    /// The online screen: the relay's rooms with a create form (web), and the lobby of the current session (web and
    /// desktop) with the song, the band and the local player's part. Built in code like the web song screen, so it
    /// adds no prefab.
    /// </summary>
    public sealed class OnlineMenu : MonoBehaviour
    {
        private enum View
        {
            Rooms,
            Lobby,
        }

        private const int   ROOM_ROWS             = 5;
        private const float ROOM_REFRESH_INTERVAL = 5f;
        private const float LOBBY_REFRESH_INTERVAL = 0.25f;

        private static readonly Color SelectedRowColor = new(0.15f, 0.32f, 0.22f, 1f);
        private static readonly Color ReadyColor = new(0.42f, 0.89f, 0.61f, 1f);
        private static readonly Color ReadyPillColor = new(0.09f, 0.25f, 0.16f, 1f);
        private static readonly Color WarningPillColor = new(0.29f, 0.21f, 0.09f, 1f);
        private static readonly Color ErrorPillColor = new(0.3f, 0.12f, 0.13f, 1f);
        private static readonly Color DimPillColor = new(0.18f, 0.2f, 0.27f, 1f);

        private static OnlineMenu _instance;
        private static View _view;
        private static bool _dirty;

        private UiBuilder _ui;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _subtitle;

        // Rooms
        private GameObject _roomsView;
        private TextMeshProUGUI _roomCount;
        private TextMeshProUGUI _roomsEmpty;
        private readonly List<RoomRow> _roomRows = new();
        private GameObject _createForm;
        private TMP_InputField _roomName;
        private TMP_InputField _roomPassword;
        private TextMeshProUGUI _maxPlayersLabel;
        private TextMeshProUGUI _createStatus;
        private GameObject _joinForm;
        private TextMeshProUGUI _joinTitle;
        private TextMeshProUGUI _joinDetails;
        private TMP_InputField _joinPassword;
        private TextMeshProUGUI _joinStatus;

        // Lobby
        private GameObject _lobbyView;
        private RawImage _cover;
        private Image _coverFrame;
        private TextMeshProUGUI _songName;
        private TextMeshProUGUI _songArtist;
        private TextMeshProUGUI _songStatus;
        private GameObject _progressBar;
        private RectTransform _progressFill;
        private Button _chooseSong;
        private TextMeshProUGUI _chooseSongLabel;
        private TextMeshProUGUI _guestNote;
        private readonly List<PlayerRow> _playerRows = new();
        private Button _partButton;
        private Image _partButtonImage;
        private TextMeshProUGUI _partLabel;
        private TextMeshProUGUI _hint;
        private Button _startButton;

        private IReadOnlyList<RelayRoom> _rooms = new List<RelayRoom>();
        private RelayRoom _selectedRoom;
        private int _maxPlayers = 4;
        // A create or join waiting on the relay; the room list refreshes on its own flag, so a slow relay
        // doesn't swallow the buttons
        private bool _busy;
        private bool _refreshing;
        private float _lastRoomRefresh = float.NegativeInfinity;
        private float _lastLobbyRefresh = float.NegativeInfinity;
        private SongEntry _coverSong;
        private SongOffer _readyOffer;
        private CancellationTokenSource _coverCancel;
        private bool _schemePushed;

        private sealed class RoomRow
        {
            public GameObject      Root;
            public Image           Background;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Count;
            public RelayRoom       Room;
        }

        private sealed class PlayerRow
        {
            public GameObject      Root;
            public Image           Background;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Part;
            public GameObject      Pill;
            public Image           PillBackground;
            public TextMeshProUGUI PillText;
        }

        /// <summary>Whether the lobby is the screen on top right now.</summary>
        public static bool IsLobbyShowing =>
            _instance != null && _instance.isActiveAndEnabled && _view == View.Lobby;

        /// <summary>
        /// Adds the (inactive) screen under the menu manager so it can be pushed as <see cref="MenuManager.Menu.Online"/>.
        /// </summary>
        public static MenuObject Create(Transform parent)
        {
            var go = UiBuilder.CreateCanvasObject("OnlineMenu", parent);
            var menuObject = go.AddComponent<MenuObject>();
            menuObject.SetUp(MenuManager.Menu.Online, true);

            var menu = go.AddComponent<OnlineMenu>();
            menu._ui = new UiBuilder(parent.GetComponentInChildren<TMP_Text>(true)?.font);
            menu.Build((RectTransform) go.transform);
            _instance = menu;
            return menuObject;
        }

        /// <summary>The relay's rooms (web only).</summary>
        public static void ShowRooms()
        {
            _view = View.Rooms;
            Show();
        }

        /// <summary>The current session's lobby; goes back to it if another menu was opened over it.</summary>
        public static void ShowLobby()
        {
            _view = View.Lobby;
            _dirty = true;
            Show();
        }

        private static void Show()
        {
            var manager = MenuManager.Instance;
            if (manager == null)
            {
                return;
            }

            if (manager.IsOpen(MenuManager.Menu.Online))
            {
                manager.PopTo(MenuManager.Menu.Online);
                if (_instance != null)
                {
                    _instance.Refresh();
                }
            }
            else
            {
                manager.PushMenu(MenuManager.Menu.Online);
            }
        }

        /// <summary>Leaves the screen, e.g. when the session ended.</summary>
        public static void Close()
        {
            var manager = MenuManager.Instance;
            if (manager == null || !manager.IsOpen(MenuManager.Menu.Online))
            {
                return;
            }

            manager.PopTo(MenuManager.Menu.Online);
            manager.PopMenu();
        }

        public static void MarkDirty()
        {
            _dirty = true;
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
            Refresh();
        }

        private void OnDisable()
        {
            if (_schemePushed)
            {
                Navigator.Instance?.PopScheme();
                _schemePushed = false;
            }
        }

        private void Refresh()
        {
            _roomsView.SetActive(_view == View.Rooms);
            _lobbyView.SetActive(_view == View.Lobby);

            if (_view == View.Rooms)
            {
                _title.text = Localize.Key("Menu.Multiplayer.Web.Rooms");
                var player = MultiplayerManager.Instance.LocalPlayer;
                _subtitle.text = player == null
                    ? string.Empty
                    : Localize.KeyFormat("Menu.Multiplayer.Online.PlayingAs", player.Profile.Name,
                        player.Profile.CurrentInstrument.ToLocalizedName());
                if (_selectedRoom == null)
                {
                    ShowCreateForm();
                }
                RefreshRooms().Forget();
            }
            else
            {
                RebuildLobby();
            }

            UpdateScheme();
            YargLogger.LogFormatInfo("[Online] Screen: {0}", _view);
        }

        private void Update()
        {
            if (_view == View.Rooms)
            {
                if (!_refreshing && !_busy && Time.unscaledTime - _lastRoomRefresh >= ROOM_REFRESH_INTERVAL)
                {
                    RefreshRooms().Forget();
                }
            }
            else if (_dirty && Time.unscaledTime - _lastLobbyRefresh >= LOBBY_REFRESH_INTERVAL)
            {
                RebuildLobby();
            }
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
            if (_view == View.Rooms)
            {
                if (_selectedRoom != null)
                {
                    entries.Add(new NavigationScheme.Entry(MenuAction.Green, "Menu.Multiplayer.Join", Join));
                    entries.Add(new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", ShowCreateForm));
                }
                else
                {
                    entries.Add(new NavigationScheme.Entry(MenuAction.Green, "Menu.Multiplayer.Web.CreateRoom",
                        CreateRoom));
                    entries.Add(new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back));
                }
                entries.Add(new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Multiplayer.Web.Refresh",
                    () => RefreshRooms().Forget()));
            }
            else
            {
                if (MultiplayerManager.IsHosting)
                {
                    entries.Add(new NavigationScheme.Entry(MenuAction.Green, "Menu.Multiplayer.Start", StartMatch));
                    entries.Add(new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Multiplayer.ChooseSong",
                        ChooseSong));
                }
                entries.Add(new NavigationScheme.Entry(MenuAction.Blue, "Menu.Multiplayer.ChoosePart", ChoosePart));
                entries.Add(new NavigationScheme.Entry(MenuAction.Red, "Menu.Multiplayer.Leave", Leave));
            }

            Navigator.Instance.PushSchemeImmediate(new NavigationScheme(entries, false));
            _schemePushed = true;
        }

        #region Rooms

        private async UniTaskVoid RefreshRooms()
        {
            // Once a create or join is on its way the transport is about to belong to a room
            if (_refreshing || _busy)
            {
                return;
            }

            _refreshing = true;
            _lastRoomRefresh = Time.unscaledTime;
            var (rooms, error) = await MultiplayerWebMenu.ListRoomsAsync();
            _refreshing = false;
            _lastRoomRefresh = Time.unscaledTime;
            // Stale when the player left the screen, or created or joined a room meanwhile
            if (this == null || !isActiveAndEnabled || _view != View.Rooms || _busy)
            {
                return;
            }

            // Joinable rooms first, since only the first rows fit
            rooms = rooms?.OrderBy(r => r.IsFull || r.Playing).ToList();

            if (error != null)
            {
                _rooms = new List<RelayRoom>();
                _roomsEmpty.text = error;
                _roomsEmpty.color = UiBuilder.ErrorColor;
            }
            else
            {
                _rooms = rooms;
                _roomsEmpty.text = Localize.Key("Menu.Multiplayer.Web.NoRooms");
                _roomsEmpty.color = UiBuilder.DimTextColor;
            }

            // The selected room may have closed or filled up meanwhile
            if (_selectedRoom != null)
            {
                var current = _rooms.FirstOrDefault(r => r.Id == _selectedRoom.Id);
                if (current == null)
                {
                    ShowCreateForm();
                    _createStatus.text = Localize.Key("Menu.Multiplayer.Web.Error.not_found");
                    _createStatus.color = UiBuilder.ErrorColor;
                }
                else
                {
                    _selectedRoom = current;
                    FillJoinForm();
                }
            }

            FillRooms();
        }

        private void FillRooms()
        {
            _roomCount.text = _rooms.Count == 0 ? string.Empty
                : _rooms.Count > _roomRows.Count
                    ? Localize.KeyFormat("Menu.Multiplayer.Online.RoomCountMore", _rooms.Count, _roomRows.Count)
                    : Localize.KeyFormat("Menu.Multiplayer.Online.RoomCount", _rooms.Count);
            _roomsEmpty.gameObject.SetActive(_rooms.Count == 0);

            for (int i = 0; i < _roomRows.Count; i++)
            {
                var row = _roomRows[i];
                var room = i < _rooms.Count ? _rooms[i] : null;
                row.Room = room;
                row.Root.SetActive(room != null);
                if (room == null)
                {
                    continue;
                }

                string name = room.Name;
                if (room.Playing)
                {
                    name += $"  <size=70%><color=#FFC46B>{Localize.Key("Menu.Multiplayer.Online.InMatch")}</color></size>";
                }
                row.Name.text = name;
                row.Count.text = $"{room.Players} / {room.MaxPlayers}";
                row.Count.color = room.IsFull ? UiBuilder.ErrorColor : UiBuilder.DimTextColor;
                row.Background.color = _selectedRoom != null && _selectedRoom.Id == room.Id
                    ? SelectedRowColor
                    : UiBuilder.RowColor;
            }
        }

        private void SelectRoom(RoomRow row)
        {
            if (row.Room == null || _busy)
            {
                return;
            }

            _selectedRoom = row.Room;
            _joinPassword.text = string.Empty;
            _joinStatus.text = string.Empty;
            _createForm.SetActive(false);
            _joinForm.SetActive(true);
            FillJoinForm();
            FillRooms();
            UpdateScheme();
            Focus(_joinPassword);
        }

        private void FillJoinForm()
        {
            _joinTitle.text = Localize.KeyFormat("Menu.Multiplayer.Online.JoinRoom", _selectedRoom.Name);
            string details = $"{_selectedRoom.Players} / {_selectedRoom.MaxPlayers}";
            if (_selectedRoom.IsFull)
            {
                details += " · " + Localize.Key("Menu.Multiplayer.Web.Error.full");
            }
            else if (_selectedRoom.Playing)
            {
                details += " · " + Localize.Key("Menu.Multiplayer.Online.InMatch");
            }
            _joinDetails.text = details;
        }

        private void ShowCreateForm()
        {
            _selectedRoom = null;
            _joinForm.SetActive(false);
            _createForm.SetActive(true);
            _createStatus.text = string.Empty;
            if (string.IsNullOrEmpty(_roomName.text) && MultiplayerManager.Instance.LocalPlayer is { } player)
            {
                _roomName.text = Localize.KeyFormat("Menu.Multiplayer.Online.DefaultRoomName", player.Profile.Name);
            }
            FillRooms();
            UpdateScheme();
        }

        private void ChangeMaxPlayers(int delta)
        {
            _maxPlayers = Mathf.Clamp(_maxPlayers + delta, 2, ProtocolInfo.MaxMachines);
            _maxPlayersLabel.text = _maxPlayers.ToString();
        }

        private void CreateRoom()
        {
            if (_busy)
            {
                return;
            }

            string name = _roomName.text.Trim();
            if (name.Length == 0)
            {
                SetStatus(_createStatus, Localize.Key("Menu.Multiplayer.Online.NeedName"), true);
                Focus(_roomName);
                return;
            }
            if (_roomPassword.text.Length == 0)
            {
                SetStatus(_createStatus, Localize.Key("Menu.Multiplayer.Online.NeedPassword"), true);
                Focus(_roomPassword);
                return;
            }

            CreateAsync(name, _roomPassword.text).Forget();
        }

        private async UniTaskVoid CreateAsync(string name, string password)
        {
            _busy = true;
            SetStatus(_createStatus, Localize.Key("Menu.Multiplayer.Online.Creating"), false);
            string error = await MultiplayerWebMenu.CreateAsync(name, password, _maxPlayers);
            _busy = false;
            if (this == null)
            {
                return;
            }

            if (error != null)
            {
                SetStatus(_createStatus, error, true);
                return;
            }

            _roomPassword.text = string.Empty;
            ShowLobby();
        }

        private void Join()
        {
            if (_busy || _selectedRoom == null)
            {
                return;
            }
            if (_joinPassword.text.Length == 0)
            {
                SetStatus(_joinStatus, Localize.Key("Menu.Multiplayer.Online.NeedPassword"), true);
                Focus(_joinPassword);
                return;
            }

            JoinAsync(_selectedRoom, _joinPassword.text).Forget();
        }

        private async UniTaskVoid JoinAsync(RelayRoom room, string password)
        {
            _busy = true;
            SetStatus(_joinStatus, Localize.Key("Menu.Multiplayer.Online.Joining"), false);
            string error = await MultiplayerWebMenu.JoinAsync(room, password);
            _busy = false;
            if (this == null)
            {
                return;
            }

            if (error != null)
            {
                // Stay on the form to try the password again
                SetStatus(_joinStatus, error, true);
                _joinPassword.text = string.Empty;
                Focus(_joinPassword);
                return;
            }

            _joinPassword.text = string.Empty;
            _selectedRoom = null;
            ShowLobby();
        }

        private void Back()
        {
            // A refresh may still be waiting; dropping the transport fails it, and its result is ignored
            if (_busy)
            {
                return;
            }

            MultiplayerWebMenu.DropTransport();
            MenuManager.Instance.PopMenu();
        }

        private static void Focus(TMP_InputField field)
        {
            EventSystem.current?.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        }

        private static void SetStatus(TextMeshProUGUI label, string text, bool error)
        {
            label.text = text;
            label.color = error ? UiBuilder.ErrorColor : UiBuilder.DimTextColor;
        }

        #endregion

        #region Lobby

        private void RebuildLobby()
        {
            _dirty = false;
            _lastLobbyRefresh = Time.unscaledTime;

            var manager = MultiplayerManager.Instance;
            var session = manager.Session;
            if (session == null)
            {
                return;
            }

            var players = session.Players.Where(p => p.Status != LobbyPlayerStatus.Left).ToList();
            var self = players.FirstOrDefault(p => p.PlayerId == session.LocalPlayerId);
            int maxPlayers = PlatformInfo.IsWeb ? MultiplayerWebMenu.RoomMaxPlayers : 0;

            // Header
            _title.text = PlatformInfo.IsWeb && !string.IsNullOrEmpty(MultiplayerWebMenu.RoomName)
                ? MultiplayerWebMenu.RoomName
                : Localize.Key("Menu.Multiplayer.Lobby");
            string count = maxPlayers > 0
                ? Localize.KeyFormat("Menu.Multiplayer.Online.PlayerCountOf", players.Count, maxPlayers)
                : Localize.KeyFormat("Menu.Multiplayer.Online.PlayerCount", players.Count);
            if (session.IsHost)
            {
                _subtitle.text = Localize.Key("Menu.Multiplayer.Online.YouHost") + " · " + count;
            }
            else
            {
                string hostName = players.FirstOrDefault(p => p.PlayerId == 0)?.Name ?? string.Empty;
                _subtitle.text = Localize.KeyFormat("Menu.Multiplayer.Online.HostedBy", hostName) + " · " + count;
            }

            // Song
            var offer = session.CurrentOffer;
            var song = LocalSong(offer);
            if (song != null && !ReferenceEquals(offer, _readyOffer))
            {
                _readyOffer = offer;
                YargLogger.LogFormatInfo("[Online] Song ready: {0}", offer.Name);
            }
            if (offer == null)
            {
                _songName.text = Localize.Key("Menu.Multiplayer.Online.NoSong");
                _songArtist.text = Localize.Key(session.IsHost
                    ? "Menu.Multiplayer.Online.NoSongHost"
                    : "Menu.Multiplayer.Online.NoSongGuest");
            }
            else
            {
                _songName.text = offer.Name;
                _songArtist.text = offer.Artist;
            }
            SetCover(song);

            _progressBar.SetActive(false);
            _songStatus.text = string.Empty;
            if (self != null && offer != null)
            {
                switch (self.Status)
                {
                    case LobbyPlayerStatus.Downloading:
                        _progressBar.SetActive(true);
                        _progressFill.anchorMax = new Vector2(self.Progress / 100f, 1f);
                        SetStatus(_songStatus,
                            Localize.KeyFormat("Menu.Multiplayer.Online.Downloading", self.Progress), false);
                        _songStatus.color = UiBuilder.WarningColor;
                        break;
                    case LobbyPlayerStatus.SongFailed:
                        SetStatus(_songStatus, Localize.Key("Menu.Multiplayer.Status.SongFailed"), true);
                        break;
                }
            }

            _chooseSong.gameObject.SetActive(session.IsHost);
            _chooseSong.interactable = CanChangeSetup(session);
            _chooseSongLabel.text = Localize.Key(offer == null
                ? "Menu.Multiplayer.ChooseSong"
                : "Menu.Multiplayer.Online.ChooseAnotherSong");
            _guestNote.gameObject.SetActive(!session.IsHost);

            // Band, then the open slots
            for (int i = 0; i < _playerRows.Count; i++)
            {
                var row = _playerRows[i];
                if (i < players.Count)
                {
                    FillPlayer(row, players[i], session);
                }
                else if (i < maxPlayers)
                {
                    FillOpenSlot(row);
                }
                else
                {
                    row.Root.SetActive(false);
                }
            }

            // Part and start
            // The host's lobby state catches up a moment after picking
            bool picked = MultiplayerMenuFlow.HasPickedPart || self is { HasPart: true };
            _partButton.interactable = song != null && CanChangeSetup(session);
            _partLabel.text = picked && self != null
                ? $"{self.Instrument.ToLocalizedName()} · {self.Difficulty.ToLocalizedName()}"
                : Localize.Key(song == null
                    ? "Menu.Multiplayer.Online.PartAfterSong"
                    : "Menu.Multiplayer.ChoosePart");
            _partButtonImage.color = song != null && !picked ? UiBuilder.PrimaryButtonColor : UiBuilder.ButtonColor;

            if (session is HostSession host)
            {
                _startButton.gameObject.SetActive(true);
                _startButton.interactable = host.CanBeginLoad;
            }
            else
            {
                _startButton.gameObject.SetActive(false);
            }

            var (hint, color) = DescribeWait(session, players, offer, song, picked);
            _hint.text = hint;
            _hint.color = color;
        }

        private static bool CanChangeSetup(IMultiplayerSession session) =>
            session.State == SessionState.Lobby || session.State == SessionState.Transferring;

        private static SongEntry LocalSong(SongOffer offer)
        {
            if (offer == null)
            {
                return null;
            }

            var manager = MultiplayerManager.Instance;
            return manager.Session.IsHost ? manager.OfferedSong : MultiplayerSongLibrary.FindByHash(offer.Hash);
        }

        private void SetCover(SongEntry song)
        {
            if (ReferenceEquals(song, _coverSong))
            {
                return;
            }

            _coverSong = song;
            _coverFrame.color = song == null ? UiBuilder.ButtonColor : Color.clear;
            _coverCancel?.Cancel();
            _cover.texture = null;
            _cover.color = Color.clear;
            if (song != null)
            {
                _coverCancel = new CancellationTokenSource();
                _cover.LoadAlbumCover(song, _coverCancel.Token);
            }
        }

        /// <summary>
        /// What the lobby is waiting for, so nobody has to guess why the song doesn't start.
        /// </summary>
        private static (string Text, Color Color) DescribeWait(IMultiplayerSession session, List<LobbyPlayer> players,
            SongOffer offer, SongEntry song, bool picked)
        {
            if (!CanChangeSetup(session))
            {
                return (Localize.Key("Menu.Multiplayer.Online.InMatch"), UiBuilder.DimTextColor);
            }
            if (offer == null)
            {
                return (Localize.Key(session.IsHost
                    ? "Menu.Multiplayer.Online.NoSongHost"
                    : "Menu.Multiplayer.Online.NoSongGuest"), UiBuilder.DimTextColor);
            }
            if (song != null && !picked)
            {
                return (Localize.Key("Menu.Multiplayer.Online.PickYourPart"), UiBuilder.WarningColor);
            }

            if (session is HostSession host)
            {
                if (host.CanBeginLoad)
                {
                    return (Localize.Key("Menu.Multiplayer.Online.AllReady"), ReadyColor);
                }

                var waiting = players.FirstOrDefault(p => p.PlayerId != session.LocalPlayerId &&
                    p.Status != LobbyPlayerStatus.HasSong && p.Status != LobbyPlayerStatus.SongFailed);
                if (waiting != null)
                {
                    return waiting.Status == LobbyPlayerStatus.Downloading
                        ? (Localize.KeyFormat("Menu.Multiplayer.Online.WaitingDownload", waiting.Name, waiting.Progress),
                            UiBuilder.WarningColor)
                        : (Localize.KeyFormat("Menu.Multiplayer.Online.WaitingFor", waiting.Name),
                            UiBuilder.WarningColor);
                }
                return (Localize.Key("Menu.Multiplayer.Online.WaitingParts"), UiBuilder.WarningColor);
            }

            return (Localize.Key("Menu.Multiplayer.Online.HostStarts"), UiBuilder.DimTextColor);
        }

        private static void FillPlayer(PlayerRow row, LobbyPlayer player, IMultiplayerSession session)
        {
            row.Root.SetActive(true);
            row.Background.color = UiBuilder.RowColor;
            row.Name.color = UiBuilder.TextColor;

            string name = player.Name;
            if (player.PlayerId == session.LocalPlayerId)
            {
                name += $" <color=#A6B0C7>({Localize.Key("Menu.Multiplayer.Online.You")})</color>";
            }
            else if (player.PlayerId == 0)
            {
                name += $" <color=#A6B0C7>({Localize.Key("Menu.Multiplayer.Online.Host")})</color>";
            }
            row.Name.text = name;
            row.Part.text = player.HasPart
                ? $"{player.Instrument.ToLocalizedName()} · {player.Difficulty.ToLocalizedName()}"
                : Localize.Key("Menu.Multiplayer.Online.NoPart");
            row.Part.gameObject.SetActive(true);

            string status = Localize.Key($"Menu.Multiplayer.Status.{player.Status}");
            if (player.Status == LobbyPlayerStatus.Downloading)
            {
                status += $" {player.Progress}%";
            }
            row.PillText.text = status.ToUpperInvariant();
            row.Pill.SetActive(true);

            switch (player.Status)
            {
                case LobbyPlayerStatus.HasSong:
                case LobbyPlayerStatus.Loaded:
                    row.PillBackground.color = ReadyPillColor;
                    row.PillText.color = ReadyColor;
                    break;
                case LobbyPlayerStatus.SongFailed:
                case LobbyPlayerStatus.Left:
                    row.PillBackground.color = ErrorPillColor;
                    row.PillText.color = UiBuilder.ErrorColor;
                    break;
                case LobbyPlayerStatus.Playing:
                case LobbyPlayerStatus.Finished:
                    row.PillBackground.color = DimPillColor;
                    row.PillText.color = UiBuilder.DimTextColor;
                    break;
                default:
                    row.PillBackground.color = WarningPillColor;
                    row.PillText.color = UiBuilder.WarningColor;
                    break;
            }
        }

        private static void FillOpenSlot(PlayerRow row)
        {
            row.Root.SetActive(true);
            row.Background.color = UiBuilder.PanelColor;
            row.Name.text = Localize.Key("Menu.Multiplayer.Online.OpenSlot");
            row.Name.color = UiBuilder.DimTextColor;
            row.Part.gameObject.SetActive(false);
            row.Pill.SetActive(false);
        }

        private void ChooseSong()
        {
            var session = MultiplayerManager.Instance.Session;
            if (session is not HostSession || !CanChangeSetup(session))
            {
                return;
            }

            if (PlatformInfo.IsWeb)
            {
                // Called straight from the click or key press, so the browser allows the picker
                MultiplayerWebMenu.PickAndOfferSong().Forget();
                return;
            }

            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);
            MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
            menu.gameObject.SetActive(true);
        }

        private void ChoosePart()
        {
            var session = MultiplayerManager.Instance.Session;
            var song = session == null ? null : LocalSong(session.CurrentOffer);
            if (song != null && CanChangeSetup(session))
            {
                MultiplayerMenuFlow.OpenPartPicker(song);
            }
        }

        private void StartMatch()
        {
            if (MultiplayerManager.Instance.Session is HostSession host && host.CanBeginLoad)
            {
                host.BeginLoad();
            }
        }

        private void Leave()
        {
            MultiplayerManager.Instance.Leave();
            Close();
        }

        #endregion

        #region Layout

        private void Build(RectTransform root)
        {
            var background = UiBuilder.Image(UiBuilder.Rect("Background", root, Vector2.zero, Vector2.one),
                UiBuilder.BackgroundColor);
            background.raycastTarget = true;

            _title = _ui.Text("Title", root, new Vector2(0.04f, 0.885f), new Vector2(0.96f, 0.96f), 64,
                FontStyles.Bold | FontStyles.UpperCase, TextAlignmentOptions.BottomLeft, UiBuilder.TextColor);
            _subtitle = _ui.Text("Subtitle", root, new Vector2(0.04f, 0.835f), new Vector2(0.96f, 0.885f), 30,
                FontStyles.Normal, TextAlignmentOptions.TopLeft, UiBuilder.DimTextColor);

            BuildRooms(root);
            BuildLobby(root);
        }

        private static RectTransform Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            float spacing = 10)
        {
            var panel = UiBuilder.Rect(name, parent, anchorMin, anchorMax);
            UiBuilder.Image(panel, UiBuilder.PanelColor);
            UiBuilder.Column(panel, spacing, 32);
            return panel;
        }

        private Button LayoutButton(string name, Transform parent, float height, Color color,
            UnityEngine.Events.UnityAction onClick, string key, out TextMeshProUGUI label, float width = -1)
        {
            var rect = UiBuilder.LayoutRect(name, parent, height, width);
            var button = _ui.Button(rect, color, onClick, out label, 32);
            if (key != null)
            {
                label.text = Localize.Key(key);
            }
            return button;
        }

        private TextMeshProUGUI FieldLabel(Transform parent, string key)
        {
            var label = _ui.LayoutText("Label", parent, 30, 24, FontStyles.Normal, TextAlignmentOptions.BottomLeft,
                UiBuilder.DimTextColor);
            label.text = Localize.Key(key);
            return label;
        }

        private static RectTransform Spacer(Transform parent)
        {
            var rect = UiBuilder.Rect("Spacer", parent, Vector2.zero, Vector2.one);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 1;
            element.flexibleWidth = 1;
            return rect;
        }

        private void BuildRooms(RectTransform root)
        {
            var view = UiBuilder.Rect("Rooms", root, Vector2.zero, Vector2.one);
            _roomsView = view.gameObject;

            // Room list
            var list = Panel("RoomList", view, new Vector2(0.04f, 0.21f), new Vector2(0.665f, 0.81f));
            var header = UiBuilder.LayoutRect("Header", list, 64);
            UiBuilder.Row(header, 16);
            var heading = _ui.LayoutText("Heading", header, 64, 36, FontStyles.Bold, TextAlignmentOptions.Left,
                UiBuilder.TextColor);
            heading.text = Localize.Key("Menu.Multiplayer.Online.RoomsHeading");
            heading.GetComponent<LayoutElement>().flexibleWidth = 0;
            heading.GetComponent<LayoutElement>().preferredWidth = 200;
            _roomCount = _ui.LayoutText("Count", header, 64, 26, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.DimTextColor);
            LayoutButton("Refresh", header, 64, UiBuilder.ButtonColor, () => RefreshRooms().Forget(),
                "Menu.Multiplayer.Web.Refresh", out _, 220);

            for (int i = 0; i < ROOM_ROWS; i++)
            {
                var row = new RoomRow();
                var rect = UiBuilder.LayoutRect($"Room{i}", list, 76);
                var button = _ui.Button(rect, UiBuilder.RowColor, () => SelectRoom(row), out var label, 34);
                row.Root = rect.gameObject;
                row.Background = (Image) button.targetGraphic;
                row.Name = label;
                row.Name.alignment = TextAlignmentOptions.Left;
                row.Name.margin = new Vector4(28, 0, 200, 0);
                row.Count = _ui.Text("Count", rect, Vector2.zero, Vector2.one, 32, FontStyles.Normal,
                    TextAlignmentOptions.Right, UiBuilder.DimTextColor);
                row.Count.margin = new Vector4(0, 0, 28, 0);
                row.Root.SetActive(false);
                _roomRows.Add(row);
            }

            _roomsEmpty = _ui.LayoutText("Empty", list, 120, 30, FontStyles.Normal, TextAlignmentOptions.Center,
                UiBuilder.DimTextColor);
            _roomsEmpty.textWrappingMode = TextWrappingModes.Normal;
            Spacer(list);

            // Create form
            var side = UiBuilder.Rect("Side", view, new Vector2(0.685f, 0.21f), new Vector2(0.96f, 0.81f));
            var create = Panel("Create", side, Vector2.zero, Vector2.one);
            _createForm = create.gameObject;
            var createHeading = _ui.LayoutText("Heading", create, 56, 36, FontStyles.Bold, TextAlignmentOptions.Left,
                UiBuilder.TextColor);
            createHeading.text = Localize.Key("Menu.Multiplayer.Online.CreateHeading");
            FieldLabel(create, "Menu.Multiplayer.Web.RoomName");
            _roomName = _ui.InputField(UiBuilder.LayoutRect("Name", create, 64), string.Empty, false, 32);
            FieldLabel(create, "Menu.Multiplayer.Web.RoomPassword");
            _roomPassword = _ui.InputField(UiBuilder.LayoutRect("Password", create, 64),
                Localize.Key("Menu.Multiplayer.Online.PasswordHint"), true, 64);
            _roomPassword.onSubmit.AddListener(_ => CreateRoom());
            FieldLabel(create, "Menu.Multiplayer.Web.MaxPlayers");
            var stepper = UiBuilder.LayoutRect("Players", create, 64);
            UiBuilder.Row(stepper, 12);
            LayoutButton("Fewer", stepper, 72, UiBuilder.ButtonColor, () => ChangeMaxPlayers(-1), null,
                out var fewer, 72);
            fewer.text = "−";
            _maxPlayersLabel = _ui.LayoutText("Count", stepper, 72, 40, FontStyles.Bold, TextAlignmentOptions.Center,
                UiBuilder.TextColor);
            _maxPlayersLabel.text = _maxPlayers.ToString();
            LayoutButton("More", stepper, 72, UiBuilder.ButtonColor, () => ChangeMaxPlayers(1), null, out var more,
                72);
            more.text = "+";
            Spacer(create);
            _createStatus = _ui.LayoutText("Status", create, 48, 24, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.DimTextColor);
            _createStatus.textWrappingMode = TextWrappingModes.Normal;
            LayoutButton("Create", create, 76, UiBuilder.PrimaryButtonColor, CreateRoom,
                "Menu.Multiplayer.Web.CreateRoom", out _);

            // Join form, in place of the create form while a room is selected
            var join = Panel("Join", side, Vector2.zero, Vector2.one);
            _joinForm = join.gameObject;
            _joinTitle = _ui.LayoutText("Heading", join, 56, 36, FontStyles.Bold, TextAlignmentOptions.Left,
                UiBuilder.TextColor);
            _joinDetails = _ui.LayoutText("Details", join, 40, 26, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.DimTextColor);
            FieldLabel(join, "Menu.Multiplayer.Web.RoomPassword");
            _joinPassword = _ui.InputField(UiBuilder.LayoutRect("Password", join, 64),
                Localize.Key("Menu.Multiplayer.Online.AskHost"), true, 64);
            _joinPassword.onSubmit.AddListener(_ => Join());
            _joinStatus = _ui.LayoutText("Status", join, 64, 26, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.DimTextColor);
            _joinStatus.textWrappingMode = TextWrappingModes.Normal;
            Spacer(join);
            LayoutButton("Join", join, 76, UiBuilder.PrimaryButtonColor, Join, "Menu.Multiplayer.Join", out _);
            LayoutButton("Cancel", join, 64, UiBuilder.ButtonColor, ShowCreateForm, "Menu.Common.Cancel", out _);
            _joinForm.SetActive(false);

            // Bottom bar
            var bar = UiBuilder.Rect("Bar", view, new Vector2(0.04f, 0.11f), new Vector2(0.96f, 0.19f));
            UiBuilder.Row(bar, 24);
            Spacer(bar);
            LayoutButton("Back", bar, 80, UiBuilder.ButtonColor, Back, "Menu.Common.Back", out _, 260);
        }

        private void BuildLobby(RectTransform root)
        {
            var view = UiBuilder.Rect("Lobby", root, Vector2.zero, Vector2.one);
            _lobbyView = view.gameObject;

            // Song
            var songPanel = Panel("Song", view, new Vector2(0.04f, 0.21f), new Vector2(0.35f, 0.81f));
            var coverFrame = UiBuilder.LayoutRect("CoverFrame", songPanel, 240);
            _coverFrame = UiBuilder.Image(coverFrame, UiBuilder.ButtonColor);
            var coverRect = UiBuilder.Rect("Cover", coverFrame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            coverRect.sizeDelta = new Vector2(240, 240);
            _cover = coverRect.gameObject.AddComponent<RawImage>();
            _cover.color = Color.clear;
            _cover.raycastTarget = false;
            _songName = _ui.LayoutText("Name", songPanel, 56, 44, FontStyles.Bold, TextAlignmentOptions.BottomLeft,
                UiBuilder.TextColor);
            _songArtist = _ui.LayoutText("Artist", songPanel, 56, 30, FontStyles.Normal, TextAlignmentOptions.TopLeft,
                UiBuilder.DimTextColor);
            _songArtist.textWrappingMode = TextWrappingModes.Normal;
            _songStatus = _ui.LayoutText("Status", songPanel, 36, 26, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.WarningColor);
            var bar = UiBuilder.LayoutRect("Progress", songPanel, 14);
            UiBuilder.Image(bar, UiBuilder.FieldColor);
            _progressBar = bar.gameObject;
            _progressFill = UiBuilder.Rect("Fill", bar, Vector2.zero, new Vector2(0f, 1f));
            UiBuilder.Image(_progressFill, UiBuilder.WarningColor);
            Spacer(songPanel);
            _chooseSong = LayoutButton("ChooseSong", songPanel, 76, UiBuilder.ButtonColor, ChooseSong,
                "Menu.Multiplayer.ChooseSong", out _chooseSongLabel);
            _guestNote = _ui.LayoutText("GuestNote", songPanel, 40, 26, FontStyles.Normal, TextAlignmentOptions.Left,
                UiBuilder.DimTextColor);
            _guestNote.text = Localize.Key("Menu.Multiplayer.Online.HostPicks");

            // Band
            var band = Panel("Band", view, new Vector2(0.365f, 0.21f), new Vector2(0.96f, 0.81f));
            band.GetComponent<VerticalLayoutGroup>().spacing = 8;
            var heading = _ui.LayoutText("Heading", band, 52, 36, FontStyles.Bold, TextAlignmentOptions.Left,
                UiBuilder.TextColor);
            heading.text = Localize.Key("Menu.Multiplayer.Online.Band");
            for (int i = 0; i < ProtocolInfo.MaxMachines; i++)
            {
                var row = new PlayerRow();
                // One line per player, so a full room of eight fits: name, part, status
                var rect = UiBuilder.LayoutRect($"Player{i}", band, 56);
                row.Root = rect.gameObject;
                row.Background = UiBuilder.Image(rect, UiBuilder.RowColor);
                row.Name = _ui.Text("Name", rect, Vector2.zero, new Vector2(0.45f, 1f), 30, FontStyles.Bold,
                    TextAlignmentOptions.Left, UiBuilder.TextColor);
                row.Name.margin = new Vector4(24, 0, 12, 0);
                row.Part = _ui.Text("Part", rect, new Vector2(0.45f, 0f), Vector2.one, 26, FontStyles.Normal,
                    TextAlignmentOptions.Left, UiBuilder.DimTextColor);
                row.Part.margin = new Vector4(0, 0, 316, 0);

                var pill = UiBuilder.Rect("Pill", rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
                pill.sizeDelta = new Vector2(280, 40);
                pill.anchoredPosition = new Vector2(-164, 0);
                row.Pill = pill.gameObject;
                row.PillBackground = UiBuilder.Image(pill, ReadyPillColor);
                row.PillText = _ui.Text("Text", pill, Vector2.zero, Vector2.one, 22, FontStyles.Bold,
                    TextAlignmentOptions.Center, ReadyColor);
                row.Root.SetActive(false);
                _playerRows.Add(row);
            }
            Spacer(band);

            // Bottom bar: the local part, what the lobby waits for, leave and start
            var bottom = UiBuilder.Rect("Bar", view, new Vector2(0.04f, 0.11f), new Vector2(0.96f, 0.19f));
            UiBuilder.Row(bottom, 24);
            var partHeading = _ui.LayoutText("PartHeading", bottom, 80, 28, FontStyles.Normal,
                TextAlignmentOptions.Right, UiBuilder.DimTextColor);
            partHeading.text = Localize.Key("Menu.Multiplayer.Online.YourPart");
            partHeading.GetComponent<LayoutElement>().flexibleWidth = 0;
            partHeading.GetComponent<LayoutElement>().preferredWidth = 170;
            _partButton = LayoutButton("Part", bottom, 80, UiBuilder.ButtonColor, ChoosePart, null, out _partLabel,
                420);
            _partButtonImage = (Image) _partButton.targetGraphic;
            _hint = _ui.LayoutText("Hint", bottom, 80, 26, FontStyles.Normal, TextAlignmentOptions.Right,
                UiBuilder.DimTextColor);
            _hint.textWrappingMode = TextWrappingModes.Normal;
            LayoutButton("Leave", bottom, 80, UiBuilder.DangerButtonColor, Leave, "Menu.Multiplayer.Leave",
                out var leaveLabel, 200);
            leaveLabel.color = new Color(1f, 0.7f, 0.7f, 1f);
            _startButton = LayoutButton("Start", bottom, 80, UiBuilder.PrimaryButtonColor, StartMatch,
                "Menu.Multiplayer.Start", out _, 240);
        }

        #endregion
    }
}
