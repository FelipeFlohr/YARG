import { defineConfig } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';

const port = Number(process.env.YARG_WEB_PORT || 8089);
// Specs that measure real play (frame rates, long songs, several pages) run on the GPU project only
const GPU_SPECS = ['gameplay.spec.mjs', 'songs.spec.mjs', 'lobby.spec.mjs'];

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.mjs',
  timeout: 180_000,
  workers: 1,
  reporter: 'list',
  webServer: {
    command: 'node serve.mjs',
    url: `http://127.0.0.1:${port}/`,
    reuseExistingServer: false,
    timeout: 30_000,
  },
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    headless: true,
    launchOptions: {
      // audio.spec.mjs writes the fake microphone WAV (a 440 Hz tone) before its tests run
      args: ['--mute-audio', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', '--use-angle=swiftshader',
        '--autoplay-policy=no-user-gesture-required', '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream',
        `--use-file-for-fake-audio-capture=${path.join(os.tmpdir(), 'yarg-fake-mic-440.wav')}`],
    },
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' }, testIgnore: GPU_SPECS },
    {
      // Real GPU (ANGLE on D3D11) in the new headless mode, for frame rate measurements; still muted
      name: 'gpu',
      testMatch: GPU_SPECS,
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
