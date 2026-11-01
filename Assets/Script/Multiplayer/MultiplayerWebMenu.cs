using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Localization;
using YARG.Menu.Persistent;
using YARG.Multiplayer.Transport;
using YARG.Song.Web;

namespace YARG.Multiplayer
{
    /// <summary>
    /// The web's online menu: password rooms on the relay instead of the desktop's IP and port.
    /// </summary>
    public static class MultiplayerWebMenu
    {
        private const float REQUEST_TIMEOUT = 15;

        // Opened for the room list and reused to create or join, then handed to the session
        private static WebSocketTransport _transport;

        /// <summary>
        /// The relay's address: <c>-relay</c> (<c>?relay=wss://…</c> on the page) or the page's own origin.
        /// </summary>
        public static string RelayUrl
        {
            get
            {
                var args = PlatformInfo.GetArgs();
                int index = Array.IndexOf(args, "-relay");
                if (index >= 0 && index + 1 < args.Length)
                {
                    return args[index + 1];
                }
                return BrowserWebSocket.DefaultRelayUrl();
            }
        }

        public static WebSocketTransport CreateTransport()
        {
            string url = RelayUrl;
            YargLogger.LogFormatInfo("Connecting to the multiplayer relay at {0}", url);
            return new WebSocketTransport(new BrowserWebSocket(), url ?? "ws://invalid/ws");
        }

        /// <summary>
        /// Pumps the transport every frame until the relay answers. Throws the request's
        /// <see cref="RelayException"/>, or a <see cref="RelayException.CONNECTION_LOST"/> one after the timeout.
        /// </summary>
        public static async UniTask<T> WaitForRelay<T>(WebSocketTransport transport, Task<T> request)
        {
            float deadline = Time.realtimeSinceStartup + REQUEST_TIMEOUT;
            while (!request.IsCompleted)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    throw new RelayException(RelayException.CONNECTION_LOST, "no answer from the server");
                }
                transport.Pump();
                await UniTask.Yield();
            }
            return await request;
        }

        public static async UniTask WaitForRelay(WebSocketTransport transport, Task request)
        {
            float deadline = Time.realtimeSinceStartup + REQUEST_TIMEOUT;
            while (!request.IsCompleted)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    throw new RelayException(RelayException.CONNECTION_LOST, "no answer from the server");
                }
                transport.Pump();
                await UniTask.Yield();
            }
            await request;
        }

        public static string DescribeError(RelayException error)
        {
            switch (error.Code)
            {
                case RelayException.WRONG_PASSWORD:
                case RelayException.FULL:
                case RelayException.NOT_FOUND:
                case RelayException.RATE_LIMITED:
                case RelayException.TOO_LARGE:
                case RelayException.TOO_MANY_ROOMS:
                    return Localize.Key($"Menu.Multiplayer.Web.Error.{error.Code}");
                case RelayException.CONNECTION_LOST:
                    return Localize.KeyFormat("Menu.Multiplayer.Web.Error.connection_lost", error.Message);
                default:
                    return Localize.KeyFormat("Menu.Multiplayer.Web.Error.Other", error.Code);
            }
        }

        public static void OpenRoomBrowser()
        {
            OpenRoomBrowserAsync().Forget();
        }

        private static WebSocketTransport EnsureTransport()
        {
            // A socket the relay closed (or one already used for a room) can't be reused
            if (_transport == null || _transport.IsClosed || _transport.IsInRoom)
            {
                DropTransport();
                _transport = CreateTransport();
            }
            return _transport;
        }

        private static void DropTransport()
        {
            _transport?.Dispose();
            _transport = null;
        }

        private static void ShowError(RelayException error)
        {
            YargLogger.LogFormatWarning("Relay request failed: {0}", error.Message);
            ToastManager.ToastError(DescribeError(error));
            if (error.Code == RelayException.CONNECTION_LOST)
            {
                DropTransport();
            }
        }

        /// <summary>
        /// A dialog's callback runs before that dialog is cleared, so the next one waits for it.
        /// </summary>
        private static UniTask WaitForNoDialog() =>
            UniTask.WaitUntil(() => !DialogManager.Instance.IsDialogShowing);

        private static async UniTaskVoid OpenRoomBrowserAsync()
        {
            var transport = EnsureTransport();

            IReadOnlyList<RelayRoom> rooms;
            try
            {
                rooms = await WaitForRelay(transport, transport.ListRoomsAsync());
            }
            catch (RelayException e)
            {
                ShowError(e);
                return;
            }

            await WaitForNoDialog();
            var dialog = DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.Web.Rooms"));
            if (rooms.Count == 0)
            {
                dialog.AddListButton(Localize.Key("Menu.Multiplayer.Web.NoRooms"), () => { }, false);
            }

            foreach (var room in rooms)
            {
                string label = Localize.KeyFormat("Menu.Multiplayer.Web.Room", room.Name, room.Players,
                    room.MaxPlayers);
                if (room.Playing)
                {
                    label += Localize.Key("Menu.Multiplayer.Web.InMatch");
                }

                dialog.AddListButton(label, () =>
                {
                    DialogManager.Instance.ClearDialog();
                    AskPassword(room);
                }, false);
            }

            dialog.AddListButton(Localize.Key("Menu.Multiplayer.Web.CreateRoom"), () =>
            {
                DialogManager.Instance.ClearDialog();
                AskRoomName();
            }, false);
            dialog.AddListButton(Localize.Key("Menu.Multiplayer.Web.Refresh"), () =>
            {
                DialogManager.Instance.ClearDialog();
                OpenRoomBrowser();
            }, false);
            dialog.AddListButton(Localize.Key("Menu.Common.Back"), () =>
            {
                DialogManager.Instance.ClearDialog();
                DropTransport();
            }, false);
        }

        private static void AskPassword(RelayRoom room)
        {
            var dialog = DialogManager.Instance.ShowRenameDialog(
                Localize.KeyFormat("Menu.Multiplayer.Web.JoinPassword", room.Name),
                password => JoinAsync(room, password).Forget());
            dialog.SetPasswordMode();
        }

        private static async UniTaskVoid JoinAsync(RelayRoom room, string password)
        {
            var transport = EnsureTransport();
            try
            {
                await WaitForRelay(transport, transport.JoinRoomAsync(room.Id, password));
            }
            catch (RelayException e)
            {
                ShowError(e);
                // Back to the list, e.g. to try the password again
                await WaitForNoDialog();
                OpenRoomBrowser();
                return;
            }

            // The session owns the transport now
            _transport = null;
            MultiplayerManager.Instance.JoinWeb(transport);
            MultiplayerMenuFlow.RequestOpenLobby();
        }

        private static void AskRoomName()
        {
            DialogManager.Instance.ShowRenameDialog(Localize.Key("Menu.Multiplayer.Web.RoomName"),
                name => AskRoomPassword(name.Trim()).Forget());
        }

        private static async UniTaskVoid AskRoomPassword(string name)
        {
            await WaitForNoDialog();
            var dialog = DialogManager.Instance.ShowRenameDialog(Localize.Key("Menu.Multiplayer.Web.RoomPassword"),
                password => AskMaxPlayers(name, password).Forget());
            dialog.SetPasswordMode();
        }

        private static async UniTaskVoid AskMaxPlayers(string name, string password)
        {
            await WaitForNoDialog();
            var dialog = DialogManager.Instance.ShowList(Localize.Key("Menu.Multiplayer.Web.MaxPlayers"));
            for (int players = 2; players <= ProtocolInfo.MaxMachines; players++)
            {
                int max = players;
                dialog.AddListButton(Localize.KeyFormat("Menu.Multiplayer.Web.Players", max), () =>
                {
                    DialogManager.Instance.ClearDialog();
                    CreateAsync(name, password, max).Forget();
                }, false);
            }
        }

        private static async UniTaskVoid CreateAsync(string name, string password, int maxPlayers)
        {
            var transport = EnsureTransport();
            try
            {
                await WaitForRelay(transport, transport.CreateRoomAsync(name, password, maxPlayers));
            }
            catch (RelayException e)
            {
                ShowError(e);
                return;
            }

            _transport = null;
            MultiplayerManager.Instance.HostWeb(transport);
            MultiplayerMenuFlow.RequestOpenLobby();
        }

        /// <summary>
        /// Host: picks a song from the player's computer (the web song loader's picker), offers it to the room
        /// and asks for the host's part.
        /// </summary>
        public static async UniTaskVoid PickAndOfferSong()
        {
            SongEntry song;
            try
            {
                song = await WebSongLoader.PickAsync(CancellationToken.None);
            }
            catch (WebSongLoadException e)
            {
                YargLogger.LogException(e, "Failed to load the song to offer");
                ToastManager.ToastError(Localize.KeyFormat("Menu.Multiplayer.Web.SongLoadFailed", e.Message));
                MultiplayerMenuFlow.OpenLobby();
                return;
            }

            if (song == null || !MultiplayerManager.IsHosting)
            {
                MultiplayerMenuFlow.OpenLobby();
                return;
            }

            if (await MultiplayerManager.Instance.OfferSong(song, 1f))
            {
                MultiplayerMenuFlow.OpenPartPicker(song);
            }
            else
            {
                MultiplayerMenuFlow.OpenLobby();
            }
        }
    }
}
