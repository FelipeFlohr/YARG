using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace YARG.Relay
{
    /// <summary>
    /// All open rooms. One lock guards rooms and the room-related state of every connection; nothing that
    /// can block runs under it (control messages are queued, and data is sent by the caller afterwards).
    /// </summary>
    public sealed class RoomRegistry
    {
        public const string WRONG_PASSWORD = "wrong_password";
        public const string FULL           = "full";
        public const string NOT_FOUND      = "not_found";
        public const string RATE_LIMITED   = "rate_limited";
        public const string TOO_LARGE      = "too_large";
        public const string TOO_MANY_ROOMS = "too_many_rooms";
        public const string ALREADY_IN     = "already_in_room";
        public const string NOT_HOST       = "not_host";
        public const string BAD_REQUEST    = "bad_request";

        private readonly object                   _gate  = new();
        private readonly Dictionary<string, Room> _rooms = new();
        private readonly RelayOptions             _options;
        private readonly Func<DateTime>           _clock;
        private readonly ILogger                  _logger;

        public WrongPasswordLimiter Limiter { get; }

        public RoomRegistry(RelayOptions options, Func<DateTime> clock, ILogger logger)
        {
            _options = options;
            _clock = clock;
            _logger = logger;
            Limiter = new WrongPasswordLimiter(options.WrongPasswordLimit, options.WrongPasswordWindow, clock);
        }

        public object[] List()
        {
            lock (_gate)
            {
                return _rooms.Values
                    .OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(room => (object) new
                    {
                        id = room.Id,
                        name = room.Name,
                        players = room.Players,
                        max = room.MaxPlayers,
                        playing = room.Playing,
                        locked = true,
                    })
                    .ToArray();
            }
        }

        /// <returns>The error code, or null once the connection hosts the new room.</returns>
        public string? Create(RelayConnection connection, string name, string password, int maxPlayers)
        {
            // Hashing is slow on purpose, so it's done before taking the lock
            var hash = new PasswordHasher(password, _options.PasswordIterations);
            lock (_gate)
            {
                if (connection.Room != null)
                {
                    return ALREADY_IN;
                }
                if (_rooms.Values.Count(room => room.Host.Ip == connection.Ip) >= _options.MaxRoomsPerIp)
                {
                    return TOO_MANY_ROOMS;
                }

                string id;
                do
                {
                    id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
                } while (_rooms.ContainsKey(id));

                var room = new Room(id, name, maxPlayers, hash, connection, _clock());
                _rooms[id] = room;
                connection.Room = room;
                connection.PeerId = 0;
                _logger.LogInformation("Room {Id} created by {Ip} (max {Max})", id, connection.Ip, maxPlayers);
                connection.SendJson(new { type = "created", room = id });
                return null;
            }
        }

        /// <returns>The error code, or null once the connection is in the room.</returns>
        public string? Join(RelayConnection connection, string roomId, string password)
        {
            Room? room;
            lock (_gate)
            {
                if (connection.Room != null)
                {
                    return ALREADY_IN;
                }
                if (!_rooms.TryGetValue(roomId, out room))
                {
                    return NOT_FOUND;
                }
            }

            // Checked before the password, so a limited IP learns nothing from its next guesses
            if (Limiter.IsLimited(connection.Ip))
            {
                return RATE_LIMITED;
            }
            if (!room.Password.Verify(password))
            {
                Limiter.RecordFailure(connection.Ip);
                return WRONG_PASSWORD;
            }

            lock (_gate)
            {
                if (room.IsClosed)
                {
                    return NOT_FOUND;
                }
                if (connection.Room != null)
                {
                    return ALREADY_IN;
                }
                if (room.Players >= room.MaxPlayers)
                {
                    return FULL;
                }

                ushort peerId = room.NextPeerId();
                room.Clients[peerId] = connection;
                room.LastActivity = _clock();
                connection.Room = room;
                connection.PeerId = peerId;

                connection.SendJson(new { type = "joined", room = room.Id, peer = peerId });
                room.Host.SendJson(new { type = "peer_joined", peer = peerId });
                return null;
            }
        }

        /// <summary>
        /// The connection is gone: a host closes its room, a client is removed from its room.
        /// </summary>
        public void Leave(RelayConnection connection)
        {
            lock (_gate)
            {
                var room = connection.Room;
                if (room == null)
                {
                    return;
                }

                if (room.Host == connection)
                {
                    CloseRoom(room, "host_left");
                }
                else
                {
                    RemoveClient(room, connection.PeerId, null);
                }
                connection.Room = null;
            }
        }

        /// <returns>The error code, or null.</returns>
        public string? Kick(RelayConnection host, int peerId)
        {
            lock (_gate)
            {
                if (!host.IsHost)
                {
                    return NOT_HOST;
                }
                if (peerId > 0 && peerId <= ushort.MaxValue)
                {
                    RemoveClient(host.Room!, (ushort) peerId, "kicked");
                }
                return null;
            }
        }

        /// <returns>The error code, or null.</returns>
        public string? SetPlaying(RelayConnection host, bool playing)
        {
            lock (_gate)
            {
                if (!host.IsHost)
                {
                    return NOT_HOST;
                }
                host.Room!.Playing = playing;
                host.Room.LastActivity = _clock();
                return null;
            }
        }

        /// <summary>
        /// Rewrites a data frame's peer header for its destination: a client's frame goes to the host tagged
        /// with the client's ID, a host's frame goes to the client it names, tagged as coming from the host (0).
        /// </summary>
        /// <returns>The destination, or null to drop the frame.</returns>
        public RelayConnection? Route(RelayConnection source, byte[] frame)
        {
            lock (_gate)
            {
                var room = source.Room;
                if (room == null || room.IsClosed)
                {
                    return null;
                }
                room.LastActivity = _clock();

                RelayConnection? destination;
                ushort tag;
                if (room.Host == source)
                {
                    ushort target = (ushort) (frame[0] | (frame[1] << 8));
                    if (!room.Clients.TryGetValue(target, out destination))
                    {
                        return null;
                    }
                    tag = 0;
                }
                else
                {
                    destination = room.Host;
                    tag = source.PeerId;
                }

                frame[0] = (byte) tag;
                frame[1] = (byte) (tag >> 8);
                return destination;
            }
        }

        /// <summary>
        /// Closes the rooms nobody used for the idle timeout.
        /// </summary>
        public void SweepIdle()
        {
            lock (_gate)
            {
                var oldest = _clock() - _options.IdleTimeout;
                foreach (var room in _rooms.Values.Where(room => room.LastActivity <= oldest).ToList())
                {
                    CloseRoom(room, "idle");
                    room.Host.Room = null;
                    room.Host.SendJson(new { type = "closed", reason = "idle" });
                    room.Host.CloseAfterSending();
                }
            }
        }

        private void CloseRoom(Room room, string reason)
        {
            room.IsClosed = true;
            _rooms.Remove(room.Id);
            foreach (var client in room.Clients.Values)
            {
                client.Room = null;
                client.SendJson(new { type = "closed", reason });
                client.CloseAfterSending();
            }
            room.Clients.Clear();
            _logger.LogInformation("Room {Id} closed: {Reason}", room.Id, reason);
        }

        private static void RemoveClient(Room room, ushort peerId, string? closeReason)
        {
            if (!room.Clients.Remove(peerId, out var client))
            {
                return;
            }

            client.Room = null;
            if (closeReason != null)
            {
                client.SendJson(new { type = "closed", reason = closeReason });
                client.CloseAfterSending(WebSocketCloseStatus.NormalClosure);
            }
            room.Host.SendJson(new { type = "peer_left", peer = peerId });
        }
    }
}
