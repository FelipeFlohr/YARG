using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.Dialogs;
using YARG.Menu.DifficultySelect;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Player;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Online multiplayer's way through the menus: host or join, the lobby (<see cref="OnlineMenu"/>) and picking a
    /// part.
    /// </summary>
    public static class MultiplayerMenuFlow
    {
        private static bool      _openLobbyRequested;
        private static SongOffer _partPickedFor;

        /// <summary>
        /// The local player chose a part for the song offered now.
        /// </summary>
        public static bool HasPickedPart
        {
            get
            {
                var offer = MultiplayerManager.Instance.Session?.CurrentOffer;
                return offer != null && ReferenceEquals(offer, _partPickedFor);
            }
        }

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

            // The web joins password rooms on the relay instead of an address
            if (PlatformInfo.IsWeb)
            {
                OnlineMenu.ShowRooms();
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
                        _openLobbyRequested = true;
                    }
                });
            }, false);
        }

        public static void OpenLobby()
        {
            if (!MultiplayerManager.IsActive || MenuManager.Instance == null ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MultiplayerGameplay.IsInMatch)
            {
                return;
            }

            OnlineMenu.ShowLobby();
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
                _partPickedFor = manager.Session?.CurrentOffer;
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

            if (DialogManager.Instance.IsDialogShowing)
            {
                return;
            }

            var profile = player.Profile;
            var dialog = DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.ChoosePart"));
            YargLogger.LogInfo("[Online] Part picker");
            // A profile in a game mode without a track (left over from an older version) has no part to play
            // The profile's own instrument first, hardest difficulty first: what most players pick
            var instruments = profile.GameMode.HasGameplayTrack()
                ? profile.GameMode.PossibleInstrumentsForSong(song).OrderBy(i => i != profile.CurrentInstrument)
                : Enumerable.Empty<Instrument>();
            foreach (var instrument in instruments)
            {
                // The game mode lists vocal parts the song may not have
                if (!PartDifficulty.HasPlayablePart(song, instrument))
                {
                    continue;
                }

                foreach (var difficulty in EnumExtensions<Difficulty>.Values.Reverse())
                {
                    if (!PartDifficulty.HasPlayableDifficulty(song, instrument, difficulty))
                    {
                        continue;
                    }

                    dialog.AddListButton($"{instrument.ToLocalizedName()} - {difficulty.ToLocalizedName()}", () =>
                    {
                        profile.CurrentInstrument = instrument;
                        profile.CurrentDifficulty = difficulty;
                        session.SetLocalPart(instrument, difficulty);
                        _partPickedFor = session.CurrentOffer;
                        YargLogger.LogFormatInfo("[Online] Part: {0} {1}", instrument, difficulty);

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

            // The session ended (the host left, the connection dropped) while the lobby was up
            if (!MultiplayerManager.IsActive && OnlineMenu.IsLobbyShowing)
            {
                OnlineMenu.Close();
            }
        }

        public static void OnLobbyChanged()
        {
            OnlineMenu.MarkDirty();
        }

        /// <summary>
        /// Opens the lobby once the menu is ready, e.g. after the score screen.
        /// </summary>
        public static void RequestOpenLobby()
        {
            _openLobbyRequested = true;
        }

        /// <summary>
        /// Closes any dialog this flow opened (part picker, join prompt) before leaving the menu.
        /// The dialog manager is persistent, so they would otherwise stay over gameplay.
        /// </summary>
        public static void CloseAll()
        {
            _openLobbyRequested = false;
            if (DialogManager.Instance.IsDialogShowing)
            {
                DialogManager.Instance.ClearDialog();
            }
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
