# YARG.Relay

WebSocket relay for the web build's online multiplayer. It keeps password rooms in memory and forwards frames
between a room's host and its clients; it doesn't understand the game protocol. Design:
`docs/superpowers/specs/2026-11-01-web-port-5-multiplayer-design.md`.

- `/ws`: the WebSocket endpoint (JSON control messages, binary `[peerId u16 LE][payload]` data).
- `/health`: answers `ok`.

Nothing is written to disk. Restarting the relay closes every room.

## Running locally

```bash
dotnet run --project Server/YARG.Relay -- --urls http://127.0.0.1:8090
```

Open the web build with `?relay=ws://127.0.0.1:8090/ws` to use it (by default the game uses `/ws` on the page's
own origin). Tests: `cd Tests/YARG.Relay.Tests && dotnet test`.

## Settings

Every limit is a `Relay:*` setting (see `RelayOptions.cs`), e.g. as environment variables:

| Variable | Default |
|---|---|
| `Relay__MaxRoomsPerIp` | `3` |
| `Relay__WrongPasswordLimit` / `Relay__WrongPasswordWindow` | `5` / `00:01:00` |
| `Relay__IdleTimeout` | `00:30:00` |
| `Relay__PingInterval` / `Relay__PingTimeout` | `00:00:10` / `00:00:30` |
| `Relay__MaxBinaryFrameBytes` | `1048578` (1 MiB + header) |
| `Relay__TrustForwardedFor` | `false` (`true` in `deploy/compose.yaml`, behind Caddy) |

Only turn on `TrustForwardedFor` behind a proxy that replaces `X-Forwarded-For`; otherwise a client can pick its
own IP and dodge the per-IP limits.

## Deploying

See [`deploy/README.md`](../../deploy/README.md): one `docker compose up -d --build` in `deploy/` builds this relay
from source (this folder's `Dockerfile`) and runs it behind Caddy, which serves the WebGL build and proxies `/ws`
on the same origin, with HTTPS on the Tailscale tailnet.
