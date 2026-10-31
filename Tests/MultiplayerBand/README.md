# Multiplayer band tests: headless runs

Docker image with Xvfb for running YARG player instances without a display. All commands run from the repository root.

## Build

1. Player build (Linux, own folder; the script refuses `~/yarg-build` and its subfolders):

   ```bash
   flock /tmp/yarg-unity.lock "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PWD" -buildTarget Linux64 \
     -executeMethod Editor.MultiplayerTestBuild.Build -mpBuildOutput "$HOME/yarg-build-mp-banda" \
     -logFile "${TMPDIR:-/tmp}/unity-mpbanda-build.log"
   ```

   The Addressables build leaves `Assets/Settings/AddressableAssetsData/link.xml` (git-ignored).
2. `docker build -t yarg-mp-banda Tests/MultiplayerBand`

## Smoke test (one offline bot, 2112 Pt. 6)

```bash
RUN=$HOME/yarg-mp-banda-runs/smoke && rm -rf "$RUN" && mkdir -p "$RUN/data" && \
echo '{ "DisablePerSongBackgrounds": true, "DisableGlobalBackgrounds": true, "DisableDefaultBackground": true, "NoFail": 1, "AllowRemoteContent": false, "SongFolders": ["/songs"] }' > "$RUN/data/settings.json" && \
timeout 900 docker run --rm --name yarg-test-smoke --network none --device /dev/dri --device /dev/snd/seq \
  --group-add "$(stat -c %g /dev/dri/renderD128)" --group-add "$(stat -c %g /dev/dri/card0)" \
  --group-add "$(stat -c %g /dev/snd/seq)" \
  --user "$(id -u):$(id -g)" -e HOME=/work -v "$RUN:/work" -v "$HOME/yarg-build-mp-banda:/game:ro" \
  -v "$HOME/repos/musicas yarg:/songs:ro" yarg-mp-banda \
  bash -c 'Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 & export DISPLAY=:99; sleep 2; /game/YARG.x86_64 -force-glcore -screen-fullscreen 0 -screen-width 640 -screen-height 360 -persistent-data-path /work/data -logFile /work/player.log -mp-autotest offline 1 -mp-song Soliloquy'
grep "\[AutoTest\] PASS" "$RUN/player.log"
```

## What the headless environment needed

- `-force-glcore`: Vulkan under Xvfb fails ("No DRI3 support detected") and the player dies. OpenGL works, with Mesa
  llvmpipe software rendering in the container.
- Audio: `/etc/asound.conf` with a null PCM, so no sound device is needed.
- `--device /dev/snd/seq` plus its group: without the ALSA sequencer the Minis MIDI backend segfaults in
  `rtmidi_get_port_count`.
- `libhidapi-hidraw0` for the input system's HID backend.
- `"AllowRemoteContent": false` in `settings.json`, otherwise the game tries to fetch a remote Addressables catalog.
  `"NoFail": 1` keeps the fail meter from ending songs where a scripted player misses on purpose.
- Harmless log noise: read-only `/game` (the loading screen tries to write `StreamingAssets/sources`), `libvlc.so.12`
  missing (only video backgrounds, which are disabled), a `FocusHandler` NullReferenceException.
- One instance at a time on a 4 core machine.

## Band scenarios

Scenarios (`band4`, `same-instrument`, `keys`, `no-stems`) are defined in `scenarios.py`. The host starts first, the
clients 20 s later (5 s apart, `--stagger` changes that), all inside one container with `--network none`.

```bash
python3 Tests/MultiplayerBand/run_scenario.py band4            # runs docker, copies logs, runs the checker
python3 Tests/MultiplayerBand/run_scenario.py band4 --dry-run  # only prints the docker command
python3 Tests/MultiplayerBand/check_band_logs.py band4 "$HOME/yarg-mp-banda-runs/band4"  # recheck existing logs
```

Logs and settings go to `~/yarg-mp-banda-runs/<scenario>/<instance>/`; the checker reads `<instance>.log`. Run the
scripts without `-I` (isolated mode drops the script folder from `sys.path`).

Other scenarios: `same-instrument-both-miss` (two scripted guitarists whose misses overlap from 35 to 40 s),
`four-lane` (4-lane and pro drums), `fail-meter` (`NoFail` off) and `band8` (eight machines). No song in the test
library has a 6-fret or elite drums chart, and pro guitar has no gameplay track in the game, so those have no scenario.

## Native runs (Windows, no Docker)

On a machine with a GPU, `--native` runs the instances as plain processes, which is much faster than software
rendering in Docker. On Windows the build step above makes a Windows player (`YARG.exe`), since the editor builds for
the platform it runs on:

```bash
"$UNITY_EDITOR" -batchmode -nographics -projectPath "$(cygpath -w "$PWD")" -buildTarget Win64 \
  -executeMethod Editor.MultiplayerTestBuild.Build -mpBuildOutput "$HOME/yarg-build-mp-banda" -logFile unity-build.log
```

```bash
YARG_MP_SONGS='C:\Users\me\Desktop\Musicas' python Tests/MultiplayerBand/run_scenario.py band4 --native
```

Each instance opens a small window. The game's sound is off (`MasterMusicVolume` 0) unless `--audible` is passed.
`--game`, `--songs` and `--host-wait` override the build, the host's song folder and the wait before the first client.

Checker unit tests (standard library only):

```bash
cd Tests/MultiplayerBand && python3 -I -m unittest discover -s . -v
```

(`python3 -I -m unittest test_check_band_logs` does not work: `-I` drops the current directory from `sys.path`.)
