using YARG.Core;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Multiplayer.Session;

namespace YARG.Multiplayer.Protocol
{
    public abstract class NetMessage
    {
        public abstract MessageType Type { get; }
    }

    public sealed class Hello : NetMessage
    {
        public override MessageType Type => MessageType.Hello;

        public ushort ProtocolVersion { get; }
        public string GameVersion     { get; }
        public string ProfileName     { get; }

        public Hello(ushort protocolVersion, string gameVersion, string profileName)
        {
            ProtocolVersion = protocolVersion;
            GameVersion = gameVersion;
            ProfileName = profileName;
        }
    }

    public sealed class Welcome : NetMessage
    {
        public override MessageType Type => MessageType.Welcome;

        public int PlayerId { get; }

        public Welcome(int playerId)
        {
            PlayerId = playerId;
        }
    }

    public sealed class Reject : NetMessage
    {
        public override MessageType Type => MessageType.Reject;

        public RejectCode Code            { get; }
        public string     HostGameVersion { get; }

        public Reject(RejectCode code, string hostGameVersion)
        {
            Code = code;
            HostGameVersion = hostGameVersion;
        }
    }

    public sealed class LobbyState : NetMessage
    {
        public override MessageType Type => MessageType.LobbyState;

        public LobbyPlayer[] Players { get; }
        public SongOffer?    Offer   { get; }
        public SessionState  State   { get; }

        public LobbyState(LobbyPlayer[] players, SongOffer? offer, SessionState state)
        {
            Players = players;
            Offer = offer;
            State = state;
        }
    }

    public sealed class SetPart : NetMessage
    {
        public override MessageType Type => MessageType.SetPart;

        public Instrument Instrument { get; }
        public Difficulty Difficulty { get; }

        public SetPart(Instrument instrument, Difficulty difficulty)
        {
            Instrument = instrument;
            Difficulty = difficulty;
        }
    }

    public sealed class Ping : NetMessage
    {
        public override MessageType Type => MessageType.Ping;

        public double SentAt { get; }

        public Ping(double sentAt)
        {
            SentAt = sentAt;
        }
    }

    public sealed class Pong : NetMessage
    {
        public override MessageType Type => MessageType.Pong;

        public double PingSentAt  { get; }
        public double ReplySentAt { get; }

        public Pong(double pingSentAt, double replySentAt)
        {
            PingSentAt = pingSentAt;
            ReplySentAt = replySentAt;
        }
    }

    public sealed class SongOffer : NetMessage
    {
        public override MessageType Type => MessageType.SongOffer;

        public HashWrapper     Hash       { get; }
        public string          Name       { get; }
        public string          Artist     { get; }
        public SongPackageKind Kind       { get; }
        public long            TotalBytes { get; }
        public float           SongSpeed  { get; }

        public SongOffer(HashWrapper hash, string name, string artist, SongPackageKind kind, long totalBytes,
            float songSpeed)
        {
            Hash = hash;
            Name = name;
            Artist = artist;
            Kind = kind;
            TotalBytes = totalBytes;
            SongSpeed = songSpeed;
        }
    }

    public sealed class HaveSong : NetMessage
    {
        public override MessageType Type => MessageType.HaveSong;

        public HashWrapper Hash { get; }

        public HaveSong(HashWrapper hash)
        {
            Hash = hash;
        }
    }

    public sealed class NeedSong : NetMessage
    {
        public override MessageType Type => MessageType.NeedSong;

        public HashWrapper Hash { get; }

        public NeedSong(HashWrapper hash)
        {
            Hash = hash;
        }
    }

    public sealed class SongChunk : NetMessage
    {
        public override MessageType Type => MessageType.SongChunk;

        public HashWrapper Hash   { get; }
        public long        Offset { get; }
        public byte[]      Data   { get; }

        public SongChunk(HashWrapper hash, long offset, byte[] data)
        {
            Hash = hash;
            Offset = offset;
            Data = data;
        }
    }

    public sealed class SongReady : NetMessage
    {
        public override MessageType Type => MessageType.SongReady;

        public HashWrapper Hash { get; }

        public SongReady(HashWrapper hash)
        {
            Hash = hash;
        }
    }

    public sealed class SongFailed : NetMessage
    {
        public override MessageType Type => MessageType.SongFailed;

        public HashWrapper Hash   { get; }
        public string      Reason { get; }

        public SongFailed(HashWrapper hash, string reason)
        {
            Hash = hash;
            Reason = reason;
        }
    }

    public sealed class BeginLoad : NetMessage
    {
        public override MessageType Type => MessageType.BeginLoad;
    }

    public sealed class Loaded : NetMessage
    {
        public override MessageType Type => MessageType.Loaded;
    }

    public sealed class StartAt : NetMessage
    {
        public override MessageType Type => MessageType.StartAt;

        public double HostTime { get; }

        public StartAt(double hostTime)
        {
            HostTime = hostTime;
        }
    }

    public sealed class ReturnToLobby : NetMessage
    {
        public override MessageType Type => MessageType.ReturnToLobby;
    }

    /// <summary>
    /// A player's profile and engine parameters, as a <see cref="YARG.Core.Replays.ReplayFrame"/>
    /// without inputs (see <see cref="ReplayFrameCodec"/>).
    /// </summary>
    public sealed class PlayerSetup : NetMessage
    {
        public override MessageType Type => MessageType.PlayerSetup;

        public int    PlayerId { get; }
        public byte[] Frame    { get; }

        /// <summary>
        /// Profiles don't serialize the bot flag (replays never needed it), but a bot sends no inputs,
        /// so the other machines have to run it as a bot too.
        /// </summary>
        public bool IsBot { get; }

        public PlayerSetup(int playerId, byte[] frame, bool isBot)
        {
            PlayerId = playerId;
            Frame = frame;
            IsBot = isBot;
        }
    }

    public sealed class InputBatch : NetMessage
    {
        public override MessageType Type => MessageType.InputBatch;

        public int PlayerId { get; }

        /// <summary>
        /// Song time the sender has fully processed: no later batch will contain an input before it.
        /// </summary>
        public double Watermark { get; }

        public GameInput[] Inputs { get; }

        public InputBatch(int playerId, double watermark, GameInput[] inputs)
        {
            PlayerId = playerId;
            Watermark = watermark;
            Inputs = inputs;
        }
    }

    public sealed class StatsCheckpoint : NetMessage
    {
        public override MessageType Type => MessageType.StatsCheckpoint;

        public int    PlayerId { get; }
        public byte[] Frame    { get; }

        public StatsCheckpoint(int playerId, byte[] frame)
        {
            PlayerId = playerId;
            Frame = frame;
        }
    }

    public sealed class Finished : NetMessage
    {
        public override MessageType Type => MessageType.Finished;

        public int    PlayerId { get; }
        public byte[] Frame    { get; }

        public Finished(int playerId, byte[] frame)
        {
            PlayerId = playerId;
            Frame = frame;
        }
    }

    public sealed class Leave : NetMessage
    {
        public override MessageType Type => MessageType.Leave;

        public string Reason { get; }

        public Leave(string reason)
        {
            Reason = reason;
        }
    }
}
