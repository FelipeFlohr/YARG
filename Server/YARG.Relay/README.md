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
| `Relay__TrustForwardedFor` | `false` (`true` in `docker-compose.yml`, behind Caddy) |

Only turn on `TrustForwardedFor` behind a proxy that replaces `X-Forwarded-For`; otherwise a client can pick its
own IP and dodge the per-IP limits.

## Deploying on the VPS

The VPS runs two containers: Caddy (HTTPS, the static WebGL build, and `/ws` proxied to the relay, all on one
origin) and the relay.

1. Point the domain's DNS (A/AAAA) at the VPS and open ports 80 and 443 (TCP, and UDP 443 for HTTP/3).
2. Install Docker with the compose plugin.
3. Copy this folder to the VPS (e.g. `/opt/yarg`), and the WebGL build (the folder with `index.html`, `Build/`
   and `StreamingAssets/`, made by `Editor.WebBuild.Build`; never a `-webDev` build) to `/opt/yarg/web`:

   ```bash
   rsync -a Server/YARG.Relay/ vps:/opt/yarg/
   rsync -a --delete ~/yarg-build-web/ vps:/opt/yarg/web/
   ```

4. Start it, with the domain in `YARG_DOMAIN` (Caddy gets the certificate on the first request):

   ```bash
   cd /opt/yarg
   YARG_DOMAIN=yarg.example.com docker compose up -d --build
   ```

   Put `YARG_DOMAIN=…` in `/opt/yarg/.env` to avoid typing it each time. `YARG_WEB_BUILD` changes the build folder.

5. Check: `curl https://yarg.example.com/ws` answers 400 (not a WebSocket request), the page loads, and the online
   menu lists rooms.

Updating:

- New WebGL build: rsync it to `/opt/yarg/web` again; Caddy serves the new files right away (`Cache-Control:
  no-cache`).
- New relay: copy the folder again and `docker compose up -d --build relay`. Open rooms are lost.

Logs: `docker compose logs -f relay` (room created/closed, with the creator's IP) and `docker compose logs caddy`.
