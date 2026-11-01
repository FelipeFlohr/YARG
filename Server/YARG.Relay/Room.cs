using System;
using System.Collections.Generic;

namespace YARG.Relay
{
    /// <summary>
    /// A host and its clients. Every member is guarded by the <see cref="RoomRegistry"/> lock.
    /// </summary>
    public sealed class Room
    {
        public string          Id         { get; }
        public string          Name       { get; }
        public int             MaxPlayers { get; }
        public PasswordHasher  Password   { get; }
        public RelayConnection Host       { get; }

        public Dictionary<ushort, RelayConnection> Clients { get; } = new();

        public bool     Playing      { get; set; }
        public DateTime LastActivity { get; set; }
        public bool     IsClosed     { get; set; }

        private ushort _nextPeerId = 1;

        /// <summary>
        /// Machines in the room, the host included.
        /// </summary>
        public int Players => 1 + Clients.Count;

        public Room(string id, string name, int maxPlayers, PasswordHasher password, RelayConnection host,
            DateTime now)
        {
            Id = id;
            Name = name;
            MaxPlayers = maxPlayers;
            Password = password;
            Host = host;
            LastActivity = now;
        }

        /// <summary>
        /// Client peer IDs start at 1 (the host is 0) and aren't reused within a room until they wrap around.
        /// </summary>
        public ushort NextPeerId()
        {
            ushort id = _nextPeerId;
            // Wrapping after 65535 joins skips 0 and IDs still in use
            do
            {
                _nextPeerId = (ushort) (_nextPeerId == ushort.MaxValue ? 1 : _nextPeerId + 1);
            } while (Clients.ContainsKey(_nextPeerId));
            return id;
        }
    }
}
