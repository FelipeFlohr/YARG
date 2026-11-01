# Web smoke test

Boots the WebGL build in headless Chromium and checks that it reaches the main menu and keeps its settings and
profiles in `localStorage`. Browsers run headless and muted (`--mute-audio`); never use `--headed` here.

```bash
cd Tests/WebSmoke
npm ci
npx playwright install chromium
npx playwright test
```

`serve.mjs` serves the build (`*.br` files with `Content-Encoding: br`). It is started by the Playwright config.

- `YARG_WEB_BUILD`: build folder, default `~/yarg-build-web` (made by `Editor.WebBuild.Build`, see `docs/`).
- `YARG_WEB_PORT`: port, default `8089`.

`bootToMenu(page)` loads the page, clicks `#yarg-start` and waits for the `Main menu ready` console line. It fails
on an error screen (`#yarg-error`) or on any `console.error` that contains `Exception`.

`fixtures/profile.json` is a minimal `YargProfile` record, used when the fresh build has no profile yet.

## Audio, microphone and frame time (`audio.spec.mjs`)

Uses the debug hooks of `YargWebAudio.jspre` (`window.yargAudioDebug`, `window.yargPerf`; see
`docs/superpowers/specs/2026-11-01-web-port-2-audio-design.md`). The tone and microphone tests need a test build
(`-webDev` or `-webTestHooks`, which define `YARG_WEB_TEST_HOOKS`); a release build ignores those requests.

- **Tone**: `requestToneTest()` makes the C# side load a generated 440 Hz tone through the real mixer path. Checks
  master and stem RMS, that the song time is `(ctx.currentTime - t0) * rate + offset` and advances with the context
  and the wall clock (±50 ms), records 5 s of the master to `audio-tone.wav`, takes a screenshot and checks the
  frame probe.
- **Microphone**: Chrome's fake device plays a 440 Hz WAV (`--use-file-for-fake-audio-capture`);
  `requestMicTest()` opens it with a `WebMicDevice`, and the `PitchTracker` must report about 440 Hz.
- **Song**: skipped unless `YARG_WEB_SONG_QUERY` holds the query string that starts a song (for example
  `?mp-autotest=offline&mp-players=1`). Same checks on the real stems, with `audio-song.wav`.

Artifacts land in each test's folder under `test-results/`. `YARG_WEB_MIN_RMS` (default `0.02`) and
`YARG_WEB_MIN_FPS` (default `15`, low because of the software renderer) tune the thresholds.

## Fast builds while iterating

`Editor.WebBuild.Build` takes `-webDev` for an incremental development build (no shader reimport or Addressables clean,
development player, debug IL2CPP, `WasmCodeOptimization.BuildTimes`, wasm2023, no compression). Use one output folder
and do not clean `Library/Bee` between builds; the player settings it changes are restored afterwards (check
`git diff ProjectSettings`, and `git checkout ProjectSettings/ProjectSettings.asset` if Unity left explicit default
IL2CPP values). The files have no `.br` extension, which `serve.mjs` handles.

```bash
"$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$PWD" -buildTarget WebGL \
  -executeMethod Editor.WebBuild.Build -webDev -webBuildOutput ~/yarg-build-web-dev -logFile web-dev.log
YARG_WEB_BUILD=~/yarg-build-web-dev npx playwright test
```

Never deploy a `-webDev` build; the normal build (no flag) is the release one.
