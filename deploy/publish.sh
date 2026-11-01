#!/usr/bin/env bash
# Copies the deploy files, the relay source and a WebGL build to the VPS over ssh, then restarts the stack.
# Needs only ssh and tar (works from Git Bash on Windows; no rsync).
#
#   deploy/publish.sh <ssh-host> [build-folder] [remote-dir]
#
#   ssh-host      anything ssh accepts: user@host, a Host from ~/.ssh/config, or the VPS's tailnet name
#   build-folder  default ~/yarg-build-web (a release build from Editor.WebBuild.Build)
#   remote-dir    default /opt/yarg; the layout there is <dir>/deploy, <dir>/Server/YARG.Relay, <dir>/deploy/web
#
# The remote deploy/.env (TS_AUTHKEY etc.) is kept; create it once from .env.example.
# SKIP_BUILD=1 copies only the deploy files and the relay; SKIP_UP=1 copies without restarting.
set -euo pipefail

host=${1:?usage: deploy/publish.sh <ssh-host> [build-folder] [remote-dir]}
build=${2:-$HOME/yarg-build-web}
dir=${3:-/opt/yarg}
repo=$(cd "$(dirname "$0")/.." && pwd)

if [ "${SKIP_BUILD:-0}" != 1 ]; then
  [ -f "$build/index.html" ] && [ -d "$build/Build" ] || { echo "no WebGL build in $build" >&2; exit 1; }
  # A -webDev build has no compression (Build/*.wasm instead of *.wasm.br) and must not be deployed
  if ! ls "$build"/Build/*.wasm.br >/dev/null 2>&1 && [ "${ALLOW_DEV_BUILD:-0}" != 1 ]; then
    echo "$build looks like a -webDev build (no Build/*.wasm.br); set ALLOW_DEV_BUILD=1 to copy it anyway" >&2
    exit 1
  fi
fi

echo "Copying deploy/ and Server/YARG.Relay/ to $host:$dir"
ssh "$host" "mkdir -p '$dir/deploy' '$dir/Server/YARG.Relay'"
tar -C "$repo" -cf - --exclude=bin --exclude=obj --exclude=web --exclude=.env deploy Server/YARG.Relay \
  | ssh "$host" "tar -C '$dir' -xf -"

if [ "${SKIP_BUILD:-0}" != 1 ]; then
  echo "Copying the build $build to $host:$dir/deploy/web"
  # Extracted next to it, then moved in: the folder itself stays (Caddy's bind mount follows its inode)
  ssh "$host" "rm -rf '$dir/deploy/web.new' && mkdir -p '$dir/deploy/web.new' '$dir/deploy/web'"
  tar -C "$build" -cf - . | ssh "$host" "tar -C '$dir/deploy/web.new' -xf -"
  ssh "$host" "cd '$dir/deploy' && find web -mindepth 1 -maxdepth 1 -exec rm -rf {} + \
    && find web.new -mindepth 1 -maxdepth 1 -exec mv {} web/ \; && rmdir web.new"
fi

if [ "${SKIP_UP:-0}" != 1 ]; then
  ssh "$host" "cd '$dir/deploy' && docker compose up -d --build && docker compose ps"
fi
