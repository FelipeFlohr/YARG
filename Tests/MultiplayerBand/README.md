# Multiplayer band tests: headless runs

Docker image with Xvfb for running YARG player instances without a display.

## Build

1. Player build (Linux, own folder; the script refuses `~/yarg-build`):

   ```bash
   flock /tmp/yarg-unity.lock "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PWD" -buildTarget Linux64 \
     -executeMethod Editor.MultiplayerTestBuild.Build -mpBuildOutput "$HOME/yarg-build-mp-banda" -logFile /tmp/unity-mpbanda-build.log
   ```

2. `Tests/MultiplayerBand/patch-minis.sh "$HOME/yarg-build-mp-banda" <path to cecilpatch.dll>` (needed after every build, see the script).
3. `docker build -t yarg-mp-banda Tests/MultiplayerBand`

## Smoke test (one offline bot, 2112 Pt. 6)

```bash
RUN=$HOME/yarg-mp-banda-runs/smoke && rm -rf "$RUN" && mkdir -p "$RUN/data" && \
echo '{ "DisablePerSongBackgrounds": true, "DisableGlobalBackgrounds": true, "DisableDefaultBackground": true, "NoFail": 1, "AllowRemoteContent": false, "SongFolders": ["/songs"] }' > "$RUN/data/settings.json" && \
timeout 900 docker run --rm --name yarg-test-smoke --network none --device /dev/dri \
  --group-add "$(stat -c %g /dev/dri/renderD128)" --group-add "$(stat -c %g /dev/dri/card0)" \
  --user "$(id -u):$(id -g)" -e HOME=/work -v "$RUN:/work" -v "$HOME/yarg-build-mp-banda:/game:ro" \
  -v "$HOME/repos/musicas yarg:/songs:ro" yarg-mp-banda \
  bash -c 'Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 & export DISPLAY=:99; sleep 2; /game/YARG.x86_64 -force-glcore -screen-fullscreen 0 -screen-width 640 -screen-height 360 -persistent-data-path /work/data -logFile /work/player.log -mp-autotest offline 1 -mp-song Soliloquy'
grep "\[AutoTest\] PASS" "$RUN/player.log"
```

## What the headless environment needed

- `-force-glcore`: Vulkan under Xvfb fails ("No DRI3 support detected") and the player dies. OpenGL works, with Mesa
  llvmpipe software rendering in the container.
- Audio: `/etc/asound.conf` with a null PCM, so no sound device is needed.
- `Minis.dll` patch (above): this host has no `/dev/snd/seq`, so MIDI polling segfaults.
- `libhidapi-hidraw0` for the input system's HID backend.
- `"AllowRemoteContent": false` in `settings.json`, otherwise the game tries to fetch a remote Addressables catalog.
  `"NoFail": 1` keeps the fail meter from ending songs where a scripted player misses on purpose.
- Harmless log noise: read-only `/game` (the loading screen tries to write `StreamingAssets/sources`), `libvlc.so.12`
  missing (only video backgrounds, which are disabled), a `FocusHandler` NullReferenceException.
- One instance at a time on a 4 core machine.
