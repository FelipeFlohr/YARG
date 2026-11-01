using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace YARG.Relay
{
    /// <summary>
    /// Serves one WebSocket: reads frames, answers control messages and forwards data.
    /// </summary>
    public sealed class RelayHandler
    {
        private static readonly TimeSpan WRITER_DRAIN_TIMEOUT = TimeSpan.FromSeconds(5);

        private readonly RoomRegistry _registry;
        private readonly RelayOptions _options;

        public RelayHandler(RoomRegistry registry, RelayOptions options)
        {
            _registry = registry;
            _options = options;
        }

        public async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string ip = GetClientIp(context);
            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = _options.PingInterval,
                KeepAliveTimeout = _options.PingTimeout,
            });

            var connection = new RelayConnection(socket, ip, _options);
            var writer = connection.RunWriterAsync();
            try
            {
                await ReadLoop(socket, connection, context.RequestAborted);
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException ||
                e is IOException || e is ObjectDisposedException)
            {
                // The socket went away
            }
            finally
            {
                _registry.Leave(connection);
                connection.CloseAfterSending();
                if (await Task.WhenAny(writer, Task.Delay(WRITER_DRAIN_TIMEOUT)) != writer)
                {
                    connection.Abort();
                }
            }
        }

        private string GetClientIp(HttpContext context)
        {
            if (_options.TrustForwardedFor)
            {
                string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
                int comma = forwarded.IndexOf(',');
                string first = (comma >= 0 ? forwarded.Substring(0, comma) : forwarded).Trim();
                if (first.Length > 0)
                {
                    return first;
                }
            }
            return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private async Task ReadLoop(WebSocket socket, RelayConnection connection, CancellationToken cancellation)
        {
            int capacity = Math.Max(_options.MaxBinaryFrameBytes, _options.MaxTextFrameBytes) + 1;
            var buffer = new byte[Math.Min(64 * 1024, capacity)];
            while (true)
            {
                int count = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                    {
                        Array.Resize(ref buffer, Math.Min(buffer.Length * 2, capacity));
                    }

                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count),
                        cancellation);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    count += result.Count;
                    int limit = result.MessageType == WebSocketMessageType.Text
                        ? _options.MaxTextFrameBytes
                        : _options.MaxBinaryFrameBytes;
                    if (count > limit)
                    {
                        connection.SendError(RoomRegistry.TOO_LARGE);
                        connection.CloseAfterSending(WebSocketCloseStatus.MessageTooBig);
                        return;
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControl(connection, buffer.AsSpan(0, count));
                }
                else if (count >= 2)
                {
                    var frame = buffer.AsSpan(0, count).ToArray();
                    var destination = _registry.Route(connection, frame);
                    if (destination != null)
                    {
                        await destination.SendDataAsync(frame);
                    }
                }
            }
        }

        private void HandleControl(RelayConnection connection, ReadOnlySpan<byte> json)
        {
            string? error;
            try
            {
                using var document = JsonDocument.Parse(json.ToArray());
                var root = document.RootElement;
                error = GetString(root, "type") switch
                {
                    "list"   => List(connection),
                    "create" => Create(connection, root),
                    "join"   => Join(connection, root),
                    "kick"   => root.TryGetProperty("peer", out var peer) && peer.TryGetInt32(out int peerId)
                        ? _registry.Kick(connection, peerId)
                        : RoomRegistry.BAD_REQUEST,
                    "state"  => root.TryGetProperty("playing", out var playing) &&
                        (playing.ValueKind == JsonValueKind.True || playing.ValueKind == JsonValueKind.False)
                        ? _registry.SetPlaying(connection, playing.GetBoolean())
                        : RoomRegistry.BAD_REQUEST,
                    _        => RoomRegistry.BAD_REQUEST,
                };
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException)
            {
                error = RoomRegistry.BAD_REQUEST;
            }

            if (error != null)
            {
                connection.SendError(error);
            }
        }

        private string? List(RelayConnection connection)
        {
            connection.SendJson(new { type = "rooms", rooms = _registry.List() });
            return null;
        }

        private string? Create(RelayConnection connection, JsonElement root)
        {
            string? name = GetString(root, "name")?.Trim();
            string? password = GetString(root, "password");
            if (!IsValidText(name, _options.MaxNameLength) || !IsValidText(password, _options.MaxPasswordLength) ||
                !root.TryGetProperty("max", out var maxElement) || !maxElement.TryGetInt32(out int max) ||
                max < 2 || max > _options.MaxPlayers)
            {
                return RoomRegistry.BAD_REQUEST;
            }

            return _registry.Create(connection, name!, password!, max);
        }

        private string? Join(RelayConnection connection, JsonElement root)
        {
            string? room = GetString(root, "room");
            string? password = GetString(root, "password");
            if (string.IsNullOrEmpty(room) || password == null)
            {
                return RoomRegistry.BAD_REQUEST;
            }

            return _registry.Join(connection, room, password);
        }

        private static bool IsValidText(string? text, int maxLength) =>
            !string.IsNullOrEmpty(text) && text.Length <= maxLength;

        private static string? GetString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
