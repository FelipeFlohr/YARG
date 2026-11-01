using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Engine.Guitar;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;

namespace YARG.Multiplayer.Tests.Protocol
{
    public class MessageCodecTests
    {
        private static readonly HashWrapper TestHash = HashWrapper.Hash(new byte[] { 1, 2, 3 });

        private static SongOffer TestOffer() =>
            new SongOffer(TestHash, "Song", "Artist", SongPackageKind.Sng, 5_000_000_000, 1.25f);

        public static IEnumerable<NetMessage> AllMessages()
        {
            yield return new Hello(ProtocolInfo.Version, "v0.15", "Ana");
            yield return new Welcome(3);
            yield return new Reject(RejectCode.SessionFull, "v0.15");
            yield return new LobbyState(new[]
            {
                new LobbyPlayer(0, "Host", Instrument.FiveFretGuitar, Difficulty.Expert, LobbyPlayerStatus.HasSong, 100, true),
                new LobbyPlayer(2, "Bia", Instrument.ProDrums, Difficulty.Hard, LobbyPlayerStatus.Downloading, 42),
            }, TestOffer(), SessionState.Transferring);
            yield return new LobbyState(Array.Empty<LobbyPlayer>(), null, SessionState.Lobby);
            yield return new SetPart(Instrument.FiveFretBass, Difficulty.Medium);
            yield return new Ping(12.5);
            yield return new Pong(12.5, 1012.75);
            yield return TestOffer();
            yield return new HaveSong(TestHash);
            yield return new NeedSong(TestHash);
            yield return new SongChunk(TestHash, 4_294_967_296, new byte[] { 5, 6, 7 });
            yield return new SongReady(TestHash);
            yield return new SongFailed(TestHash, "hash mismatch");
            yield return new BeginLoad();
            yield return new Loaded();
            yield return new StartAt(103.0);
            yield return new ReturnToLobby();
            yield return new PlayerSetup(1, new byte[] { 1, 2, 3, 4 }, isBot: true);
            yield return new InputBatch(2, 1.6, new[] { new GameInput(1.25, 3, 1) });
            yield return new StatsCheckpoint(1, new byte[] { 9 });
            yield return new Finished(1, new byte[] { 8, 7 });
            yield return new Leave("bye");
        }

        [Test]
        public void EveryMessageTypeIsCovered()
        {
            var covered = AllMessages().Select(m => m.Type).Distinct().OrderBy(t => t);
            var all = Enum.GetValues(typeof(MessageType)).Cast<MessageType>().OrderBy(t => t);
            Assert.That(covered, Is.EqualTo(all));
        }

        [TestCaseSource(nameof(AllMessages))]
        public void Message_RoundTripsAllProperties(NetMessage message)
        {
            var decoded = MessageCodec.Decode(MessageCodec.Encode(message));
            AssertDeepEqual(decoded, message, message.GetType().Name);
        }

        [Test]
        public void InputBatch_RoundTrips()
        {
            var inputs = new[] { new GameInput(1.25, 3, 1), new GameInput(1.5, 4, -7) };
            var decoded = (InputBatch) MessageCodec.Decode(MessageCodec.Encode(new InputBatch(2, 1.6, inputs)));
            Assert.That(decoded.PlayerId, Is.EqualTo(2));
            Assert.That(decoded.Watermark, Is.EqualTo(1.6));
            Assert.That(decoded.Inputs.Select(i => (i.Time, i.Action, i.Integer)),
                Is.EqualTo(inputs.Select(i => (i.Time, i.Action, i.Integer))));
        }

        [Test]
        public void SongOffer_RoundTripsHashAndFields()
        {
            var decoded = (SongOffer) MessageCodec.Decode(MessageCodec.Encode(TestOffer()));
            Assert.That(decoded.Hash, Is.EqualTo(HashWrapper.Hash(new byte[] { 1, 2, 3 })));
            Assert.That(decoded.TotalBytes, Is.EqualTo(5_000_000_000));
            Assert.That(decoded.Kind, Is.EqualTo(SongPackageKind.Sng));
            Assert.That(decoded.SongSpeed, Is.EqualTo(1.25f));
        }

        [Test]
        public void LobbyState_RoundTripsWithAndWithoutOffer()
        {
            var player = new LobbyPlayer(1, "Ana", Instrument.Vocals, Difficulty.Easy, LobbyPlayerStatus.Idle, 0);

            var with = (LobbyState) MessageCodec.Decode(MessageCodec.Encode(new LobbyState(new[] { player }, TestOffer(), SessionState.Results)));
            Assert.That(with.Offer, Is.Not.Null);
            Assert.That(with.Offer!.Name, Is.EqualTo("Song"));
            Assert.That(with.Players.Single().Name, Is.EqualTo("Ana"));
            Assert.That(with.State, Is.EqualTo(SessionState.Results));

            var without = (LobbyState) MessageCodec.Decode(MessageCodec.Encode(new LobbyState(new[] { player }, null, SessionState.Lobby)));
            Assert.That(without.Offer, Is.Null);
            Assert.That(without.Players.Single().Instrument, Is.EqualTo(Instrument.Vocals));
        }

        [Test]
        public void Decode_ThrowsOnUnknownType()
        {
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(new byte[] { 255 }));
        }

        [Test]
        public void Decode_ThrowsOnEmptyData()
        {
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(Array.Empty<byte>()));
        }

        [Test]
        public void Decode_ThrowsOnTruncatedData()
        {
            var data = MessageCodec.Encode(new Hello(ProtocolInfo.Version, "v0.15", "Ana"));
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data.Take(data.Length - 1).ToArray()));
        }

        [Test]
        public void Decode_ThrowsOnTrailingBytes()
        {
            var data = MessageCodec.Encode(new Loaded()).Concat(new byte[] { 0 }).ToArray();
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }

        [Test]
        public void Decode_ThrowsOnOversizedChunk()
        {
            var data = MessageCodec.Encode(new SongChunk(TestHash, 0, new byte[ProtocolInfo.MaxChunkSize + 1]));
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }

        [Test]
        public void Decode_ThrowsOnTooManyInputs()
        {
            var data = MessageCodec.Encode(new InputBatch(1, 0, new GameInput[MessageCodec.MaxInputsPerBatch + 1]));
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }

        [Test]
        public void Decode_ThrowsOnTooManyLobbyPlayers()
        {
            var players = Enumerable.Range(0, MessageCodec.MaxLobbyPlayers + 1)
                .Select(i => new LobbyPlayer(i, "P", Instrument.FiveFretGuitar, Difficulty.Expert, LobbyPlayerStatus.Idle, 0))
                .ToArray();
            var data = MessageCodec.Encode(new LobbyState(players, null, SessionState.Lobby));
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }

        [Test]
        public void Decode_ThrowsOnHugeDeclaredArrayLength()
        {
            // PlayerSetup claiming a frame of int.MaxValue bytes, with nothing behind it
            var data = new byte[] { (byte) MessageType.PlayerSetup, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F };
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }

        [Test]
        public void ReplayFrameCodec_RoundTripsProfileAndStats()
        {
            var profile = new YargProfile { Name = "Ana", GameMode = GameMode.FiveFretGuitar };
            var thresholds = new[] { 0.21f, 0.46f, 0.77f, 1.85f, 3.08f, 4.29f };
            var parameters = EnginePreset.Default.FiveFretGuitar.Create(thresholds, thresholds, false);
            var frame = new ReplayFrame(profile, parameters, new GuitarStats { CommittedScore = 1234 },
                Array.Empty<GameInput>());

            var decoded = ReplayFrameCodec.Decode(ReplayFrameCodec.Encode(frame));

            Assert.That(decoded.Profile.Name, Is.EqualTo("Ana"));
            Assert.That(decoded.Stats.CommittedScore, Is.EqualTo(1234));
            Assert.That(decoded.Stats, Is.InstanceOf<GuitarStats>());
        }

        [Test]
        public void ReplayFrameCodec_ThrowsInvalidDataOnGarbage()
        {
            Assert.Throws<InvalidDataException>(() => ReplayFrameCodec.Decode(new byte[] { 1, 2, 3 }));
        }

        private static void AssertDeepEqual(object? actual, object? expected, string path)
        {
            if (expected is null || actual is null)
            {
                Assert.That(actual, Is.EqualTo(expected), path);
                return;
            }

            Assert.That(actual.GetType(), Is.EqualTo(expected.GetType()), path);
            var type = expected.GetType();

            if (expected is IList expectedList && expected is not string)
            {
                var actualList = (IList) actual;
                Assert.That(actualList.Count, Is.EqualTo(expectedList.Count), path + ".Count");
                for (int i = 0; i < expectedList.Count; i++)
                {
                    AssertDeepEqual(actualList[i], expectedList[i], $"{path}[{i}]");
                }
                return;
            }

            if (type.Namespace != null && type.Namespace.StartsWith("YARG.Multiplayer") && !type.IsEnum)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    AssertDeepEqual(property.GetValue(actual), property.GetValue(expected), $"{path}.{property.Name}");
                }
                return;
            }

            Assert.That(actual, Is.EqualTo(expected), path);
        }
    }
}
