namespace YARG.Multiplayer.Protocol
{
    // The numeric values are part of the wire format: only append, never reorder
    public enum MessageType : byte
    {
        Hello = 1,
        Welcome,
        Reject,
        LobbyState,
        SetPart,
        Ping,
        Pong,
        SongOffer,
        HaveSong,
        NeedSong,
        SongChunk,
        SongReady,
        SongFailed,
        BeginLoad,
        Loaded,
        StartAt,
        ReturnToLobby,
        PlayerSetup,
        InputBatch,
        StatsCheckpoint,
        Finished,
        Leave,
    }

    public enum RejectCode : byte
    {
        VersionMismatch,
        SessionFull,
        SessionInProgress,
    }

    public enum SongPackageKind : byte
    {
        IniFolderZip,
        Sng,
    }
}
