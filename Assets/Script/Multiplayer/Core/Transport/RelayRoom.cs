using System;

namespace YARG.Multiplayer.Transport
{
    /// <summary>
    /// A room as the relay lists it. Every room has a password.
    /// </summary>
    public sealed class RelayRoom
    {
        public string Id         { get; }
        public string Name       { get; }
        public int    Players    { get; }
        public int    MaxPlayers { get; }
        public bool   Playing    { get; }

        public bool IsFull => Players >= MaxPlayers;

        public RelayRoom(string id, string name, int players, int maxPlayers, bool playing)
        {
            Id = id;
            Name = name;
            Players = players;
            MaxPlayers = maxPlayers;
            Playing = playing;
        }
    }

    /// <summary>
    /// The relay refused a request, or the connection to it failed (<see cref="Code"/> is then
    /// <see cref="CONNECTION_LOST"/>).
    /// </summary>
    public sealed class RelayException : Exception
    {
        public const string WRONG_PASSWORD  = "wrong_password";
        public const string FULL            = "full";
        public const string NOT_FOUND       = "not_found";
        public const string RATE_LIMITED    = "rate_limited";
        public const string TOO_LARGE       = "too_large";
        public const string TOO_MANY_ROOMS  = "too_many_rooms";
        public const string ALREADY_IN_ROOM = "already_in_room";
        public const string BAD_REQUEST     = "bad_request";
        public const string CONNECTION_LOST = "connection_lost";

        public string Code { get; }

        public RelayException(string code, string? detail = null)
            : base(detail == null ? $"Relay error: {code}" : $"Relay error: {code} ({detail})")
        {
            Code = code;
        }
    }
}
