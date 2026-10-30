using YARG.Core;

namespace YARG.Multiplayer.Protocol
{
    public enum LobbyPlayerStatus : byte
    {
        Idle,
        MissingSong,
        Downloading,
        HasSong,
        SongFailed,
        Loaded,
        Playing,
        Finished,
        Left,
    }

    public sealed class LobbyPlayer
    {
        public int               PlayerId   { get; }
        public string            Name       { get; }
        public Instrument        Instrument { get; }
        public Difficulty        Difficulty { get; }
        public LobbyPlayerStatus Status     { get; }

        /// <summary>
        /// Song download progress, from 0 to 100.
        /// </summary>
        public byte Progress { get; }

        public LobbyPlayer(int playerId, string name, Instrument instrument, Difficulty difficulty,
            LobbyPlayerStatus status, byte progress)
        {
            PlayerId = playerId;
            Name = name;
            Instrument = instrument;
            Difficulty = difficulty;
            Status = status;
            Progress = progress;
        }
    }
}
