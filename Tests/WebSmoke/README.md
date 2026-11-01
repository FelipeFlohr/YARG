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

## Song loading (`song.spec.mjs`)

Loads a song from disk the way the player does and saves screenshots (song screen, difficulty select, gameplay,
score screen, result, errors) to `test-results/`. The page is opened with `?web-test`, which enables
`window.yargTest.songCommand` (`bot` adds an active bot player, `play` starts the current song with it).

- `fixtures/song/` holds the chart and `song.ini`; `fixtures/make-song.mjs` (run by the spec) builds
  `fixtures/song-build/` with the song folder, `web-fixture.zip`, `web-fixture.sng`, `no-audio.zip` and
  `not-a-song.txt`. The audio is the game's `calibration_music.ogg`; the cover is a generated PNG.
- `serve.mjs` serves `fixtures/` under `/__fixtures/`.
- `window.yargTest.loadSongFromUrl(url)` and `loadFolderFromUrls(base, names)` fetch a song, write it to the in-memory
  filesystem and take the same C# path as a drop. "Load another" goes through the real picker (Playwright's
  `filechooser` event). `listSongFiles()` lists `/tmp/yarg-song`.

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
