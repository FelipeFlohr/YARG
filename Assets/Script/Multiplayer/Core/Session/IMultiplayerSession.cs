using System;
using System.Collections.Generic;
using YARG.Core;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.Session
{
    public interface IMultiplayerSession : IDisposable
    {
        bool IsHost { get; }

        /// <summary>
        /// The host is always player 0; -1 on a client until the host accepts it.
        /// </summary>
        int LocalPlayerId { get; }

        SessionState              State        { get; }
        IReadOnlyList<LobbyPlayer> Players     { get; }
        SongOffer?                CurrentOffer { get; }

        /// <summary>
        /// Local clock time (seconds) at which the song starts, once the host scheduled it.
        /// </summary>
        double? StartTimeLocal { get; }

        event Action LobbyChanged;

        /// <summary>
        /// <see cref="PlayerSetup"/>, <see cref="InputBatch"/>, <see cref="StatsCheckpoint"/> or
        /// <see cref="Finished"/> from another player.
        /// </summary>
        event Action<NetMessage> GameMessageReceived;

        /// <summary>
        /// The session ended for a reason other than <see cref="Leave"/> or <see cref="IDisposable.Dispose"/>.
        /// </summary>
        event Action<string> Ended;

        event Action BeginLoadRequested;
        event Action ReturnToLobbyRequested;

        /// <summary>
        /// Processes network events, pings, timeouts and song transfers. Call once per frame.
        /// </summary>
        void Tick();

        void SetLocalPart(Instrument instrument, Difficulty difficulty);

        /// <summary>
        /// Sends one of the four game messages: the host broadcasts it, a client sends it to the host,
        /// which relays it to everybody else.
        /// </summary>
        void SendGameMessage(NetMessage message);

        void ReportLoaded();
        void Leave();
    }

    internal static class GameMessages
    {
        public static bool TryGetPlayerId(NetMessage message, out int playerId)
        {
            switch (message)
            {
                case PlayerSetup m:
                    playerId = m.PlayerId;
                    return true;
                case InputBatch m:
                    playerId = m.PlayerId;
                    return true;
                case StatsCheckpoint m:
                    playerId = m.PlayerId;
                    return true;
                case Finished m:
                    playerId = m.PlayerId;
                    return true;
                default:
                    playerId = -1;
                    return false;
            }
        }

        public static void EnsureGameMessage(NetMessage message)
        {
            if (!TryGetPlayerId(message, out _))
            {
                throw new ArgumentException($"{message.Type} is not a game message");
            }
        }
    }
}
