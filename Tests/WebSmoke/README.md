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
