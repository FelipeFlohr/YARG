using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace YARG.Relay.Tests
{
    public class RelayTests
    {
        private RelayTestServer _server = null!;

        [SetUp]
        public async Task SetUp()
        {
            _server = await RelayTestServer.StartAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            await _server.DisposeAsync();
        }

        private Task<RawRelayClient> Connect(string ip = "10.0.0.1") =>
            RawRelayClient.ConnectAsync(_server.WebSocketUri, ip);

        private static string Type(JsonElement message) => message.GetProperty("type").GetString()!;

        private static void AssertError(JsonElement message, string code)
        {
            Assert.That(Type(message), Is.EqualTo("error"), message.ToString());
            Assert.That(message.GetProperty("code").GetString(), Is.EqualTo(code));
        }

        private static async Task AssertClosedWith(RawRelayClient client, string reason)
        {
            var message = await client.ReceiveJsonAsync();
            Assert.That(Type(message), Is.EqualTo("closed"), message.ToString());
            Assert.That(message.GetProperty("reason").GetString(), Is.EqualTo(reason));
            Assert.That((await client.ReceiveAsync()).IsClose, Is.True);
        }

        [Test]
        public async Task List_IsEmptyAtFirst()
        {
            await using var client = await Connect();
            var reply = await client.RequestAsync(new { type = "list" });
            Assert.That(Type(reply), Is.EqualTo("rooms"));
            Assert.That(reply.GetProperty("rooms").GetArrayLength(), Is.EqualTo(0));
        }

        [Test]
        public async Task Create_ShowsTheRoomLockedWithItsHost()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync("Amigos", "abc", 3);

            await using var browser = await Connect("10.0.0.2");
            var rooms = (await browser.RequestAsync(new { type = "list" })).GetProperty("rooms");
            Assert.That(rooms.GetArrayLength(), Is.EqualTo(1));
            var entry = rooms[0];
            Assert.That(entry.GetProperty("id").GetString(), Is.EqualTo(room));
            Assert.That(entry.GetProperty("name").GetString(), Is.EqualTo("Amigos"));
            Assert.That(entry.GetProperty("players").GetInt32(), Is.EqualTo(1));
            Assert.That(entry.GetProperty("max").GetInt32(), Is.EqualTo(3));
            Assert.That(entry.GetProperty("playing").GetBoolean(), Is.False);
            Assert.That(entry.GetProperty("locked").GetBoolean(), Is.True);
            Assert.That(entry.TryGetProperty("password", out _), Is.False);
        }

        [Test]
        public async Task Join_WithTheRightPassword_TellsTheHost()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var a = await Connect("10.0.0.2");
            await using var b = await Connect("10.0.0.3");

            Assert.That(await a.JoinAsync(room), Is.EqualTo(1));
            var joined = await host.ReceiveJsonAsync();
            Assert.That(Type(joined), Is.EqualTo("peer_joined"));
            Assert.That(joined.GetProperty("peer").GetInt32(), Is.EqualTo(1));

            Assert.That(await b.JoinAsync(room), Is.EqualTo(2));
            Assert.That((await host.ReceiveJsonAsync()).GetProperty("peer").GetInt32(), Is.EqualTo(2));

            var rooms = (await a.RequestAsync(new { type = "list" })).GetProperty("rooms");
            Assert.That(rooms[0].GetProperty("players").GetInt32(), Is.EqualTo(3));
        }

        [Test]
        public async Task Join_WithTheWrongPassword_IsRefused()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync(password: "certa");
            await using var client = await Connect("10.0.0.2");

            AssertError(await client.RequestAsync(new { type = "join", room, password = "errada" }), "wrong_password");
            // Still free to try again
            Assert.That(await client.JoinAsync(room, "certa"), Is.EqualTo(1));
        }

        [Test]
        public async Task Join_UnknownRoom_IsNotFound()
        {
            await using var client = await Connect();
            AssertError(await client.RequestAsync(new { type = "join", room = "nope", password = "x" }), "not_found");
        }

        [Test]
        public async Task Join_FullRoom_IsRefused()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync(max: 2);
            await using var a = await Connect("10.0.0.2");
            await using var b = await Connect("10.0.0.3");

            await a.JoinAsync(room);
            AssertError(await b.RequestAsync(new { type = "join", room, password = "segredo" }), "full");
        }

        [Test]
        public async Task WrongPasswords_AreRateLimitedPerIp()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync(password: "certa");
            await using var guesser = await Connect("10.0.0.2");

            for (int i = 0; i < 5; i++)
            {
                AssertError(await guesser.RequestAsync(new { type = "join", room, password = $"chute{i}" }),
                    "wrong_password");
            }
            // Even the right password, until the window passes
            AssertError(await guesser.RequestAsync(new { type = "join", room, password = "certa" }), "rate_limited");

            // Another socket from the same IP is limited too, another IP isn't
            await using var sameIp = await Connect("10.0.0.2");
            AssertError(await sameIp.RequestAsync(new { type = "join", room, password = "certa" }), "rate_limited");
            await using var otherIp = await Connect("10.0.0.3");
            Assert.That(await otherIp.JoinAsync(room, "certa"), Is.EqualTo(1));
        }

        [Test]
        public async Task RateLimit_ExpiresWithTheWindow()
        {
            await _server.DisposeAsync();
            _server = await RelayTestServer.StartAsync(options =>
            {
                options.WrongPasswordLimit = 2;
                options.WrongPasswordWindow = TimeSpan.FromMilliseconds(500);
            });

            await using var host = await Connect();
            string room = await host.CreateAsync(password: "certa");
            await using var guesser = await Connect("10.0.0.2");
            for (int i = 0; i < 2; i++)
            {
                AssertError(await guesser.RequestAsync(new { type = "join", room, password = "x" }), "wrong_password");
            }
            AssertError(await guesser.RequestAsync(new { type = "join", room, password = "certa" }), "rate_limited");

            await Task.Delay(700);
            Assert.That(await guesser.JoinAsync(room, "certa"), Is.EqualTo(1));
        }

        [Test]
        public async Task Create_IsLimitedToThreeRoomsPerIp()
        {
            var hosts = new RawRelayClient[3];
            for (int i = 0; i < hosts.Length; i++)
            {
                hosts[i] = await Connect();
                await hosts[i].CreateAsync($"Sala{i}");
            }

            await using var fourth = await Connect();
            AssertError(await fourth.RequestAsync(new { type = "create", name = "Sala3", password = "x", max = 2 }),
                "too_many_rooms");

            await using var otherIp = await Connect("10.0.0.9");
            await otherIp.CreateAsync("Outra");

            // Closing one frees a slot
            await hosts[0].CloseAsync();
            await Task.Delay(200);
            await fourth.CreateAsync("Sala3");

            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }

        [Test]
        public async Task Create_TwiceOnOneSocket_IsRefused()
        {
            await using var host = await Connect();
            await host.CreateAsync();
            AssertError(await host.RequestAsync(new { type = "create", name = "B", password = "x", max = 2 }),
                "already_in_room");
        }

        [TestCase("{\"type\":\"create\",\"name\":\"\",\"password\":\"x\",\"max\":2}")]
        [TestCase("{\"type\":\"create\",\"name\":\"A\",\"password\":\"\",\"max\":2}")]
        [TestCase("{\"type\":\"create\",\"name\":\"A\",\"password\":\"x\",\"max\":1}")]
        [TestCase("{\"type\":\"create\",\"name\":\"A\",\"password\":\"x\",\"max\":9}")]
        [TestCase("{\"type\":\"create\",\"name\":\"A\",\"password\":\"x\"}")]
        [TestCase("{\"type\":\"create\",\"name\":\"0123456789012345678901234567890123\",\"password\":\"x\",\"max\":2}")]
        [TestCase("{\"type\":\"join\",\"room\":\"abc\"}")]
        [TestCase("{\"type\":\"dance\"}")]
        [TestCase("[1,2]")]
        [TestCase("not json")]
        public async Task BadRequests_AreAnsweredWithAnError(string text)
        {
            await using var client = await Connect();
            await client.SendTextAsync(text);
            AssertError(await client.ReceiveJsonAsync(), "bad_request");

            // The socket stays usable
            Assert.That(Type(await client.RequestAsync(new { type = "list" })), Is.EqualTo("rooms"));
        }

        [Test]
        public async Task KickAndState_AreHostOnly()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);

            AssertError(await client.RequestAsync(new { type = "kick", peer = 1 }), "not_host");
            AssertError(await client.RequestAsync(new { type = "state", playing = true }), "not_host");
        }

        [Test]
        public async Task Data_FromAClient_ReachesTheHostTaggedWithTheClient()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var a = await Connect("10.0.0.2");
            await using var b = await Connect("10.0.0.3");
            await a.JoinAsync(room);
            await b.JoinAsync(room);
            await host.ReceiveJsonAsync();
            await host.ReceiveJsonAsync();

            // The peer ID a client writes is ignored
            await b.SendBinaryAsync(7, new byte[] { 1, 2, 3 });
            var (from, payload) = await host.ReceiveDataAsync();
            Assert.That(from, Is.EqualTo(2));
            Assert.That(payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public async Task Data_FromTheHost_ReachesOnlyTheNamedClient()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var a = await Connect("10.0.0.2");
            await using var b = await Connect("10.0.0.3");
            await a.JoinAsync(room);
            await b.JoinAsync(room);

            await host.SendBinaryAsync(2, new byte[] { 9 });
            await host.SendBinaryAsync(99, new byte[] { 8 }); // nobody: dropped
            await host.SendBinaryAsync(1, new byte[] { 7 });

            var (fromB, payloadB) = await b.ReceiveDataAsync();
            Assert.That(fromB, Is.EqualTo(0));
            Assert.That(payloadB, Is.EqualTo(new byte[] { 9 }));
            var (fromA, payloadA) = await a.ReceiveDataAsync();
            Assert.That(fromA, Is.EqualTo(0));
            Assert.That(payloadA, Is.EqualTo(new byte[] { 7 }));
        }

        [Test]
        public async Task Data_KeepsItsOrderAndSize()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            var big = new byte[1024 * 1024];
            new Random(1).NextBytes(big);
            for (int i = 0; i < 3; i++)
            {
                big[0] = (byte) i;
                await client.SendBinaryAsync(0, big);
            }
            for (int i = 0; i < 3; i++)
            {
                var (_, payload) = await host.ReceiveDataAsync();
                Assert.That(payload.Length, Is.EqualTo(big.Length));
                Assert.That(payload[0], Is.EqualTo(i));
            }
        }

        [Test]
        public async Task Data_OutsideARoom_IsDropped()
        {
            await using var client = await Connect();
            await client.SendBinaryAsync(0, new byte[] { 1 });
            Assert.That(Type(await client.RequestAsync(new { type = "list" })), Is.EqualTo("rooms"));
        }

        [Test]
        public async Task TooLargeFrame_IsRefusedAndClosed()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            await client.SendBinaryAsync(0, new byte[1024 * 1024 + 1]);
            AssertError(await client.ReceiveJsonAsync(), "too_large");
            var close = await client.ReceiveAsync();
            Assert.That(close.IsClose, Is.True);
            Assert.That(close.CloseStatus, Is.EqualTo(WebSocketCloseStatus.MessageTooBig));

            var left = await host.ReceiveJsonAsync();
            Assert.That(Type(left), Is.EqualTo("peer_left"));
        }

        [Test]
        public async Task LargestAllowedFrame_GoesThrough()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            await client.SendBinaryAsync(0, new byte[1024 * 1024]);
            var (_, payload) = await host.ReceiveDataAsync();
            Assert.That(payload.Length, Is.EqualTo(1024 * 1024));
        }

        [Test]
        public async Task HostLeaving_ClosesTheRoomForEveryone()
        {
            var host = await Connect();
            string room = await host.CreateAsync();
            await using var a = await Connect("10.0.0.2");
            await using var b = await Connect("10.0.0.3");
            await a.JoinAsync(room);
            await b.JoinAsync(room);

            await host.CloseAsync();
            await host.DisposeAsync();

            await AssertClosedWith(a, "host_left");
            await AssertClosedWith(b, "host_left");

            await using var browser = await Connect();
            Assert.That((await browser.RequestAsync(new { type = "list" })).GetProperty("rooms").GetArrayLength(),
                Is.EqualTo(0));
            AssertError(await browser.RequestAsync(new { type = "join", room, password = "segredo" }), "not_found");
        }

        [Test]
        public async Task HostDropping_ClosesTheRoomToo()
        {
            var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);

            // No close handshake
            await host.DisposeAsync();
            await AssertClosedWith(client, "host_left");
        }

        [Test]
        public async Task ClientLeaving_TellsTheHostAndFreesTheSeat()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync(max: 2);
            var a = await Connect("10.0.0.2");
            await a.JoinAsync(room);
            await host.ReceiveJsonAsync();

            await a.CloseAsync();
            await a.DisposeAsync();
            var left = await host.ReceiveJsonAsync();
            Assert.That(Type(left), Is.EqualTo("peer_left"));
            Assert.That(left.GetProperty("peer").GetInt32(), Is.EqualTo(1));

            await using var b = await Connect("10.0.0.3");
            Assert.That(await b.JoinAsync(room), Is.EqualTo(2));
        }

        [Test]
        public async Task Kick_ClosesTheClientAndTellsTheHost()
        {
            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            await host.SendJsonAsync(new { type = "kick", peer = 1 });
            await AssertClosedWith(client, "kicked");
            var left = await host.ReceiveJsonAsync();
            Assert.That(Type(left), Is.EqualTo("peer_left"));
        }

        [Test]
        public async Task State_ShowsInTheList()
        {
            await using var host = await Connect();
            await host.CreateAsync();
            await host.SendJsonAsync(new { type = "state", playing = true });

            await using var browser = await Connect();
            var rooms = (await browser.RequestAsync(new { type = "list" })).GetProperty("rooms");
            Assert.That(rooms[0].GetProperty("playing").GetBoolean(), Is.True);
        }

        [Test]
        public async Task IdleRoom_IsClosed()
        {
            await _server.DisposeAsync();
            _server = await RelayTestServer.StartAsync(options =>
            {
                options.IdleTimeout = TimeSpan.FromMilliseconds(500);
                options.IdleCheckInterval = TimeSpan.FromMilliseconds(100);
            });

            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            await AssertClosedWith(client, "idle");
            await AssertClosedWith(host, "idle");
        }

        [Test]
        public async Task ActiveRoom_IsNotClosedForIdleness()
        {
            await _server.DisposeAsync();
            _server = await RelayTestServer.StartAsync(options =>
            {
                options.IdleTimeout = TimeSpan.FromMilliseconds(600);
                options.IdleCheckInterval = TimeSpan.FromMilliseconds(100);
            });

            await using var host = await Connect();
            string room = await host.CreateAsync();
            await using var client = await Connect("10.0.0.2");
            await client.JoinAsync(room);
            await host.ReceiveJsonAsync();

            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(200);
                await client.SendBinaryAsync(0, new byte[] { (byte) i });
                var (_, payload) = await host.ReceiveDataAsync();
                Assert.That(payload[0], Is.EqualTo(i));
            }
        }

        [Test]
        public void PasswordHasher_VerifiesOnlyTheSamePassword()
        {
            var hasher = new PasswordHasher("senha", 10);
            Assert.That(hasher.Verify("senha"), Is.True);
            Assert.That(hasher.Verify("Senha"), Is.False);
            Assert.That(hasher.Verify(""), Is.False);
            Assert.That(hasher.Verify("senha "), Is.False);
        }

        [Test]
        public void WrongPasswordLimiter_SweepForgetsExpiredIps()
        {
            var now = new DateTime(2026, 1, 1);
            var limiter = new WrongPasswordLimiter(2, TimeSpan.FromMinutes(1), () => now);
            limiter.RecordFailure("a");
            limiter.RecordFailure("a");
            Assert.That(limiter.IsLimited("a"), Is.True);
            Assert.That(limiter.IsLimited("b"), Is.False);

            now = now.AddSeconds(61);
            limiter.Sweep();
            Assert.That(limiter.IsLimited("a"), Is.False);
        }
    }
}
