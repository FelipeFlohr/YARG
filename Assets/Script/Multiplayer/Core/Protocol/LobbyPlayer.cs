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

        /// <summary>
        /// The player chose a part for the current song; until then <see cref="Instrument"/> and
        /// <see cref="Difficulty"/> are only defaults.
        /// </summary>
        public bool HasPart { get; }

        public LobbyPlayer(int playerId, string name, Instrument instrument, Difficulty difficulty,
            LobbyPlayerStatus status, byte progress, bool hasPart = false)
        {
            PlayerId = playerId;
            Name = name;
            Instrument = instrument;
            Difficulty = difficulty;
            Status = status;
            Progress = progress;
            HasPart = hasPart;
        }
    }
}
