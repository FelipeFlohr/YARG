#!/usr/bin/env bash
# Starts (or restarts after changes) the YARG web stack on this machine. See README.md in this folder.
#
#   deploy/start.sh            checks the setup, then docker compose up -d --build
#   deploy/start.sh --update   git pull first (new relay or deploy files), then the same
#
# The game build is not in git: copy the WebGL build folder (index.html, Build/, StreamingAssets/,
# TemplateData/) into deploy/volume/web yourself, then run this (again) so the web container picks it up.
set -euo pipefail

cd "$(dirname "$0")"

if [ "${1:-}" = "--update" ]; then
  git pull --ff-only
fi

if [ ! -f .env ]; then
  cp .env.example .env
  echo "Created deploy/.env from .env.example. Put your Tailscale auth key in TS_AUTHKEY, then run this again." >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
. ./.env
set +a

build=${YARG_WEB_BUILD:-./volume/web}
mkdir -p "$build" volume/tailscale

if [ ! -f "$build/index.html" ] || [ ! -d "$build/Build" ]; then
  echo "No game build in deploy/${build#./}: copy the WebGL build folder's contents there" >&2
  echo "(index.html, Build/, StreamingAssets/, TemplateData/), then run this again." >&2
  exit 1
fi
if ! ls "$build"/Build/*.wasm.br >/dev/null 2>&1; then
  echo "Warning: no Build/*.wasm.br, so this looks like a -webDev test build (big and uncompressed)." >&2
fi

if [[ ",${COMPOSE_PROFILES:-}," == *",tailnet,"* ]] && [ -z "$(ls -A volume/tailscale)" ]; then
  case "${TS_AUTHKEY:-}" in
    "" | *REPLACE_ME*)
      echo "First start on the tailnet needs TS_AUTHKEY in deploy/.env (see README.md)." >&2
      exit 1
      ;;
  esac
fi

docker compose up -d --build
# A bind mount sticks to the folder it found at start: if volume/web was deleted and copied again, only a new
# container sees the new one (takes a second; open rooms live in the relay and are not affected)
docker compose up -d --no-deps --force-recreate web
docker compose ps

if [[ ",${COMPOSE_PROFILES:-}," == *",tailnet,"* ]]; then
  echo "Game: https://${TS_HOSTNAME:-yarg}.<your-tailnet>.ts.net (the first load may wait a few seconds for the certificate)"
else
  echo "Game: http://${YARG_HTTP_BIND:-127.0.0.1}:${YARG_HTTP_PORT:-8080} (plain HTTP; no tailnet profile in .env)"
fi
