using System;

namespace YARG.Relay
{
    /// <summary>
    /// Limits and timings, bound from the <c>Relay</c> configuration section (e.g. <c>Relay__MaxRoomsPerIp=3</c>).
    /// </summary>
    public sealed class RelayOptions
    {
        public const string SECTION = "Relay";

        /// <summary>
        /// Largest binary frame: a whole game message (1 MiB) plus the two-byte peer header.
        /// </summary>
        public int MaxBinaryFrameBytes { get; set; } = 1024 * 1024 + 2;

        public int MaxTextFrameBytes { get; set; } = 16 * 1024;

        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A socket that doesn't answer a ping within this is aborted.
        /// </summary>
        public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan IdleTimeout       { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan IdleCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        public int      WrongPasswordLimit  { get; set; } = 5;
        public TimeSpan WrongPasswordWindow { get; set; } = TimeSpan.FromMinutes(1);

        public int MaxRoomsPerIp { get; set; } = 3;

        /// <summary>
        /// Most machines in a room, the host included (the game's own limit).
        /// </summary>
        public int MaxPlayers { get; set; } = 8;

        public int MaxNameLength     { get; set; } = 32;
        public int MaxPasswordLength { get; set; } = 64;

        public int PasswordIterations { get; set; } = 10_000;

        /// <summary>
        /// Frames queued for one socket before writers to it have to wait.
        /// </summary>
        public int SendQueueFrames { get; set; } = 64;

        /// <summary>
        /// How long a writer waits for room in a full send queue before the slow socket is dropped.
        /// </summary>
        public TimeSpan SendStallTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Take the client IP from the first <c>X-Forwarded-For</c> value. Only behind a proxy that replaces
        /// the header (Caddy does for clients it doesn't trust), or any client could pick its own IP.
        /// </summary>
        public bool TrustForwardedFor { get; set; }
    }
}
