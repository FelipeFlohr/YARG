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

        /// <summary>
        /// The room this machine created or joined last, for the lobby's title.
        /// </summary>
        public static string RoomName { get; private set; }

        /// <summary>
        /// The size of that room, or 0 when unknown.
        /// </summary>
        public static int RoomMaxPlayers { get; private set; }

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

        /// <summary>
        /// Closes the socket opened for the room list, e.g. when the player leaves the online screen.
        /// </summary>
        public static void DropTransport()
        {
            _transport?.Dispose();
            _transport = null;
        }

        private static string Fail(RelayException error)
        {
            YargLogger.LogFormatWarning("Relay request failed: {0}", error.Message);
            if (error.Code == RelayException.CONNECTION_LOST)
            {
                DropTransport();
            }
            return DescribeError(error);
        }

        /// <summary>
        /// The relay's rooms, or <c>null</c> and the error to show.
        /// </summary>
        public static async UniTask<(IReadOnlyList<RelayRoom> Rooms, string Error)> ListRoomsAsync()
        {
            var transport = EnsureTransport();
            try
            {
                return (await WaitForRelay(transport, transport.ListRoomsAsync()), null);
            }
            catch (RelayException e)
            {
                return (null, Fail(e));
            }
        }

        /// <summary>
        /// Joins <paramref name="room"/> and starts the client session. Returns the error to show, or <c>null</c>.
        /// </summary>
        public static async UniTask<string> JoinAsync(RelayRoom room, string password)
        {
            var transport = EnsureTransport();
            try
            {
                await WaitForRelay(transport, transport.JoinRoomAsync(room.Id, password));
            }
            catch (RelayException e)
            {
                return Fail(e);
            }

            // The session owns the transport now
            _transport = null;
            RoomName = room.Name;
            RoomMaxPlayers = room.MaxPlayers;
            MultiplayerManager.Instance.JoinWeb(transport);
            return null;
        }

        /// <summary>
        /// Creates a room and starts the host session. Returns the error to show, or <c>null</c>.
        /// </summary>
        public static async UniTask<string> CreateAsync(string name, string password, int maxPlayers)
        {
            var transport = EnsureTransport();
            try
            {
                await WaitForRelay(transport, transport.CreateRoomAsync(name, password, maxPlayers));
            }
            catch (RelayException e)
            {
                return Fail(e);
            }

            _transport = null;
            RoomName = name;
            RoomMaxPlayers = maxPlayers;
            MultiplayerManager.Instance.HostWeb(transport);
            return null;
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
