import { defineConfig } from '@playwright/test';

const port = Number(process.env.YARG_WEB_PORT || 8089);

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
      args: ['--mute-audio', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', '--use-angle=swiftshader'],
    },
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
