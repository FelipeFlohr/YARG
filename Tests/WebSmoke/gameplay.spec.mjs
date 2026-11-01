// Real gameplay on the web build, measured the way a player would notice it: frame rate on the GPU, sound of the
// actual song stems (level, clipping, timing) and screenshots. Runs in the "gpu" project (playwright.config.mjs),
// headless and muted, on the machine's GPU through ANGLE/D3D11; the software renderer of the other projects says
// nothing about real performance. Needs a test build (-webDev or -webTestHooks).
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import { buildSongFixtures } from './fixtures/make-song.mjs';
import { loadPure } from '../WebAudio/load.mjs';
import { dismissAntiPiracyDialog } from './fixtures/dialogs.mjs';
import { routeFixtures } from './fixtures/remote.mjs';

const pure = loadPure();
const FIXTURES = '/__fixtures/song-build/';
const MIN_FPS = Number(process.env.YARG_WEB_GPU_MIN_FPS || 50);
// The fixture song is the calibration track: sparse clicks, so a low average level and clear peaks
const MIN_RMS = Number(process.env.YARG_WEB_MIN_RMS || 0.005);
const MIN_PEAK = 0.1;

test.setTimeout(300_000);
test.beforeAll(() => buildSongFixtures());
test.beforeEach(({ context }) => routeFixtures(context));

function collectLogs(page) {
  const lines = [];
  const errors = [];
  page.on('console', (msg) => {
    lines.push(msg.text());
    if (msg.type() === 'error' && msg.text().includes('Exception')) errors.push(msg.text());
  });
  page.on('pageerror', (err) => errors.push(String(err.message || err)));
  const count = (text) => lines.filter((line) => line.includes(text)).length;
  return {
    lines,
    errors,
    async waitFor(text, timeout = 60_000) {
      const start = Date.now();
      while (count(text) === 0) {
        if (Date.now() - start > timeout) {
          throw new Error(`Timed out waiting for "${text}". Last lines:\n${lines.slice(-30).join('\n')}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
    },
  };
}

async function shot(page, testInfo, name) {
  const file = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path: file });
  await testInfo.attach(name, { path: file, contentType: 'image/png' });
}

async function record(page, seconds) {
  await page.evaluate(() => window.yargAudioDebug.startRecording());
  await page.waitForTimeout(seconds * 1000);
  const base64 = await page.evaluate(async () => {
    const bytes = new Uint8Array(await window.yargAudioDebug.stopRecording());
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
  });
  return Buffer.from(base64, 'base64');
}

test('gameplay runs smoothly on the GPU with the song audible and in time', async ({ page }, testInfo) => {
  const log = collectLogs(page);

  await page.goto('/?web-test');
  const renderer = await page.evaluate(() => {
    const gl = document.createElement('canvas').getContext('webgl2');
    const info = gl && gl.getExtension('WEBGL_debug_renderer_info');
    return info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : 'unknown';
  });
  testInfo.annotations.push({ type: 'renderer', description: renderer });
  test.skip(/swiftshader|llvmpipe|basic render/i.test(renderer), `no GPU in this browser (${renderer})`);

  await page.locator('#yarg-start').click();
  await log.waitFor('Main menu ready', 170_000);
  await page.waitForTimeout(1000);
  await dismissAntiPiracyDialog(page);
  await shot(page, testInfo, '1-menu');

  await page.evaluate((url) => window.yargTest.loadSongFromUrl(url), FIXTURES + 'web-fixture.zip');
  await log.waitFor('[WebSong] Screen: Song');
  await page.evaluate(() => window.yargTest.songCommand('bot'));
  await log.waitFor('[WebSongTest] Done: bot');
  await page.evaluate(() => window.yargTest.songCommand('play'));
  await log.waitFor('[WebSongTest] Scene: Gameplay');

  // Let loading hitches pass, then measure a steady stretch of play
  await page.waitForTimeout(4000);
  await page.evaluate(() => window.yargPerf.reset && window.yargPerf.reset());
  const wav = await record(page, 8);
  const perf = await page.evaluate(() => window.yargPerf.snapshot());
  await shot(page, testInfo, '2-gameplay');

  const mixer = await page.evaluate(() =>
    window.yargAudioDebug.mixers().find((m) => m.playing && m.stems.some((s) => s.decoded)) ?? null);
  const time = mixer && await page.evaluate((id) => window.yargAudioDebug.songTime(id), mixer.id);

  fs.writeFileSync(testInfo.outputPath('gameplay.wav'), wav);
  const decoded = pure.decodeWav(wav.buffer.slice(wav.byteOffset, wav.byteOffset + wav.byteLength));
  const levels = decoded.channels.map((channel) => pure.measure(channel));
  const stats = { renderer, perf, levels, mixer, time };
  fs.writeFileSync(testInfo.outputPath('gameplay-stats.json'), JSON.stringify(stats, null, 2));

  expect(mixer, 'a mixer is playing the song stems').not.toBeNull();
  expect(Math.max(...levels.map((l) => l.rms)), 'the song is audible').toBeGreaterThan(MIN_RMS);
  expect(Math.max(...levels.map((l) => l.peak)), 'clicks are heard').toBeGreaterThan(MIN_PEAK);
  expect(Math.max(...levels.map((l) => l.peak)), 'no clipping').toBeLessThan(1.0);
  expect(Math.abs(time.songTime - ((time.contextTime - time.t0) * time.rate + time.offset)),
    'song time follows the audio clock').toBeLessThan(1e-6);
  expect(perf.fps, `average FPS (avg ${perf.avgFrameMs.toFixed(1)} ms, p95 ${perf.p95FrameMs.toFixed(1)} ms)`)
    .toBeGreaterThan(MIN_FPS);
  expect(perf.p95FrameMs, 'p95 frame time').toBeLessThan(1000 / 30);
  expect(log.errors, 'console.error with Exception').toEqual([]);
});
