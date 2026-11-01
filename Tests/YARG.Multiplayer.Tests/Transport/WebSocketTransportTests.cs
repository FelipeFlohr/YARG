using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Song;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.Transport;
using YARG.Relay;

namespace YARG.Multiplayer.Tests.Transport
{
    /// <summary>
    /// <see cref="WebSocketTransport"/> and real sessions against the real relay, in-process on Kestrel.
    /// </summary>
    public class WebSocketTransportTests
    {
        private const string VERSION = "v0.15";
        private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);

        private WebApplication _relay = null!;
        private string         _url   = null!;
        private readonly List<IDisposable> _disposables = new();
        private int _nextIp = 1;

        [SetUp]
        public async Task SetUp()
        {
            _relay = RelayApp.Build(new[] { "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning" },
                options =>
                {
                    options.PasswordIterations = 10;
                    options.TrustForwardedFor = true;
                });
            await _relay.StartAsync();
            _url = "ws" + _relay.Urls.First().Substring("http".Length) + "/ws";
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
            _disposables.Clear();
            await _relay.StopAsync();
            await _relay.DisposeAsync();
        }

        /// <summary>
        /// A transport that looks like it's on its own machine (each gets its own IP for the per-IP limits).
        /// </summary>
        private WebSocketTransport NewTransport()
        {
            var transport = new WebSocketTransport(new ClientWebSocketAdapter($"10.0.0.{_nextIp++}"), _url);
            _disposables.Add(transport);
            return transport;
        }

        private static async Task<T> Await<T>(WebSocketTransport transport, Task<T> task)
        {
            var deadline = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (deadline.Elapsed > TIMEOUT)
                {
                    Assert.Fail("timed out waiting for the relay");
                }
                transport.Pump();
                await Task.Delay(2);
            }
            return await task;
        }

        private static async Task Await(WebSocketTransport transport, Task task)
        {
            await Until(() => task.IsCompleted, transport.Pump, "the relay");
            await task;
        }

        private static async Task Until(Func<bool> condition, Action tick, string what)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.Elapsed > TIMEOUT)
                {
                    Assert.Fail($"timed out waiting for {what}");
                }
                tick();
                await Task.Delay(2);
            }
        }

        private sealed class Events : INetEventListener
        {
            public readonly List<string> Log = new();
            public readonly List<(int Peer, byte[] Message)> Messages = new();

            public void OnPeerConnected(int peerId) => Log.Add($"connected {peerId}");

            public void OnPeerDisconnected(int peerId, string reason) => Log.Add($"disconnected {peerId}: {reason}");

            public void OnMessage(int peerId, byte[] message) => Messages.Add((peerId, message));
        }

        [Test]
        public async Task RoomRequests_WorkThroughTheRelay()
        {
            var host = NewTransport();
            // Asked before the socket opened: queued until it does
            string room = await Await(host, host.CreateRoomAsync("Amigos", "senha", 3));
            Assert.That(host.IsHost, Is.True);
            Assert.That(host.RoomId, Is.EqualTo(room));

            var browser = NewTransport();
            var rooms = await Await(browser, browser.ListRoomsAsync());
            Assert.That(rooms.Select(r => (r.Id, r.Name, r.Players, r.MaxPlayers, r.Playing)),
                Is.EqualTo(new[] { (room, "Amigos", 1, 3, false) }));

            var wrong = Assert.ThrowsAsync<RelayException>(() => Await(browser, browser.JoinRoomAsync(room, "nope")));
            Assert.That(wrong!.Code, Is.EqualTo(RelayException.WRONG_PASSWORD));
            Assert.That(browser.IsInRoom, Is.False);

            await Await(browser, browser.JoinRoomAsync(room, "senha"));
            Assert.That(browser.IsInRoom, Is.True);
            Assert.That(browser.LocalPeerId, Is.EqualTo(1));

            host.SetRoomPlaying(true);
            // Another socket, so the state message may land after its first list
            var other = NewTransport();
            for (int i = 0; i < 50 && !rooms.Single().Playing; i++)
            {
                rooms = await Await(other, other.ListRoomsAsync());
            }
            Assert.That(rooms.Single().Playing, Is.True);
            Assert.That(rooms.Single().Players, Is.EqualTo(2));
        }

        [Test]
        public void SessionMethods_RequireARoom()
        {
            var transport = NewTransport();
            Assert.Throws<InvalidOperationException>(() => transport.StartHost(0));
            Assert.Throws<InvalidOperationException>(() => transport.Connect("ignored", 0));
        }

        [Test]
        public async Task Requests_FailWhenTheRelayIsUnreachable()
        {
            var transport = new WebSocketTransport(new ClientWebSocketAdapter(), "ws://127.0.0.1:1/ws");
            _disposables.Add(transport);
            var error = Assert.ThrowsAsync<RelayException>(() => Await(transport, transport.ListRoomsAsync()));
            Assert.That(error!.Code, Is.EqualTo(RelayException.CONNECTION_LOST));
            await Task.CompletedTask;
        }

        [Test]
        public async Task Messages_AreRoutedBetweenHostAndClients()
        {
            var host = NewTransport();
            string room = await Await(host, host.CreateRoomAsync("Sala", "x", 4));
            host.StartHost(0);
            var a = NewTransport();
            var b = NewTransport();
            await Await(a, a.JoinRoomAsync(room, "x"));
            await Await(b, b.JoinRoomAsync(room, "x"));
            a.Connect("ignored", 0);
            b.Connect("ignored", 0);

            var hostEvents = new Events();
            var aEvents = new Events();
            var bEvents = new Events();
            void PollAll()
            {
                host.Poll(hostEvents);
                a.Poll(aEvents);
                b.Poll(bEvents);
            }

            await Until(() => hostEvents.Log.Count == 2, PollAll, "both clients");
            Assert.That(hostEvents.Log, Is.EqualTo(new[] { "connected 1", "connected 2" }));
            Assert.That(aEvents.Log, Is.EqualTo(new[] { "connected 0" }));

            a.Send(0, new byte[] { 1 });
            b.Send(0, new byte[] { 2 });
            host.Send(2, new byte[] { 3 });
            host.Send(1, new byte[] { 4 });
            await Until(() => hostEvents.Messages.Count == 2 && aEvents.Messages.Count == 1 &&
                bEvents.Messages.Count == 1, PollAll, "the messages");

            Assert.That(hostEvents.Messages.OrderBy(m => m.Peer).Select(m => (m.Peer, m.Message[0])),
                Is.EqualTo(new[] { (1, (byte) 1), (2, (byte) 2) }));
            Assert.That(aEvents.Messages.Single().Message, Is.EqualTo(new byte[] { 4 }));
            Assert.That(bEvents.Messages.Single().Message, Is.EqualTo(new byte[] { 3 }));
            Assert.That(aEvents.Messages.Single().Peer, Is.EqualTo(0));

            // The host drops one client; the other stays
            host.Disconnect(1);
            await Until(() => aEvents.Log.Count == 2, PollAll, "the kicked client to be closed");
            Assert.That(aEvents.Log[1], Does.StartWith("disconnected 0: removed by the host"));
            Assert.That(hostEvents.Log.Last(), Is.EqualTo("disconnected 1: disconnected locally"));

            // The host leaves: the remaining client is told why
            host.Dispose();
            await Until(() => bEvents.Log.Count == 2, () => b.Poll(bEvents), "the room to close");
            Assert.That(bEvents.Log[1], Is.EqualTo("disconnected 0: the host left"));
        }

        [Test]
        public async Task TooLargeMessages_AreRefusedBeforeSending()
        {
            var host = NewTransport();
            await Await(host, host.CreateRoomAsync("Sala", "x", 2));
            Assert.Throws<ArgumentException>(() => host.Send(1, new byte[ProtocolInfo.MaxMessageSize + 1]));
        }

        private sealed class Match : IDisposable
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public HostSession                 Host       = null!;
            public WebSocketTransport          HostTransport = null!;
            public readonly List<ClientSession> Clients    = new();

            public double Now => _clock.Elapsed.TotalSeconds;

            public SessionConfig Config(string name) =>
                new(VERSION, name, timeoutSeconds: 10, connectTimeoutSeconds: 10);

            public void Tick()
            {
                Host.Tick();
                foreach (var client in Clients)
                {
                    client.Tick();
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

        private async Task<Match> StartMatch(params string[] clientNames)
        {
            var match = new Match();
            var hostTransport = NewTransport();
            string room = await Await(hostTransport, hostTransport.CreateRoomAsync("Banda", "senha", 4));
            match.HostTransport = hostTransport;
            match.Host = new HostSession(hostTransport, match.Config("Host"), () => match.Now);
            match.Host.Start(ProtocolInfo.DefaultPort);

            foreach (string name in clientNames)
            {
                var transport = NewTransport();
                await Await(transport, transport.JoinRoomAsync(room, "senha"));
                var client = new ClientSession(transport, match.Config(name), () => match.Now);
                client.Connect("ignored", ProtocolInfo.DefaultPort);
                match.Clients.Add(client);
            }

            await Until(() => match.Clients.All(c => c.LocalPlayerId > 0) &&
                match.Clients.All(c => c.Players.Count == clientNames.Length + 1), match.Tick, "everybody to join");
            return match;
        }

        [Test]
        public async Task Sessions_HostAndTwoClients_JoinAndTransferASong()
        {
            using var match = await StartMatch("Ana", "Bia");
            var ana = match.Clients[0];
            var bia = match.Clients[1];
            Assert.That(match.Host.Players.Select(p => p.Name), Is.EquivalentTo(new[] { "Host", "Ana", "Bia" }));
            Assert.That(ana.LocalPlayerId, Is.Not.EqualTo(bia.LocalPlayerId));

            // Bigger than the send backlog limit, so the transfer has to wait on BufferedAmount
            var data = new byte[3 * 1024 * 1024 + 123];
            new Random(5).NextBytes(data);
            var offer = new SongOffer(HashWrapper.Hash(data), "Song", "Artist", SongPackageKind.Sng, data.Length, 1f);

            using var received = new MemoryStream();
            ana.SongOffered += _ => ana.RespondToOffer(false);
            ana.SongChunkReceived += chunk =>
            {
                Assert.That(chunk.Offset, Is.EqualTo(received.Length));
                received.Write(chunk.Data, 0, chunk.Data.Length);
                if (received.Length == data.Length)
                {
                    ana.ReportSongReady();
                }
            };
            // Bia already has it
            bia.SongOffered += _ => bia.RespondToOffer(true);

            match.Host.OfferSong(offer, () => new MemoryStream(data));
            match.Host.SetLocalPart(Instrument.FiveFretBass, Difficulty.Hard);
            ana.SetLocalPart(Instrument.FiveFretGuitar, Difficulty.Expert);
            bia.SetLocalPart(Instrument.Keys, Difficulty.Medium);

            await Until(() => match.Host.CanBeginLoad, match.Tick, "everybody to have the song");
            Assert.That(received.ToArray(), Is.EqualTo(data));
            Assert.That(match.Host.Players.Where(p => p.PlayerId != 0).Select(p => p.Status),
                Is.All.EqualTo(LobbyPlayerStatus.HasSong));

            // On to the match: everybody loads and gets the same start time
            match.Host.BeginLoad();
            await Until(() => match.Clients.All(c => c.State == SessionState.Loading), match.Tick, "loading");
            match.Host.ReportLoaded();
            ana.ReportLoaded();
            bia.ReportLoaded();
            await Until(() => match.Clients.All(c => c.StartTimeLocal.HasValue) && match.Host.StartTimeLocal.HasValue,
                match.Tick, "the start time");
            foreach (var client in match.Clients)
            {
                Assert.That(client.StartTimeLocal!.Value, Is.EqualTo(match.Host.StartTimeLocal!.Value).Within(0.25));
            }

            // Game messages relay from one client to the others through the host
            var biaGot = new List<NetMessage>();
            bia.GameMessageReceived += biaGot.Add;
            ana.SendGameMessage(new StatsCheckpoint(ana.LocalPlayerId, new byte[] { 1 }));
            await Until(() => biaGot.Count > 0, match.Tick, "the relayed game message");
            Assert.That(biaGot.OfType<StatsCheckpoint>().Single().PlayerId, Is.EqualTo(ana.LocalPlayerId));
        }

        [Test]
        public async Task Sessions_HostLeaving_EndsTheClients()
        {
            using var match = await StartMatch("Ana", "Bia");
            var reasons = new List<string>();
            foreach (var client in match.Clients)
            {
                client.Ended += reasons.Add;
            }

            match.Host.Leave();
            await Until(() => reasons.Count == 2, () =>
            {
                foreach (var client in match.Clients)
                {
                    client.Tick();
                }
            }, "both clients to end");
            Assert.That(match.Clients.Select(c => c.State), Is.All.EqualTo(SessionState.Ended));
        }

        [Test]
        public async Task Sessions_ClientLeaving_IsMarkedLeft()
        {
            using var match = await StartMatch("Ana", "Bia");
            var ana = match.Clients[0];
            int anaId = ana.LocalPlayerId;

            ana.Leave();
            match.Clients.Remove(ana);
            ana.Dispose();
            await Until(() => match.Host.Players.All(p => p.PlayerId != anaId) ||
                match.Host.Players.Any(p => p.PlayerId == anaId && p.Status == LobbyPlayerStatus.Left),
                match.Tick, "Ana to be gone");
            await Until(() => match.Clients[0].Players.Count(p => p.Status != LobbyPlayerStatus.Left) == 2,
                match.Tick, "Bia to see it");
        }
    }
}
