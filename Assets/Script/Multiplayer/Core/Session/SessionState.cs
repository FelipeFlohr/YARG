namespace YARG.Multiplayer.Session
{
    // Sent to clients in LobbyState: only append, never reorder
    public enum SessionState : byte
    {
        Lobby,
        Transferring,
        Loading,
        Playing,
        Results,
        Ended,
    }
}
