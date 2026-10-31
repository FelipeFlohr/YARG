using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu.Dialogs;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Player;

namespace YARG.Multiplayer
{
    /// <summary>
    /// The online multiplayer dialogs: host or join, the lobby, and picking a part.
    /// </summary>
    public static class MultiplayerMenuFlow
    {
        private const float LOBBY_REFRESH_INTERVAL = 0.25f;

        private static ListDialog _lobby;
        private static bool       _lobbyDirty;
        private static bool       _openLobbyRequested;
        private static float      _lastLobbyRefresh = float.NegativeInfinity;

        private static bool IsLobbyOpen => _lobby != null;

        public static void OpenEntry()
        {
            if (MultiplayerManager.IsActive)
            {
                OpenLobby();
                return;
            }

            int activePlayers = PlayerContainer.Players.Count(player => !player.SittingOut);
            if (activePlayers == 0)
            {
                ShowError("Menu.Multiplayer.NoActivePlayer");
                return;
            }
            if (activePlayers > 1)
            {
                ShowError("Menu.Multiplayer.OnePlayerOnly");
                return;
            }

            var dialog = DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.Title"));
            dialog.AddListButton(Localize.Key("Menu.Multiplayer.Host"), () =>
            {
                DialogManager.Instance.ClearDialog();
                if (MultiplayerManager.Instance.Host())
                {
                    OpenLobby();
                }
            }, false);
            dialog.AddListButton(Localize.Key("Menu.Multiplayer.Join"), () =>
            {
                DialogManager.Instance.ClearDialog();
                DialogManager.Instance.ShowRenameDialog(Localize.Key("Menu.Multiplayer.JoinPrompt"), address =>
                {
                    // Runs before the rename dialog is cleared, so the lobby opens on the next frame
                    if (MultiplayerManager.Instance.Join(address))
                    {
                        _lobbyDirty = true;
                        _openLobbyRequested = true;
                    }
                });
            }, false);
        }

        public static void OpenLobby()
        {
            if (!MultiplayerManager.IsActive || IsLobbyOpen || DialogManager.Instance.IsDialogShowing ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MultiplayerGameplay.IsInMatch)
            {
                return;
            }

            _lobby =DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.Lobby"));
            RebuildLobby();
        }

        /// <summary>
        /// Host only: offers the song picked in the difficulty select with the part picked there,
        /// then shows the lobby.
        /// </summary>
        public static async UniTaskVoid OfferSong(SongEntry song, float songSpeed)
        {
            var manager = MultiplayerManager.Instance;
            if (await manager.OfferSong(song, songSpeed) && manager.LocalPlayer is { } localPlayer)
            {
                manager.Session?.SetLocalPart(localPlayer.Profile.CurrentInstrument,
                    localPlayer.Profile.CurrentDifficulty);
            }

            OpenLobby();
        }

        public static void OpenPartPicker(SongEntry song)
        {
            var session = MultiplayerManager.Instance.Session;
            var player = MultiplayerManager.Instance.LocalPlayer;
            if (session == null || player == null)
            {
                return;
            }

            CloseLobby();
            if (DialogManager.Instance.IsDialogShowing)
            {
                return;
            }

            var profile = player.Profile;
            var dialog = DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.ChoosePart"));
            foreach (var instrument in profile.GameMode.PossibleInstrumentsForSong(song))
            {
                foreach (var difficulty in EnumExtensions<Difficulty>.Values)
                {
                    if (!song[instrument][difficulty])
                    {
                        continue;
                    }

                    dialog.AddListButton($"{instrument.ToLocalizedName()} - {difficulty.ToLocalizedName()}", () =>
                    {
                        profile.CurrentInstrument = instrument;
                        profile.CurrentDifficulty = difficulty;
                        session.SetLocalPart(instrument, difficulty);

                        DialogManager.Instance.ClearDialog();
                        OpenLobby();
                    }, false);
                }
            }
        }

        /// <summary>
        /// Called every frame by <see cref="MultiplayerManager"/>.
        /// </summary>
        public static void Update()
        {
            if (_openLobbyRequested && !DialogManager.Instance.IsDialogShowing)
            {
                _openLobbyRequested = false;
                OpenLobby();
            }

            if (!IsLobbyOpen)
            {
                _lobby = null;
                return;
            }

            if (!MultiplayerManager.IsActive)
            {
                CloseLobby();
                return;
            }

            if (_lobbyDirty && Time.unscaledTime - _lastLobbyRefresh >= LOBBY_REFRESH_INTERVAL)
            {
                RebuildLobby();
            }
        }

        public static void OnLobbyChanged()
        {
            _lobbyDirty = true;
        }

        /// <summary>
        /// Opens the lobby once the menu is ready, e.g. after the score screen.
        /// </summary>
        public static void RequestOpenLobby()
        {
            _openLobbyRequested = true;
        }

        private static void RebuildLobby()
        {
            _lobbyDirty = false;
            _lastLobbyRefresh = Time.unscaledTime;
            _lobby.ClearList();

            var manager = MultiplayerManager.Instance;
            var session = manager.Session;
            var offer = session.CurrentOffer;

            _lobby.Title.text = offer == null
                ? Localize.Key("Menu.Multiplayer.Lobby")
                : $"{Localize.Key("Menu.Multiplayer.Lobby")}\n<size=65%>{offer.Name} - {offer.Artist}</size>";

            foreach (var player in session.Players)
            {
                _lobby.AddListButton(FormatPlayer(player), () => { }, false);
            }

            if (session is HostSession host)
            {
                _lobby.AddListButton(Localize.Key("Menu.Multiplayer.ChooseSong"), () =>
                {
                    CloseLobby();
                    var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);
                    MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
                    menu.gameObject.SetActive(true);
                }, false);

                _lobby.AddListButton(Localize.Key("Menu.Multiplayer.Start"), () =>
                {
                    if (!host.CanBeginLoad)
                    {
                        ToastManager.ToastWarning(Localize.Key("Menu.Multiplayer.NotReady"));
                        return;
                    }

                    CloseLobby();
                    host.BeginLoad();
                }, false);
            }

            var song = offer == null ? null : MultiplayerSongLibrary.FindByHash(offer.Hash);
            if (song != null)
            {
                _lobby.AddListButton(Localize.Key("Menu.Multiplayer.ChoosePart"), () => OpenPartPicker(song), false);
            }

            _lobby.AddListButton(Localize.Key("Menu.Multiplayer.Leave"), () =>
            {
                CloseLobby();
                manager.Leave();
            }, false);
        }

        private static string FormatPlayer(LobbyPlayer player)
        {
            string status = Localize.Key($"Menu.Multiplayer.Status.{player.Status}");
            if (player.Status == LobbyPlayerStatus.Downloading)
            {
                status += $" {player.Progress}%";
            }

            return $"{player.Name} - {player.Instrument.ToLocalizedName()} {player.Difficulty.ToLocalizedName()} - {status}";
        }

        /// <summary>
        /// Closes any dialog this flow opened (lobby, part picker, join prompt) before leaving the menu.
        /// The dialog manager is persistent, so they would otherwise stay over gameplay.
        /// </summary>
        public static void CloseAll()
        {
            _openLobbyRequested = false;
            _lobby = null;
            if (DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ClearDialog();
            }
        }

        private static void CloseLobby()
        {
            if (IsLobbyOpen)
            {
                DialogManager.Instance.ClearDialog();
            }
            _lobby = null;
        }

        private static void ShowError(string key)
        {
            if (!DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ShowMessage(Localize.Key("Menu.Multiplayer.Title"), Localize.Key(key));
            }
        }
    }
}
