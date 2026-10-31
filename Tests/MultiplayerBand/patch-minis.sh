#!/usr/bin/env bash
# Headless containers have no ALSA sequencer, and Minis' MidiBackend.OnUpdate then crashes the player (SIGSEGV in
# rtmidi_get_port_count). This makes OnUpdate return immediately in a built player.
# Usage: patch-minis.sh <build dir> <cecilpatch.dll>
# cecilpatch is a tiny Mono.Cecil tool (args: src dst searchdir) that inserts `ret` at the start of
# Minis.MidiBackend.OnUpdate.
set -euo pipefail
managed="$1/YARG_Data/Managed"
[ -f "$managed/Minis.orig.dll" ] || cp "$managed/Minis.dll" "$managed/Minis.orig.dll"
dotnet "$2" "$managed/Minis.orig.dll" "$managed/Minis.dll" "$managed"
