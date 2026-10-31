using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using YARG.Multiplayer.Session;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Tests.Transport
{
    public class TransportTests
    {
        private sealed class RecordingListener : INetEventListener
        {
            public readonly List<string> Events = new();
            public readonly List<(int Peer, byte[] Message)> Messages = new();
            public readonly List<(int Peer, string Reason)> Disconnects = new();

            public void OnPeerConnected(int peerId) => Events.Add($"connected {peerId}");

            public void OnPeerDisconnected(int peerId, string reason)
            {
                Events.Add($"disconnected {peerId}");
                Disconnects.Add((peerId, reason));
            }

            public void OnMessage(int peerId, byte[] message)
            {
                Events.Add($"message {peerId}");
                Messages.Add((peerId, message));
            }
        }

        private static void PollUntil(Func<bool> condition, params (INetTransport Transport, INetEventListener Listener)[] targets)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(5))
                {
                    Assert.Fail("Timed out waiting for transport events");
                }
                foreach (var (transport, listener) in targets)
                {
                    transport.Poll(listener);
                }
                Thread.Sleep(1);
            }
        }

        [Test]
        public void Tcp_LoopbackDeliversMessagesInOrder()
        {
            using var host = new TcpTransport();
            using var client = new TcpTransport();
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);

            var random = new Random(1);
            var sent = Enumerable.Range(0, 100).Select(i =>
            {
                var bytes = new byte[random.Next(1024, 60 * 1024)];
                random.NextBytes(bytes);
                return bytes;
            }).ToList();
            foreach (var message in sent)
            {
                client.Send(0, message);
            }

            PollUntil(() => hostEvents.Messages.Count == sent.Count, (host, hostEvents), (client, clientEvents));

            Assert.That(hostEvents.Events.First(), Is.EqualTo("connected 1"));
            Assert.That(hostEvents.Messages.Select(m => m.Peer), Is.All.EqualTo(1));
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.That(hostEvents.Messages[i].Message, Is.EqualTo(sent[i]), $"message {i}");
            }
            Assert.That(clientEvents.Events, Does.Contain("connected 0"));
        }

        [Test]
        public void Tcp_HostRepliesToClient()
        {
            using var host = new TcpTransport();
            using var client = new TcpTransport();
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents), (client, clientEvents));

            host.Send(1, new byte[] { 42 });
            PollUntil(() => clientEvents.Messages.Count == 1, (host, hostEvents), (client, clientEvents));
            Assert.That(clientEvents.Messages[0], Is.EqualTo((0, new byte[] { 42 })));
        }

        [Test]
        public void Tcp_DisconnectStillDeliversQueuedMessages()
        {
            using var host = new TcpTransport();
            using var client = new TcpTransport();
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents), (client, clientEvents));

            host.Send(1, new byte[] { 7 });
            host.Disconnect(1);

            PollUntil(() => clientEvents.Disconnects.Count == 1, (host, hostEvents), (client, clientEvents));
            Assert.That(clientEvents.Messages.Select(m => m.Message), Is.EqualTo(new[] { new byte[] { 7 } }));
            Assert.That(hostEvents.Disconnects, Is.EqualTo(new[] { (1, "disconnected locally") }));
        }

        [Test]
        public void Tcp_PendingSendBytesDrainsToZero()
        {
            using var host = new TcpTransport();
            using var client = new TcpTransport();
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);
            for (int i = 0; i < 20; i++)
            {
                client.Send(0, new byte[60 * 1024]);
            }
            Assert.That(client.PendingSendBytes(0), Is.GreaterThan(0));

            PollUntil(() => hostEvents.Messages.Count == 20, (host, hostEvents), (client, clientEvents));
            PollUntil(() => client.PendingSendBytes(0) == 0, (client, clientEvents));
            Assert.That(client.PendingSendBytes(5), Is.EqualTo(0));
        }

        [Test]
        public void Tcp_ClientDisconnectRaisesPeerDisconnected()
        {
            using var host = new TcpTransport();
            var client = new TcpTransport();
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents), (client, clientEvents));

            client.Dispose();
            PollUntil(() => hostEvents.Disconnects.Count == 1, (host, hostEvents));
            Assert.That(hostEvents.Disconnects[0].Peer, Is.EqualTo(1));
        }

        [Test]
        public void Tcp_ConnectFailureRaisesDisconnectOfHost()
        {
            // Grab a free port, then close it so nothing listens there
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            using var client = new TcpTransport();
            var clientEvents = new RecordingListener();
            client.Connect("127.0.0.1", port);
            PollUntil(() => clientEvents.Disconnects.Count == 1, (client, clientEvents));
            Assert.That(clientEvents.Disconnects[0].Peer, Is.EqualTo(0));
        }

        [Test]
        public void Tcp_OversizedMessageDropsConnection()
        {
            using var host = new TcpTransport();
            var hostEvents = new RecordingListener();
            host.StartHost(0);

            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents));

            raw.GetStream().Write(BitConverter.GetBytes(ProtocolInfo.MaxMessageSize + 1));
            PollUntil(() => hostEvents.Disconnects.Count == 1, (host, hostEvents));
            Assert.That(hostEvents.Disconnects[0], Is.EqualTo((1, "message too large")));
        }

        [Test]
        public void Tcp_SilentPeerTimesOutOnceKeepAliveStarted()
        {
            using var host = new TcpTransport(heartbeatIntervalSeconds: 0.1, timeoutSeconds: 0.5);
            var hostEvents = new RecordingListener();
            host.StartHost(0);

            // A raw socket never sends heartbeats, like a peer whose network went away
            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents));
            host.StartKeepAlive(1);
            // The read in progress started without a timeout; the next one has it
            raw.GetStream().Write(new byte[] { 1, 0, 0, 0, 42 });

            var watch = Stopwatch.StartNew();
            PollUntil(() => hostEvents.Disconnects.Count == 1, (host, hostEvents));
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
            Assert.That(hostEvents.Disconnects[0].Reason, Does.Contain("timed out"));
        }

        [Test]
        public void Tcp_NoHeartbeatsOrTimeoutBeforeKeepAliveStarts()
        {
            // Older builds take an empty frame for a malformed message, so nothing may go out before the handshake
            using var host = new TcpTransport(heartbeatIntervalSeconds: 0.05, timeoutSeconds: 0.3);
            var hostEvents = new RecordingListener();
            host.StartHost(0);

            using var raw = new TcpClient();
            raw.Connect(IPAddress.Loopback, host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1"), (host, hostEvents));

            Thread.Sleep(1000);
            host.Poll(hostEvents);
            Assert.That(raw.Available, Is.EqualTo(0));
            Assert.That(hostEvents.Disconnects, Is.Empty);
        }

        [Test]
        public void Tcp_HeartbeatsKeepConnectionAliveWhileNobodyPolls()
        {
            // Generous limits so a loaded machine that schedules the writer threads late doesn't fail the test
            using var host = new TcpTransport(heartbeatIntervalSeconds: 0.05, timeoutSeconds: 1.5);
            using var client = new TcpTransport(heartbeatIntervalSeconds: 0.05, timeoutSeconds: 1.5);
            var hostEvents = new RecordingListener();
            var clientEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("127.0.0.1", host.LocalPort);
            PollUntil(() => hostEvents.Events.Contains("connected 1") && clientEvents.Events.Contains("connected 0"),
                (host, hostEvents), (client, clientEvents));
            host.StartKeepAlive(1);
            client.StartKeepAlive(0);

            // Both main threads "frozen" (as while loading a song) for over twice the timeout
            Thread.Sleep(3500);
            host.Poll(hostEvents);
            client.Poll(clientEvents);
            Assert.That(hostEvents.Disconnects, Is.Empty);
            Assert.That(clientEvents.Disconnects, Is.Empty);
            // Heartbeats are not messages
            Assert.That(hostEvents.Messages, Is.Empty);
            Assert.That(clientEvents.Messages, Is.Empty);

            client.Send(0, new byte[] { 7 });
            PollUntil(() => hostEvents.Messages.Count == 1, (host, hostEvents), (client, clientEvents));
            Assert.That(hostEvents.Messages[0].Message, Is.EqualTo(new byte[] { 7 }));
        }

        private sealed class KeepAliveSpy : INetTransport
        {
            private readonly INetTransport _inner;
            public readonly List<int> Started = new();

            public KeepAliveSpy(INetTransport inner) => _inner = inner;

            public void StartHost(int port) => _inner.StartHost(port);
            public void Connect(string host, int port) => _inner.Connect(host, port);
            public void Send(int peerId, byte[] message) => _inner.Send(peerId, message);
            public long PendingSendBytes(int peerId) => _inner.PendingSendBytes(peerId);
            public void StartKeepAlive(int peerId) => Started.Add(peerId);
            public void Disconnect(int peerId) => _inner.Disconnect(peerId);
            public void Poll(INetEventListener listener) => _inner.Poll(listener);
            public void Dispose() => _inner.Dispose();
        }

        [Test]
        public void Sessions_StartTheKeepAliveOnlyAfterAnAcceptedHandshake()
        {
            var network = new InMemoryNetwork();
            var hostTransport = new KeepAliveSpy(network.CreateHost());
            using var host = new HostSession(hostTransport, new SessionConfig("v1", "Host"), () => 0);
            host.Start(ProtocolInfo.DefaultPort);

            var goodTransport = new KeepAliveSpy(network.CreateClient());
            using var good = new ClientSession(goodTransport, new SessionConfig("v1", "Ana"), () => 0);
            good.Connect("ignored", ProtocolInfo.DefaultPort);
            var oldTransport = new KeepAliveSpy(network.CreateClient());
            using var old = new ClientSession(oldTransport, new SessionConfig("v0", "Bia"), () => 0);
            old.Connect("ignored", ProtocolInfo.DefaultPort);

            Assert.That(hostTransport.Started, Is.Empty);
            for (int i = 0; i < 4; i++)
            {
                host.Tick();
                good.Tick();
                old.Tick();
            }

            // Only the client that got a Welcome, and only on the host's side for that peer
            Assert.That(goodTransport.Started, Is.EqualTo(new[] { 0 }));
            Assert.That(oldTransport.Started, Is.Empty);
            Assert.That(old.RejectReason, Is.Not.Null);
            Assert.That(hostTransport.Started, Has.Count.EqualTo(1));
        }

        [Test]
        public void InMemory_RoutesBetweenHostAndTwoClients()
        {
            var network = new InMemoryNetwork();
            var host = network.CreateHost();
            var a = network.CreateClient();
            var b = network.CreateClient();
            var hostEvents = new RecordingListener();
            var aEvents = new RecordingListener();
            var bEvents = new RecordingListener();

            host.StartHost(ProtocolInfo.DefaultPort);
            a.Connect("ignored", 0);
            b.Connect("ignored", 0);
            a.Send(0, new byte[] { 1 });
            b.Send(0, new byte[] { 2 });
            host.Send(2, new byte[] { 3 });

            host.Poll(hostEvents);
            a.Poll(aEvents);
            b.Poll(bEvents);

            Assert.That(hostEvents.Events, Is.EqualTo(new[] { "connected 1", "connected 2", "message 1", "message 2" }));
            Assert.That(hostEvents.Messages[0].Message, Is.EqualTo(new byte[] { 1 }));
            Assert.That(hostEvents.Messages[1].Message, Is.EqualTo(new byte[] { 2 }));
            Assert.That(aEvents.Events, Is.EqualTo(new[] { "connected 0" }));
            Assert.That(bEvents.Events, Is.EqualTo(new[] { "connected 0", "message 0" }));
            Assert.That(bEvents.Messages[0].Message, Is.EqualTo(new byte[] { 3 }));
        }

        [Test]
        public void InMemory_DisposeOfHostDisconnectsClients()
        {
            var network = new InMemoryNetwork();
            var host = network.CreateHost();
            var a = network.CreateClient();
            var aEvents = new RecordingListener();
            host.StartHost(0);
            a.Connect("ignored", 0);

            host.Dispose();
            a.Poll(aEvents);

            Assert.That(aEvents.Events, Is.EqualTo(new[] { "connected 0", "disconnected 0" }));
        }

        [Test]
        public void Laggy_DelaysDeliveryAndPreservesOrder()
        {
            double now = 0;
            var network = new InMemoryNetwork();
            var host = new LaggyTransport(network.CreateHost(), () => now, 0.2, 0.1, 7);
            var client = network.CreateClient();
            var hostEvents = new RecordingListener();

            host.StartHost(0);
            client.Connect("ignored", 0);
            for (byte i = 0; i < 50; i++)
            {
                client.Send(0, new[] { i });
            }

            host.Poll(hostEvents);
            Assert.That(hostEvents.Events, Is.Empty);

            now = 0.199;
            host.Poll(hostEvents);
            Assert.That(hostEvents.Events, Is.Empty);

            // Some, but not necessarily all, become available in between
            for (now = 0.2; now <= 0.3; now += 0.01)
            {
                host.Poll(hostEvents);
            }
            now = 0.3;
            host.Poll(hostEvents);

            Assert.That(hostEvents.Messages.Select(m => m.Message[0]), Is.EqualTo(Enumerable.Range(0, 50).Select(i => (byte) i)));
            Assert.That(hostEvents.Events.First(), Is.EqualTo("connected 1"));
        }
    }
}
