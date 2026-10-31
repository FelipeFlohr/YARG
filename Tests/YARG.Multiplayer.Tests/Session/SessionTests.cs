using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Tests.Session
{
    public class SessionTests
    {
        private const string VERSION = "v0.15";

        private sealed class Harness : IDisposable
        {
            public double Now = 100;
            public readonly InMemoryNetwork     Network = new();
            public readonly HostSession         Host;
            public readonly List<ClientSession> Clients = new();
            public readonly List<string>        Log     = new();

            private readonly double _timeoutSeconds;

            public Harness(int chunksPerTick = 16, double timeoutSeconds = 5)
            {
                _timeoutSeconds = timeoutSeconds;
                Host = new HostSession(Network.CreateHost(),
                    new SessionConfig(VERSION, "Host", timeoutSeconds: timeoutSeconds,
                        chunksPerTick: chunksPerTick, log: Log.Add), () => Now);
                Host.Start(ProtocolInfo.DefaultPort);
            }

            public ClientSession AddClient(string name, double clockOffset = 0, string version = VERSION)
            {
                var client = new ClientSession(Network.CreateClient(),
                    new SessionConfig(version, name, timeoutSeconds: _timeoutSeconds), () => Now + clockOffset);
                Clients.Add(client);
                client.Connect("ignored", ProtocolInfo.DefaultPort);
                Pump();
                return client;
            }

            public void Pump(int rounds = 4)
            {
                for (int i = 0; i < rounds; i++)
                {
                    Host.Tick();
                    foreach (var client in Clients)
                    {
                        client.Tick();
                    }
                }
            }

            public void Advance(double seconds, bool tickClients = true)
            {
                double end = Now + seconds;
                while (Now < end)
                {
                    Now = Math.Min(end, Now + 0.1);
                    if (tickClients)
                    {
                        // Two rounds so a ping and its pong complete at the same instant (zero RTT)
                        Pump(2);
                    }
                    else
                    {
                        Host.Tick();
                    }
                }
            }

            public void Dispose()
            {
                Host.Dispose();
                foreach (var client in Clients)
                {
                    client.Dispose();
                }
            }
        }

        private static SongOffer Offer(byte seed, long totalBytes) =>
            new SongOffer(HashWrapper.Hash(new[] { seed }), $"Song {seed}", "Artist", SongPackageKind.Sng,
                totalBytes, 1f);

        private static byte[] RandomBytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static LobbyPlayer PlayerIn(IMultiplayerSession session, int playerId) =>
            session.Players.Single(p => p.PlayerId == playerId);

        private static void ReachLoading(Harness h)
        {
            foreach (var client in h.Clients)
            {
                client.SongOffered += _ => client.RespondToOffer(true);
            }
            h.Host.OfferSong(Offer(1, 10), () => new MemoryStream(new byte[10]));
            h.Host.SetLocalPart(Instrument.FiveFretBass, Difficulty.Hard);
            foreach (var client in h.Clients)
            {
                client.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            }
            h.Pump();
            h.Host.BeginLoad();
            h.Pump();
        }

        private static void ReachPlaying(Harness h)
        {
            ReachLoading(h);
            h.Host.ReportLoaded();
            foreach (var client in h.Clients)
            {
                client.ReportLoaded();
            }
            h.Pump();
        }

        [Test]
        public void Join_AssignsIdsAndAllSeeThreePlayers()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var b = h.AddClient("Bia");

            Assert.That(a.LocalPlayerId, Is.EqualTo(1));
            Assert.That(b.LocalPlayerId, Is.EqualTo(2));
            Assert.That(h.Host.LocalPlayerId, Is.EqualTo(0));
            foreach (var session in new IMultiplayerSession[] { h.Host, a, b })
            {
                Assert.That(session.Players.Select(p => p.PlayerId), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(session.Players.Select(p => p.Name), Is.EqualTo(new[] { "Host", "Ana", "Bia" }));
                Assert.That(session.State, Is.EqualTo(SessionState.Lobby));
            }
            Assert.That(h.Host.IsHost, Is.True);
            Assert.That(a.IsHost, Is.False);
        }

        [Test]
        public void Join_RejectsVersionMismatch()
        {
            using var h = new Harness();
            string? endReason = null;
            var client = new ClientSession(h.Network.CreateClient(), new SessionConfig("v0.14", "Old"), () => h.Now);
            client.Ended += reason => endReason = reason;
            h.Clients.Add(client);
            client.Connect("ignored", 0);
            h.Pump();

            Assert.That(endReason, Is.Not.Null);
            Assert.That(client.State, Is.EqualTo(SessionState.Ended));
            Assert.That(client.RejectReason!.Code, Is.EqualTo(RejectCode.VersionMismatch));
            Assert.That(client.RejectReason.HostGameVersion, Is.EqualTo(VERSION));
            Assert.That(h.Host.Players, Has.Count.EqualTo(1));
        }

        [Test]
        public void Join_RejectsMachineBeyondTheLimit()
        {
            Assert.That(ProtocolInfo.MaxMachines, Is.EqualTo(8));

            using var h = new Harness();
            for (int i = 0; i < ProtocolInfo.MaxMachines - 1; i++)
            {
                Assert.That(h.AddClient($"P{i}").State, Is.EqualTo(SessionState.Lobby));
            }

            var extra = h.AddClient("Extra");

            Assert.That(extra.State, Is.EqualTo(SessionState.Ended));
            Assert.That(extra.RejectReason!.Code, Is.EqualTo(RejectCode.SessionFull));
            Assert.That(h.Host.Players, Has.Count.EqualTo(ProtocolInfo.MaxMachines));
        }

        [Test]
        public void Join_WhileSongIsOffered_IsAcceptedAndReceivesTheOffer()
        {
            using var h = new Harness();
            var song = Offer(1, 10);
            h.Host.OfferSong(song, () => new MemoryStream(new byte[10]));
            h.Pump();

            var late = new ClientSession(h.Network.CreateClient(), new SessionConfig(VERSION, "Late"), () => h.Now);
            h.Clients.Add(late);
            SongOffer? offered = null;
            late.SongOffered += offer =>
            {
                offered = offer;
                late.RespondToOffer(true);
            };
            late.Connect("ignored", ProtocolInfo.DefaultPort);
            h.Pump();

            Assert.That(late.RejectReason, Is.Null);
            Assert.That(late.State, Is.EqualTo(SessionState.Transferring));
            Assert.That(offered!.Hash, Is.EqualTo(song.Hash));
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.HasSong));
        }

        [Test]
        public void Join_RejectsWhilePlaying()
        {
            using var h = new Harness();
            h.AddClient("Ana");
            ReachPlaying(h);
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Playing));

            var late = h.AddClient("Late");

            Assert.That(late.RejectReason!.Code, Is.EqualTo(RejectCode.SessionInProgress));
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Playing));
            Assert.That(h.Host.Players, Has.Count.EqualTo(2));
        }

        [Test]
        public void SilentClient_IsMarkedLeftAfterTimeout()
        {
            using var h = new Harness();
            h.AddClient("Ana");

            h.Advance(4.9, tickClients: false);
            Assert.That(PlayerIn(h.Host, 1).Status, Is.Not.EqualTo(LobbyPlayerStatus.Left));

            h.Advance(0.2, tickClients: false);
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.Left));
        }

        [Test]
        public void Client_WithoutWelcome_GivesUpAfterTheConnectTimeout()
        {
            // The long in-session timeout must not apply while joining: the host here never answers
            using var h = new Harness(timeoutSeconds: 30);
            var client = new ClientSession(h.Network.CreateClient(),
                new SessionConfig(VERSION, "Ana", timeoutSeconds: 30), () => h.Now);
            h.Clients.Add(client);
            client.Connect("ignored", ProtocolInfo.DefaultPort);

            for (int i = 0; i < 95; i++)
            {
                h.Now += 0.1;
                client.Tick();
            }
            Assert.That(client.State, Is.Not.EqualTo(SessionState.Ended), "still within 10 s");

            for (int i = 0; i < 20; i++)
            {
                h.Now += 0.1;
                client.Tick();
            }
            Assert.That(client.State, Is.EqualTo(SessionState.Ended), "gave up after about 10 s, not 30 s");
        }

        [Test]
        public void ConnectedClient_SurvivesTheLongTimeoutOfSilence()
        {
            using var h = new Harness(timeoutSeconds: 30);
            var a = h.AddClient("Ana");
            Assert.That(a.LocalPlayerId, Is.EqualTo(1));

            // The host stays silent (busy loading); only the client keeps ticking
            for (int i = 0; i < 290; i++)
            {
                h.Now += 0.1;
                a.Tick();
            }
            Assert.That(a.State, Is.Not.EqualTo(SessionState.Ended), "29 s of silence is tolerated");

            for (int i = 0; i < 30; i++)
            {
                h.Now += 0.1;
                a.Tick();
            }
            Assert.That(a.State, Is.EqualTo(SessionState.Ended), "30 s of silence is not");
        }

        [Test]
        public void Host_KeepsAJoinedPeerThroughTheLongTimeoutOfSilence()
        {
            using var h = new Harness(timeoutSeconds: 30);
            h.AddClient("Ana");

            h.Advance(20, tickClients: false);
            Assert.That(h.Log, Has.None.Contains("timed out"), "joined peers get the long timeout");
            Assert.That(PlayerIn(h.Host, 1).Status, Is.Not.EqualTo(LobbyPlayerStatus.Left));

            h.Advance(11, tickClients: false);
            Assert.That(h.Log, Has.Some.Contains("timed out"));
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.Left));
        }

        [Test]
        public void Host_DropsAPeerThatNeverSaysHelloAfterTheConnectTimeout()
        {
            using var h = new Harness(timeoutSeconds: 30);
            var silent = h.Network.CreateClient();
            silent.Connect("ignored", ProtocolInfo.DefaultPort);
            h.Pump();

            h.Advance(9, tickClients: false);
            Assert.That(h.Log, Has.None.Contains("timed out"));

            h.Advance(2, tickClients: false);
            Assert.That(h.Log, Has.Some.Contains("timed out"));
        }

        [Test]
        public void ActiveClient_IsNotTimedOut()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            h.Advance(20);
            Assert.That(PlayerIn(h.Host, 1).Status, Is.Not.EqualTo(LobbyPlayerStatus.Left));
            Assert.That(a.State, Is.EqualTo(SessionState.Lobby));
        }

        [Test]
        public void HostDisconnect_EndsClientSession()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            int ended = 0;
            a.Ended += _ => ended++;

            h.Host.Dispose();
            a.Tick();
            a.Tick();

            Assert.That(ended, Is.EqualTo(1));
            Assert.That(a.State, Is.EqualTo(SessionState.Ended));
        }

        [Test]
        public void SilentHost_EndsClientSession()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            string? reason = null;
            a.Ended += r => reason = r;

            for (int i = 0; i < 52; i++)
            {
                h.Now += 0.1;
                a.Tick();
            }

            Assert.That(reason, Is.Not.Null);
            Assert.That(a.State, Is.EqualTo(SessionState.Ended));
        }

        [Test]
        public void ClientLeave_MarksPlayerLeft()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            a.Leave();
            h.Pump();
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.Left));
            Assert.That(a.State, Is.EqualTo(SessionState.Ended));
        }

        [Test]
        public void MalformedMessage_DropsPeer()
        {
            using var h = new Harness();
            var raw = h.Network.CreateClient();
            raw.Connect("ignored", 0);
            h.Pump();
            raw.Send(0, new byte[] { 255 });
            h.Pump();

            Assert.That(h.Log, Has.Some.Contains("malformed").IgnoreCase);
            Assert.That(h.Host.Players, Has.Count.EqualTo(1));
        }

        [Test]
        public void Offer_ClientWithSong_GoesStraightToHasSong()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            SongOffer? offered = null;
            a.SongOffered += offer =>
            {
                offered = offer;
                a.RespondToOffer(true);
            };

            var song = Offer(1, 1000);
            h.Host.OfferSong(song, () => throw new AssertionException("package must not be opened"));
            h.Pump();

            Assert.That(offered!.Hash, Is.EqualTo(song.Hash));
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.HasSong));
            Assert.That(PlayerIn(h.Host, 0).Status, Is.EqualTo(LobbyPlayerStatus.HasSong));
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Transferring));
            Assert.That(a.State, Is.EqualTo(SessionState.Transferring));
            Assert.That(a.CurrentOffer!.Hash, Is.EqualTo(song.Hash));
        }

        [Test]
        public void Offer_ClientWithoutSong_ReceivesAllChunksAndProgress()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var data = RandomBytes(1024 * 1024 + 1, 3);
            var chunks = new List<SongChunk>();
            int maxProgressSeen = 0;
            a.SongOffered += _ => a.RespondToOffer(false);
            a.SongChunkReceived += chunks.Add;
            a.LobbyChanged += () => maxProgressSeen = Math.Max(maxProgressSeen, PlayerIn(a, 1).Progress);

            var song = Offer(1, data.Length);
            h.Host.OfferSong(song, () => new MemoryStream(data));
            h.Pump(10);

            Assert.That(chunks, Is.Not.Empty);
            Assert.That(chunks.Select(c => c.Data.Length), Is.All.LessThanOrEqualTo(ProtocolInfo.MaxChunkSize));
            long expectedOffset = 0;
            foreach (var chunk in chunks)
            {
                Assert.That(chunk.Offset, Is.EqualTo(expectedOffset));
                Assert.That(chunk.Hash, Is.EqualTo(song.Hash));
                expectedOffset += chunk.Data.Length;
            }
            Assert.That(expectedOffset, Is.EqualTo(data.Length));
            Assert.That(chunks.SelectMany(c => c.Data), Is.EqualTo(data));
            Assert.That(maxProgressSeen, Is.EqualTo(100));
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.Downloading));

            a.ReportSongReady();
            h.Pump();
            Assert.That(PlayerIn(h.Host, 1).Status, Is.EqualTo(LobbyPlayerStatus.HasSong));
        }

        [Test]
        public void Offer_NeedSongAgain_RestartsFromZero()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var chunks = new List<SongChunk>();
            a.SongOffered += _ => a.RespondToOffer(false);
            a.SongChunkReceived += chunks.Add;

            h.Host.OfferSong(Offer(1, 100), () => new MemoryStream(new byte[100]));
            h.Pump();
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(new long[] { 0 }));

            a.RespondToOffer(false);
            h.Pump();
            Assert.That(chunks.Select(c => c.Offset), Is.EqualTo(new long[] { 0, 0 }));
        }

        [Test]
        public void Offer_NewOfferCancelsPreviousTransfer()
        {
            // Review Focus 4
            using var h = new Harness(chunksPerTick: 1);
            var a = h.AddClient("Ana");
            var events = new List<(string Kind, HashWrapper Hash)>();
            a.SongOffered += offer =>
            {
                events.Add(("offer", offer.Hash));
                a.RespondToOffer(false);
            };
            a.SongChunkReceived += chunk => events.Add(("chunk", chunk.Hash));

            var first = Offer(1, 4 * 1024 * 1024);
            var second = Offer(2, 100);
            h.Host.OfferSong(first, () => new MemoryStream(new byte[first.TotalBytes]));
            h.Pump(2);
            h.Host.OfferSong(second, () => new MemoryStream(new byte[100]));
            h.Pump(10);

            int secondOffer = events.IndexOf(("offer", second.Hash));
            Assert.That(secondOffer, Is.GreaterThan(0));
            Assert.That(events.Take(secondOffer), Has.Some.EqualTo(("chunk", first.Hash)));
            Assert.That(events.Skip(secondOffer), Has.None.EqualTo(("chunk", first.Hash)));
            Assert.That(events.Skip(secondOffer), Has.Some.EqualTo(("chunk", second.Hash)));
        }

        [Test]
        public void OfferSong_ResetsPartsPickedForThePreviousSong()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            a.SongOffered += _ => a.RespondToOffer(true);

            h.Host.OfferSong(Offer(1, 10), () => new MemoryStream(new byte[10]));
            h.Host.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            a.SetLocalPart(Instrument.ProDrums, Difficulty.Hard);
            h.Pump();
            Assert.That(h.Host.CanBeginLoad, Is.True);

            // The next song may not chart the same parts, so everybody picks again
            h.Host.OfferSong(Offer(2, 10), () => new MemoryStream(new byte[10]));
            h.Pump();
            Assert.That(h.Host.CanBeginLoad, Is.False);
        }

        [Test]
        public void BeginLoad_RequiresAllPlayersReady()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            int beginLoads = 0;
            a.BeginLoadRequested += () => beginLoads++;
            a.SongOffered += _ => a.RespondToOffer(true);

            Assert.That(h.Host.CanBeginLoad, Is.False);
            h.Host.OfferSong(Offer(1, 10), () => new MemoryStream(new byte[10]));
            h.Pump();
            Assert.That(h.Host.CanBeginLoad, Is.False, "nobody picked a part");

            h.Host.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            h.Pump();
            Assert.That(h.Host.CanBeginLoad, Is.False, "client has no part");
            Assert.Throws<InvalidOperationException>(() => h.Host.BeginLoad());

            a.SetLocalPart(Instrument.ProDrums, Difficulty.Hard);
            h.Pump();
            Assert.That(PlayerIn(h.Host, 1).Instrument, Is.EqualTo(Instrument.ProDrums));
            Assert.That(h.Host.CanBeginLoad, Is.True);

            h.Host.BeginLoad();
            h.Pump();
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Loading));
            Assert.That(a.State, Is.EqualTo(SessionState.Loading));
            Assert.That(beginLoads, Is.EqualTo(1));
        }

        [Test]
        public void BeginLoad_IgnoresPlayersWhoseDownloadFailed()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var b = h.AddClient("Bia");
            a.SongOffered += _ => a.RespondToOffer(true);
            b.SongOffered += _ => b.RespondToOffer(false);
            h.Host.OfferSong(Offer(1, 10), () => new MemoryStream(new byte[10]));
            h.Host.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            a.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            b.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            h.Pump();
            Assert.That(h.Host.CanBeginLoad, Is.False);

            b.ReportSongFailed("hash mismatch");
            h.Pump();

            Assert.That(PlayerIn(h.Host, 2).Status, Is.EqualTo(LobbyPlayerStatus.SongFailed));
            Assert.That(h.Host.CanBeginLoad, Is.True);
        }

        [Test]
        public void AllLoaded_SchedulesStartThreeSecondsAhead()
        {
            // Review Focus 2: the client's clock is 40 s behind the host's
            using var h = new Harness();
            var a = h.AddClient("Ana", clockOffset: -40);
            h.Advance(2);
            ReachLoading(h);

            h.Host.ReportLoaded();
            h.Pump();
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Loading), "waits for every player");

            a.ReportLoaded();
            h.Pump();

            Assert.That(h.Host.State, Is.EqualTo(SessionState.Playing));
            Assert.That(a.State, Is.EqualTo(SessionState.Playing));
            Assert.That(h.Host.StartTimeLocal, Is.EqualTo(h.Now + 3).Within(1e-9));
            Assert.That(a.StartTimeLocal, Is.EqualTo(h.Now + 3 - 40).Within(1e-6));
        }

        [Test]
        public void ClientLeavingDuringLoad_DoesNotBlockStart()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var b = h.AddClient("Bia");
            ReachLoading(h);

            h.Host.ReportLoaded();
            a.ReportLoaded();
            b.Leave();
            h.Pump();

            Assert.That(h.Host.State, Is.EqualTo(SessionState.Playing));
        }

        [Test]
        public void GameMessages_RelayToOthersButNotSender()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var b = h.AddClient("Bia");
            var hostGot = new List<NetMessage>();
            var aGot = new List<NetMessage>();
            var bGot = new List<NetMessage>();
            h.Host.GameMessageReceived += hostGot.Add;
            a.GameMessageReceived += aGot.Add;
            b.GameMessageReceived += bGot.Add;
            ReachPlaying(h);

            a.SendGameMessage(new InputBatch(1, 0.5, new[] { new GameInput(0.25, 1, 1) }));
            h.Pump();

            Assert.That(hostGot.OfType<InputBatch>().Single().PlayerId, Is.EqualTo(1));
            Assert.That(bGot.OfType<InputBatch>().Single().PlayerId, Is.EqualTo(1));
            Assert.That(aGot, Is.Empty);

            h.Host.SendGameMessage(new StatsCheckpoint(0, new byte[] { 1 }));
            h.Pump();

            Assert.That(aGot.OfType<StatsCheckpoint>().Single().PlayerId, Is.EqualTo(0));
            Assert.That(bGot.OfType<StatsCheckpoint>().Single().PlayerId, Is.EqualTo(0));
            Assert.That(hostGot.OfType<StatsCheckpoint>(), Is.Empty);
        }

        [Test]
        public void GameMessages_FromWrongPlayerIdAreDropped()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            var hostGot = new List<NetMessage>();
            h.Host.GameMessageReceived += hostGot.Add;
            ReachPlaying(h);

            a.SendGameMessage(new InputBatch(5, 0.5, Array.Empty<GameInput>()));
            h.Pump();

            Assert.That(hostGot, Is.Empty);
        }

        [Test]
        public void SendGameMessage_RejectsNonGameMessages()
        {
            using var h = new Harness();
            Assert.Throws<ArgumentException>(() => h.Host.SendGameMessage(new Ping(0)));
        }

        [Test]
        public void AllFinished_MovesHostToResults_ThenReturnToLobbyResetsStatuses()
        {
            using var h = new Harness();
            var a = h.AddClient("Ana");
            int returns = 0;
            a.ReturnToLobbyRequested += () => returns++;
            ReachPlaying(h);

            h.Host.SendGameMessage(new Finished(0, new byte[] { 1 }));
            h.Pump();
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Playing));
            Assert.That(PlayerIn(h.Host, 0).Status, Is.EqualTo(LobbyPlayerStatus.Finished));

            a.SendGameMessage(new Finished(1, new byte[] { 2 }));
            h.Pump();
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Results));
            Assert.That(a.State, Is.EqualTo(SessionState.Results));

            h.Host.ReturnToLobby();
            h.Pump();
            Assert.That(h.Host.State, Is.EqualTo(SessionState.Lobby));
            Assert.That(a.State, Is.EqualTo(SessionState.Lobby));
            Assert.That(returns, Is.EqualTo(1));
            Assert.That(h.Host.Players.Select(p => p.Status), Is.All.EqualTo(LobbyPlayerStatus.Idle));
            Assert.That(a.Players.Select(p => p.Status), Is.All.EqualTo(LobbyPlayerStatus.Idle));
        }
    }
}
