# Web audio tests

Tests for the web audio backend in `Assets/Plugins/WebGL/YargWebAudio.jspre` and `YargWebAudio.jslib`. None of
them need Unity or a build.

```bash
node --test Tests/WebAudio
```

- `pure.test.mjs`: the pure helpers (song clock math, ring buffer, WAV encoder, stem matrix, one-shot windows,
  test tone, frame statistics).
- `backend.test.mjs`: the browser half of the `.jspre`, run in a `vm` against `fake-webaudio.mjs`: the decode
  queue and background stem mixing, start/seek/speed scheduling, song end, one-shots, samples, the microphone ring
  buffer, recording and the debug hooks.
- `jslib.test.mjs`: the jslib against a fake Emscripten heap, and a cross-check that every `DllImport` in
  `Assets/Script/Audio/Web/WebAudioNative.cs` has a jslib function and stub, and that every API key the jslib
  calls exists.

`browser-check.mjs` runs the `.jspre` in headless, muted Chromium with real Web Audio, AudioWorklet and Chrome's
fake microphone (a 440 Hz WAV). It needs Playwright, taken from `Tests/WebSmoke` (`npm ci` and
`npx playwright install chromium` there) or from the folder in `PLAYWRIGHT_DIR`:

```bash
node Tests/WebAudio/browser-check.mjs
```

The whole game, with the C# side, is tested by `Tests/WebSmoke/audio.spec.mjs` against a web build.
