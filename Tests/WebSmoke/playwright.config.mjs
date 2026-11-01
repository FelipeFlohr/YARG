import { defineConfig } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';

const port = Number(process.env.YARG_WEB_PORT || 8089);
// YARG_WEB_BASE_URL runs the specs against a deployed build (e.g. deploy/compose.yaml) instead of serve.mjs:
// no local web server, no local relay (multiplayer.spec.mjs uses the page's own /ws), fixtures from
// fixtures/remote.mjs
const remoteBaseUrl = process.env.YARG_WEB_BASE_URL ? process.env.YARG_WEB_BASE_URL.replace(/\/+$/, '') : null;

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.mjs',
  timeout: 180_000,
  workers: 1,
  reporter: 'list',
  webServer: remoteBaseUrl ? undefined : {
    command: 'node serve.mjs',
    url: `http://127.0.0.1:${port}/`,
    reuseExistingServer: false,
    timeout: 30_000,
  },
  use: {
    baseURL: remoteBaseUrl || `http://127.0.0.1:${port}`,
    headless: true,
    launchOptions: {
      // audio.spec.mjs writes the fake microphone WAV (a 440 Hz tone) before its tests run
      args: ['--mute-audio', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', '--use-angle=swiftshader',
        '--autoplay-policy=no-user-gesture-required', '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream',
        `--use-file-for-fake-audio-capture=${path.join(os.tmpdir(), 'yarg-fake-mic-440.wav')}`],
    },
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' }, testIgnore: 'gameplay.spec.mjs' },
    {
      // Real GPU (ANGLE on D3D11) in the new headless mode, for frame rate measurements; still muted
      name: 'gpu',
      testMatch: 'gameplay.spec.mjs',
      use: {
        browserName: 'chromium',
        // Installed Edge (temporary profile, headless): the full Chromium download is blocked from running here
        channel: process.env.YARG_WEB_GPU_CHANNEL || 'msedge',
        launchOptions: {
          args: ['--mute-audio', '--ignore-gpu-blocklist', '--use-angle=d3d11', '--enable-gpu',
            '--autoplay-policy=no-user-gesture-required'],
        },
      },
    },
  ],
});
