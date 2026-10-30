using System;
using System.IO;
using System.Text;
using YARG.Core;
using YARG.Core.Input;
using YARG.Core.Song;

namespace YARG.Multiplayer.Protocol
{
    /// <summary>
    /// Binary encoding of <see cref="NetMessage"/>s. The first byte is the <see cref="MessageType"/>;
    /// everything is little-endian. Decoding never trusts the sender: any malformed input throws
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    public static class MessageCodec
    {
        public const int MaxInputsPerBatch = 10_000;
        public const int MaxLobbyPlayers = 16;

        // Time (double) + Action (int) + Integer (int)
        private const int INPUT_SIZE = sizeof(double) + sizeof(int) + sizeof(int);

        public static byte[] Encode(NetMessage message)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);

            writer.Write((byte) message.Type);
            switch (message)
            {
                case Hello m:
                    writer.Write(m.ProtocolVersion);
                    writer.Write(m.GameVersion);
                    writer.Write(m.ProfileName);
                    break;
                case Welcome m:
                    writer.Write(m.PlayerId);
                    break;
                case Reject m:
                    writer.Write((byte) m.Code);
                    writer.Write(m.HostGameVersion);
                    break;
                case LobbyState m:
                    writer.Write(m.Players.Length);
                    foreach (var player in m.Players)
                    {
                        WriteLobbyPlayer(writer, player);
                    }
                    writer.Write(m.Offer != null);
                    if (m.Offer != null)
                    {
                        WriteSongOffer(writer, m.Offer);
                    }
                    break;
                case SetPart m:
                    writer.Write((byte) m.Instrument);
                    writer.Write((byte) m.Difficulty);
                    break;
                case Ping m:
                    writer.Write(m.SentAt);
                    break;
                case Pong m:
                    writer.Write(m.PingSentAt);
                    writer.Write(m.ReplySentAt);
                    break;
                case SongOffer m:
                    WriteSongOffer(writer, m);
                    break;
                case HaveSong m:
                    WriteHash(writer, m.Hash);
                    break;
                case NeedSong m:
                    WriteHash(writer, m.Hash);
                    break;
                case SongChunk m:
                    WriteHash(writer, m.Hash);
                    writer.Write(m.Offset);
                    WriteBytes(writer, m.Data);
                    break;
                case SongReady m:
                    WriteHash(writer, m.Hash);
                    break;
                case SongFailed m:
                    WriteHash(writer, m.Hash);
                    writer.Write(m.Reason);
                    break;
                case BeginLoad _:
                case Loaded _:
                case ReturnToLobby _:
                    break;
                case StartAt m:
                    writer.Write(m.HostTime);
                    break;
                case PlayerSetup m:
                    writer.Write(m.PlayerId);
                    WriteBytes(writer, m.Frame);
                    break;
                case InputBatch m:
                    writer.Write(m.PlayerId);
                    writer.Write(m.Watermark);
                    writer.Write(m.Inputs.Length);
                    foreach (var input in m.Inputs)
                    {
                        writer.Write(input.Time);
                        writer.Write(input.Action);
                        writer.Write(input.Integer);
                    }
                    break;
                case StatsCheckpoint m:
                    writer.Write(m.PlayerId);
                    WriteBytes(writer, m.Frame);
                    break;
                case Finished m:
                    writer.Write(m.PlayerId);
                    WriteBytes(writer, m.Frame);
                    break;
                case Leave m:
                    writer.Write(m.Reason);
                    break;
                default:
                    throw new ArgumentException($"Unsupported message type {message.GetType().Name}");
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static NetMessage Decode(byte[] data)
        {
            if (data.Length == 0)
            {
                throw new InvalidDataException("Empty message");
            }

            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            NetMessage message;
            try
            {
                message = DecodeBody(reader);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is FormatException ||
                e is ArgumentException || e is DecoderFallbackException || e is OverflowException)
            {
                throw new InvalidDataException($"Malformed message: {e.Message}", e);
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException(
                    $"{stream.Length - stream.Position} trailing bytes after {message.Type} message");
            }
            return message;
        }

        private static NetMessage DecodeBody(BinaryReader reader)
        {
            var type = (MessageType) reader.ReadByte();
            switch (type)
            {
                case MessageType.Hello:
                    return new Hello(reader.ReadUInt16(), reader.ReadString(), reader.ReadString());
                case MessageType.Welcome:
                    return new Welcome(reader.ReadInt32());
                case MessageType.Reject:
                    return new Reject(ReadEnum<RejectCode>(reader), reader.ReadString());
                case MessageType.LobbyState:
                {
                    int count = ReadCount(reader, MaxLobbyPlayers, "lobby players");
                    var players = new LobbyPlayer[count];
                    for (int i = 0; i < count; i++)
                    {
                        players[i] = ReadLobbyPlayer(reader);
                    }
                    var offer = reader.ReadBoolean() ? ReadSongOffer(reader) : null;
                    return new LobbyState(players, offer);
                }
                case MessageType.SetPart:
                    return new SetPart(ReadEnum<Instrument>(reader), ReadEnum<Difficulty>(reader));
                case MessageType.Ping:
                    return new Ping(reader.ReadDouble());
                case MessageType.Pong:
                    return new Pong(reader.ReadDouble(), reader.ReadDouble());
                case MessageType.SongOffer:
                    return ReadSongOffer(reader);
                case MessageType.HaveSong:
                    return new HaveSong(ReadHash(reader));
                case MessageType.NeedSong:
                    return new NeedSong(ReadHash(reader));
                case MessageType.SongChunk:
                {
                    var hash = ReadHash(reader);
                    long offset = reader.ReadInt64();
                    if (offset < 0)
                    {
                        throw new InvalidDataException($"Negative chunk offset {offset}");
                    }
                    return new SongChunk(hash, offset, ReadBytes(reader, ProtocolInfo.MaxChunkSize));
                }
                case MessageType.SongReady:
                    return new SongReady(ReadHash(reader));
                case MessageType.SongFailed:
                    return new SongFailed(ReadHash(reader), reader.ReadString());
                case MessageType.BeginLoad:
                    return new BeginLoad();
                case MessageType.Loaded:
                    return new Loaded();
                case MessageType.StartAt:
                    return new StartAt(reader.ReadDouble());
                case MessageType.ReturnToLobby:
                    return new ReturnToLobby();
                case MessageType.PlayerSetup:
                    return new PlayerSetup(reader.ReadInt32(), ReadBytes(reader, ProtocolInfo.MaxMessageSize));
                case MessageType.InputBatch:
                {
                    int playerId = reader.ReadInt32();
                    double watermark = reader.ReadDouble();
                    int count = ReadCount(reader, MaxInputsPerBatch, "inputs");
                    EnsureRemaining(reader, (long) count * INPUT_SIZE);

                    var inputs = new GameInput[count];
                    for (int i = 0; i < count; i++)
                    {
                        inputs[i] = new GameInput(reader.ReadDouble(), reader.ReadInt32(), reader.ReadInt32());
                    }
                    return new InputBatch(playerId, watermark, inputs);
                }
                case MessageType.StatsCheckpoint:
                    return new StatsCheckpoint(reader.ReadInt32(), ReadBytes(reader, ProtocolInfo.MaxMessageSize));
                case MessageType.Finished:
                    return new Finished(reader.ReadInt32(), ReadBytes(reader, ProtocolInfo.MaxMessageSize));
                case MessageType.Leave:
                    return new Leave(reader.ReadString());
                default:
                    throw new InvalidDataException($"Unknown message type {(byte) type}");
            }
        }

        private static void WriteLobbyPlayer(BinaryWriter writer, LobbyPlayer player)
        {
            writer.Write(player.PlayerId);
            writer.Write(player.Name);
            writer.Write((byte) player.Instrument);
            writer.Write((byte) player.Difficulty);
            writer.Write((byte) player.Status);
            writer.Write(player.Progress);
        }

        private static LobbyPlayer ReadLobbyPlayer(BinaryReader reader)
        {
            return new LobbyPlayer(
                reader.ReadInt32(),
                reader.ReadString(),
                ReadEnum<Instrument>(reader),
                ReadEnum<Difficulty>(reader),
                ReadEnum<LobbyPlayerStatus>(reader),
                reader.ReadByte());
        }

        private static void WriteSongOffer(BinaryWriter writer, SongOffer offer)
        {
            WriteHash(writer, offer.Hash);
            writer.Write(offer.Name);
            writer.Write(offer.Artist);
            writer.Write((byte) offer.Kind);
            writer.Write(offer.TotalBytes);
            writer.Write(offer.SongSpeed);
        }

        private static SongOffer ReadSongOffer(BinaryReader reader)
        {
            return new SongOffer(
                ReadHash(reader),
                reader.ReadString(),
                reader.ReadString(),
                ReadEnum<SongPackageKind>(reader),
                reader.ReadInt64(),
                reader.ReadSingle());
        }

        private static void WriteHash(BinaryWriter writer, HashWrapper hash)
        {
            writer.Write(hash.HashBytes);
        }

        private static HashWrapper ReadHash(BinaryReader reader)
        {
            EnsureRemaining(reader, HashWrapper.HASH_SIZE_IN_BYTES);
            return HashWrapper.Create(reader.ReadBytes(HashWrapper.HASH_SIZE_IN_BYTES));
        }

        private static void WriteBytes(BinaryWriter writer, byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ReadBytes(BinaryReader reader, int max)
        {
            int count = ReadCount(reader, max, "bytes");
            EnsureRemaining(reader, count);
            return reader.ReadBytes(count);
        }

        private static int ReadCount(BinaryReader reader, int max, string what)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max)
            {
                throw new InvalidDataException($"Invalid count of {what}: {count} (max {max})");
            }
            return count;
        }

        private static void EnsureRemaining(BinaryReader reader, long bytes)
        {
            var stream = reader.BaseStream;
            if (stream.Length - stream.Position < bytes)
            {
                throw new InvalidDataException("Message is truncated");
            }
        }

        private static T ReadEnum<T>(BinaryReader reader) where T : Enum
        {
            byte value = reader.ReadByte();
            if (!Enum.IsDefined(typeof(T), value))
            {
                throw new InvalidDataException($"Invalid {typeof(T).Name} value {value}");
            }
            return (T) Enum.ToObject(typeof(T), value);
        }
    }
}
