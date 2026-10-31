namespace YARG.Multiplayer
{
    public static class ProtocolInfo
    {
        public const ushort Version = 2;
        public const int DefaultPort = 7676;

        public const int MaxMessageSize = 1024 * 1024;
        public const int MaxChunkSize = 64 * 1024;
        public const long MaxSongBytes = 2L * 1024 * 1024 * 1024;

        // Including the host
        public const int MaxMachines = 4;
    }
}
