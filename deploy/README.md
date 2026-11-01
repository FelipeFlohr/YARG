# Deploying the web build (VPS + Tailscale)

`start.sh` (a checked `docker compose up -d --build`) in this folder runs YARG's browser build for friends on a Tailscale tailnet:

| Service | What it does |
|---|---|
| `relay` | `Server/YARG.Relay` (the multiplayer WebSocket relay), built from source. Only reachable inside the stack. |
| `web` | Caddy. Serves the WebGL build from a bind-mounted folder (`YARG_WEB_BUILD`, default `./volume/web`) with the right headers for Unity's `*.br` files, and proxies `/ws` to the relay on the same origin. Plain HTTP, published on `127.0.0.1:8080` of the host. |
| `tailscale` | (profile `tailnet`) Joins the tailnet as `yarg` and serves `https://yarg.<tailnet>.ts.net` → `http://web:80` with a real certificate. |

The game connects to `/ws` on its own origin (`wss:` on an HTTPS page), so nothing in the build has to know the
address.

## Why HTTPS (and not `http://100.x.y.z`)

Browsers only expose the microphone (`navigator.mediaDevices.getUserMedia`, needed for vocals) and `AudioWorklet`
in a *secure context*: an `https:` page, or `http://localhost`. A tailnet IP over plain HTTP is not one. Measured
with this stack and a test build in headless Chromium (2026-11-01):

| Origin | `isSecureContext` | `navigator.mediaDevices` | `audioWorklet` | Mic test | Other |
|---|---|---|---|---|---|
| `http://localhost:8088` | `true` | present | present | open, 440 Hz detected | — |
| `http://192.168.1.5:8088` (LAN) | `false` | `undefined` | `undefined` | `getUserMedia unavailable` | Unity: `Insecure connection not allowed`, then `RuntimeData is null` (Addressables failed to load) |
| `http://100.68.229.68:8088` (tailnet IP) | `false` | `undefined` | `undefined` | `getUserMedia unavailable` | same as LAN |
| `https://<machine>.<tailnet>.ts.net` (`tailscale serve`) | `true` | present | present | open, 440 Hz detected | — |

Playback still works over plain HTTP (the audio taps fall back to `ScriptProcessor`), but vocals don't, and the
player's "Allow downloads over HTTP: Not allowed" setting makes Unity refuse its own Addressables catalog on a
non-localhost `http:` origin. So: HTTPS.

## Why a Tailscale sidecar

Options considered:

- **`tailscale/tailscale` sidecar with `TS_SERVE_CONFIG` (chosen).** The stack is its own tailnet machine with its
  own name and certificate (Tailscale gets and renews it from Let's Encrypt). Nothing on the VPS host changes:
  no host Tailscale config, no open ports (the HTTP port is bound to `127.0.0.1`), and the whole thing lives in
  this folder. Userspace networking, so no `/dev/net/tun` or `NET_ADMIN`. `tailscale serve` overwrites
  `X-Forwarded-For` with the player's tailnet IP (verified: a spoofed `X-Forwarded-For: 6.6.6.6` came out as
  `100.68.229.68`), so the relay's per-IP limits see each friend.
- **`tailscale serve` on the VPS host.** Also works (tested on the dev PC: boot, audio, mic and multiplayer specs
  pass over `https://<machine>.ts.net`). But it is state outside the repo, it uses the host's name, and the
  request reaches Caddy from the Docker gateway, which Caddy does not trust, so every player counts as one IP
  for the relay limits (3 rooms in total, shared wrong-password budget). Fine as a fallback, see below.
- **Caddy with a public domain + Let's Encrypt** (the previous setup): needs DNS, ports 80/443 open to the
  internet, and exposes the game publicly. Not wanted here. `tailscale funnel` would also expose it to the
  internet; never enable it (`AllowFunnel` is `false` in `tailscale-serve.json`).

`X-Forwarded-For` chain: player → tailscale sidecar (overwrites XFF with the tailnet IP) → Caddy (keeps XFF only
from `172.31.250.10`, the sidecar's fixed address; from anyone else it replaces it with the peer) → relay
(`Relay__TrustForwardedFor=true`, takes the first entry). Do not widen `YARG_TRUSTED_PROXIES` to a peer that does
not overwrite the header: then a client can pick its own IP.

## One-time tailnet admin settings

In https://login.tailscale.com/admin:

1. **DNS** → enable **MagicDNS**, and under **HTTPS Certificates** click **Enable HTTPS**. (Both are already on
   in the owner's tailnet: `tailscale serve` on the dev PC got a valid certificate.) Note: certificate names are
   published in the public Certificate Transparency logs, so `yarg.<tailnet>.ts.net` becomes publicly known,
   though only tailnet members can reach it.
2. **Settings → Keys** → **Generate auth key**. Reusable is not needed (the node key is kept in
   `volume/tailscale`); pre-approved if device approval is on. Optionally tag it (`tag:yarg`; the tag has to be declared in
   the policy file under `tagOwners`), which also disables key expiry for the node.
3. Friends: either invite them to the tailnet (**Users → Invite**), or share only this machine with their own
   tailnet (**Machines → yarg → Share**). With a restrictive access policy, allow them to reach the machine on
   port 443.

## On the VPS

1. Install Docker with the compose plugin, and git.
2. Clone the repository. The stack only needs `deploy/` and `Server/YARG.Relay`, so skip the game's LFS assets
   and history:

   ```bash
   GIT_LFS_SKIP_SMUDGE=1 git clone --depth 1 https://github.com/FelipeFlohr/YARG.git yarg
   ```

3. `yarg/deploy/start.sh` once: it creates `deploy/.env` from `.env.example` and stops. Put the auth key in
   `TS_AUTHKEY` (and change `TS_HOSTNAME` if you want another name).
4. Copy the game build into `yarg/deploy/volume/web` yourself (scp, a USB stick, anything): the *contents* of the
   WebGL build folder, so that `volume/web/index.html` exists next to `Build/`, `StreamingAssets/` and
   `TemplateData/`. Use a release build (`Editor.WebBuild.Build` without `-webDev`, in `~/yarg-build-web` on the
   PC); a `-webDev` build has no `Build/*.wasm.br` and `start.sh` warns about it. `deploy/volume/` is in
   `.gitignore`, so `git pull` never touches it.
5. `yarg/deploy/start.sh` again: it checks `.env` and the build, then runs `docker compose up -d --build`. The
   tailnet's node key ends up in `deploy/volume/tailscale` (keep it: without it the next start needs a new auth key).
6. Check:
   - `docker compose logs tailscale` shows the login and the serve config; the machine appears in the admin console.
   - `curl -I http://127.0.0.1:8080/` on the VPS answers 200, and `curl -i http://127.0.0.1:8080/ws` answers 400
     (not a WebSocket request).
   - From a tailnet device: `https://yarg.<tailnet>.ts.net` loads (the first request may take a few seconds
     while the certificate is issued), and the online menu lists rooms.

Updating:

- **New game build:** copy it over `deploy/volume/web` (or delete the folder and copy a new one), then
  `deploy/start.sh`. Players get it on their next page load (`Cache-Control: no-cache`). The script recreates the
  `web` container every time, because a bind mount sticks to the folder it found at start, so a folder deleted and
  copied again would otherwise be invisible to Caddy.
- **New relay or deploy files:** `deploy/start.sh --update` (a `git pull --ff-only` first). The relay is rebuilt
  and restarted only if its source changed; restarting it closes every open room.

Logs: `docker compose logs -f relay` (rooms created/closed, with the creator's IP), `docker compose logs web`,
`docker compose logs tailscale`.

### Fallback: `tailscale serve` on the host

If the VPS already runs Tailscale and you'd rather not add a machine: leave `COMPOSE_PROFILES` out of `.env`,
`docker compose up -d --build`, then on the host `tailscale serve --bg http://127.0.0.1:8080`. The game is then at
`https://<vps-name>.<tailnet>.ts.net`. (All players share one IP for the relay limits, see above.)

## Local plain HTTP (testing)

Without `COMPOSE_PROFILES=tailnet` only `relay` and `web` run:

```bash
cd deploy
YARG_WEB_BUILD=~/yarg-build-web YARG_HTTP_PORT=8088 docker compose up -d --build
# http://localhost:8088 (a secure context, so the mic works on localhost)
cd ../Tests/WebSmoke && YARG_WEB_BASE_URL=http://localhost:8088 npx playwright test
cd ../../deploy && docker compose down
```

`YARG_HTTP_BIND=0.0.0.0` publishes it on the LAN too (plain HTTP there is not a secure context, see the table).
On Windows give `YARG_WEB_BUILD` as `C:/Users/...`.

| Variable | Default | |
|---|---|---|
| `COMPOSE_PROFILES` | — | `tailnet` runs the sidecar |
| `TS_AUTHKEY` | — | first start only |
| `TS_HOSTNAME` | `yarg` | machine name on the tailnet |
| `TS_EXTRA_ARGS` | — | e.g. `--advertise-tags=tag:yarg` |
| `TS_IMAGE_TAG` | `stable` | `tailscale/tailscale` tag |
| `YARG_WEB_BUILD` | `./volume/web` | the WebGL build folder |
| `YARG_HTTP_BIND` / `YARG_HTTP_PORT` | `127.0.0.1` / `8080` | the plain HTTP port on the host |
| `YARG_TRUSTED_PROXIES` | `172.31.250.10/32` | peers whose `X-Forwarded-For` Caddy keeps |

The relay's own settings (`Relay__*`, see `Server/YARG.Relay/README.md`) can be added to the `relay` service's
`environment`.

Caddy sends the `.br` files with `Content-Encoding: br` and the type of the uncompressed file
(`application/wasm`, `application/javascript`, `application/octet-stream`), byte for byte as on disk. Go omits
`Content-Length` on responses that already carry a `Content-Encoding`, so they go out chunked; the loader's
progress bar may be less precise because of that.

## Building the game in Docker? No

The owner asked whether `docker compose` could also build the WebGL build from source. It is possible, but not
reasonable for this project:

- **Image**: GameCI publishes `unityci/editor:ubuntu-6000.3.5f2-webgl-3` (7.4 GB compressed on Docker Hub, more
  unpacked). That part is fine.
- **License**: Unity Personal can't be activated with a serial; GameCI activates it with the Unity account's email
  and password (`UNITY_EMAIL`/`UNITY_PASSWORD`) on every run, which counts as a new machine activation each time
  unless it is returned, and keeps the account password as a secret on the VPS. It works, but it is exactly the
  hack the owner suspected.
- **Sources**: the build needs the Git LFS assets and the `YARG.Core` submodule, so the VPS would need a full
  clone with LFS, and NuGetForUnity restores `Assets/Packages` only when the editor opens; in batchmode Unity
  aborts first if the project doesn't compile, so a fresh checkout needs a first run with `-ignorecompilererrors`
  and then the real one (see `CLAUDE.md`).
- **Time and resources**: the first import of the project fills an 8 GB `Library/`; a release IL2CPP WebGL build
  (wasm-opt, Brotli) takes 31 minutes on the owner's 12-core desktop with a warm `Library/`. IL2CPP + emscripten
  linking wants several GB of RAM; a typical small VPS (1–2 vCPU, 2–4 GB) would swap or get OOM-killed, and would
  take hours if it finished, starving the relay while it runs.
- **Verification**: the build is checked with the Playwright suite on the PC anyway.

So: build on the Windows PC with Unity, copy the folder to the server by hand, mount it. If a containerized build is ever
wanted, it belongs in CI (GameCI on GitHub Actions with a Library cache), producing the folder this stack mounts,
not on the VPS.
