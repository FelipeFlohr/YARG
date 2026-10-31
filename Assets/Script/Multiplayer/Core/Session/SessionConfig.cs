using System;

namespace YARG.Multiplayer.Session
{
    public sealed class SessionConfig
    {
        public string          GameVersion         { get; }
        public string          LocalProfileName    { get; }
        public double          TimeoutSeconds      { get; }
        /// <summary>
        /// Silence limit while joining, until the host's Welcome arrives (host side: until the client's Hello).
        /// <see cref="TimeoutSeconds"/> applies afterwards.
        /// </summary>
        public double          ConnectTimeoutSeconds { get; }
        public double          PingIntervalSeconds { get; }
        public int             ChunksPerTick       { get; }
        public Action<string>  Log                 { get; }

        public SessionConfig(string gameVersion, string localProfileName,
            double timeoutSeconds = 5, double pingIntervalSeconds = 0.5, int chunksPerTick = 16,
            Action<string>? log = null, double connectTimeoutSeconds = 10)
        {
            GameVersion = gameVersion;
            LocalProfileName = localProfileName;
            TimeoutSeconds = timeoutSeconds;
            ConnectTimeoutSeconds = connectTimeoutSeconds;
            PingIntervalSeconds = pingIntervalSeconds;
            ChunksPerTick = chunksPerTick;
            Log = log ?? (_ => { });
        }
    }
}
