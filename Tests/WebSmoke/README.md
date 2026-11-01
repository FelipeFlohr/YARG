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

## Online multiplayer (`multiplayer.spec.mjs`)

Plays a match between two pages (separate browser contexts, so separate `localStorage`) through a local relay. It
starts `Server/YARG.Relay` with `dotnet run` on `YARG_RELAY_PORT` (default `8090`) and passes `?relay=ws://…` to
both pages, which run the in-game autotest (only in test builds, `-webDev` or `-webTestHooks`, which define
`YARG_WEB_TEST_HOOKS`; a release build accepts only `-lang`, `-offline` and `-relay`):

- host: `?mp-autotest=webhost&mp-room=SmokeRoom&mp-password=…&mp-players=2&mp-profile=Host&mp-bot=1`
- client: `?mp-autotest=webjoin&mp-room=SmokeRoom&mp-password=…&mp-wrong-password=errada&mp-profile=Cliente&mp-bot=1`

The test checks that the wrong password is refused before the right one joins, loads the song on the host with
`window.yargTest.loadSongFromUrl` (the song loader's hook), waits for the client to install it under
`/tmp/yarg-song/mp-…`, takes screenshots of both pages in gameplay and on the results screen (attached to the
report, also in `test-results/`), and expects `[AutoTest] PASS` on both. Any `[AutoTest] FAIL` fails it at once.

The song is `fixtures/song-build/web-fixture.sng` from `fixtures/make-song.mjs` (served at `/__fixtures/`), or
`YARG_WEB_SONG_URL`.

```bash
npx playwright test multiplayer.spec.mjs
```
