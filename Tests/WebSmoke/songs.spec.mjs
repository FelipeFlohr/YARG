// Real songs in singleplayer, on the GPU: every song file in YARG_WEB_SONGS (e.g. a folder of .sng files) is
// loaded the way a dropped file is, played by the bot for a while, recorded and measured: every stem decoded and
// playing, the mix audible without clipping, song time locked to the audio clock, the playback rate steady (on
// the web a rate change also changes the pitch, so constant sync corrections warble), frame rate, and screenshots
// of the song screen and gameplay. Runs in the "gpu" project (playwright.config.mjs); needs a test build.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { loadPure } from '../WebAudio/load.mjs';
import { dismissAntiPiracyDialog } from './fixtures/dialogs.mjs';

const pure = loadPure();
const songsDir = process.env.YARG_WEB_SONGS;
const songs = songsDir && fs.existsSync(songsDir)
  ? fs.readdirSync(songsDir).filter((f) => /\.(sng|zip)$/i.test(f)).sort()
  : [];

const MIN_FPS = Number(process.env.YARG_WEB_GPU_MIN_FPS || 50);
// A full mix: well above the clicks of the calibration fixture
const MIN_RMS = Number(process.env.YARG_WEB_SONG_MIN_RMS || 0.02);
const PLAY_SECONDS = Number(process.env.YARG_WEB_SONG_PLAY_SECONDS || 40);
// Share of frames with the stems not at 1x while the song starts and plays
const MAX_OFF_RATE = 0.02;

test.setTimeout(420_000);

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
    count,
    async waitFor(text, timeout = 90_000) {
      const start = Date.now();
      while (count(text) === 0) {
        if (Date.now() - start > timeout) {
          throw new Error(`Timed out waiting for "${text}". Last lines:\n${lines.slice(-30).join('\n')}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      return lines.find((line) => line.includes(text));
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

// The mixer's playback rate on every frame for a while, from the start of the song
const sampleRates = (page, seconds) => page.evaluate(async (ms) => {
  const rates = [];
  const end = performance.now() + ms;
  while (performance.now() < end) {
    const m = window.yargAudioDebug.mixers().find((x) => x.playing);
    if (m) rates.push(m.rate);
    await new Promise((resolve) => requestAnimationFrame(resolve));
  }
  return rates;
}, seconds * 1000);

const playingMixer = (page) => page.evaluate(() =>
  window.yargAudioDebug.mixers().find((m) => m.playing && m.stems.some((s) => s.decoded)) ?? null);

test.describe('real songs', () => {
  test.skip(songs.length === 0, 'set YARG_WEB_SONGS to a folder with .sng or .zip songs');

  for (const song of songs) {
    test(`plays ${song}`, async ({ page }, testInfo) => {
      const log = collectLogs(page);

      await page.goto('/?web-test');
      await page.locator('#yarg-start').click();
      await log.waitFor('Main menu ready', 170_000);
      await dismissAntiPiracyDialog(page);

      const loadStart = Date.now();
      await page.evaluate((url) => window.yargTest.loadSongFromUrl(url), '/__songs/' + encodeURIComponent(song));
      const loaded = await log.waitFor('[WebSong] Loaded');
      await log.waitFor('[WebSong] Screen: Song');
      const loadSeconds = (Date.now() - loadStart) / 1000;
      await page.waitForTimeout(2000); // album art decode
      await shot(page, testInfo, '1-song-screen');
      expect(log.count('[WebSong] Error'), 'load errors').toBe(0);
      expect(log.count('Failed to decode an image'), 'album art').toBe(0);

      await page.evaluate(() => window.yargTest.songCommand('bot'));
      await log.waitFor('[WebSongTest] Done: bot');
      await page.evaluate(() => window.yargTest.songCommand('play'));
      await log.waitFor('[WebSongTest] Scene: Gameplay');
      const part = log.lines.find((l) => l.includes('[WebSongTest] Web Test:')) ?? '';

      // Every stem decoded before playback
      await expect.poll(() => playingMixer(page), { timeout: 60_000 }).not.toBeNull();
      const rates = await sampleRates(page, 8);
      const offRate = rates.filter((r) => Math.abs(r - 1) > 1e-6);
      await shot(page, testInfo, '2-gameplay-early');

      await page.evaluate(() => window.yargPerf.reset && window.yargPerf.reset());
      const t0 = await page.evaluate(() => {
        const m = window.yargAudioDebug.mixers().find((x) => x.playing);
        return m ? window.yargAudioDebug.songTime(m.id) : null;
      });
      const wav = await record(page, 10);
      const perf = await page.evaluate(() => window.yargPerf.snapshot());
      const mixer = await playingMixer(page);
      const time = await page.evaluate((id) => window.yargAudioDebug.songTime(id), mixer.id);
      await shot(page, testInfo, '3-gameplay');

      // Keep playing a while longer to catch problems that build up (drift, stalls, stems stopping)
      await page.waitForTimeout(Math.max(0, PLAY_SECONDS - 18) * 1000);
      const later = await page.evaluate((id) => window.yargAudioDebug.songTime(id), mixer.id);
      const stemsLater = await page.evaluate((id) => window.yargAudioDebug.stems(id), mixer.id);
      await shot(page, testInfo, '4-gameplay-later');

      fs.writeFileSync(testInfo.outputPath('song.wav'), wav);
      const decoded = pure.decodeWav(wav.buffer.slice(wav.byteOffset, wav.byteOffset + wav.byteLength));
      const levels = decoded.channels.map((channel) => pure.measure(channel));
      const songDelta = later.songTime - time.songTime;
      const wallDelta = (later.performanceNow - time.performanceNow) / 1000;
      const stats = {
        song, loaded, part, loadSeconds, perf, levels, songDelta, wallDelta,
        rate: { frames: rates.length, offOne: offRate.length / rates.length, min: Math.min(1, ...rates),
          max: Math.max(1, ...rates) },
        recordedFrom: t0 && t0.songTime,
        stems: stemsLater.map((s) => ({ stem: s.stem, decoded: s.decoded, playing: s.playing, rate: s.playbackRate,
          duration: s.duration, rms: s.rms, peak: s.peak })),
      };
      fs.writeFileSync(testInfo.outputPath('stats.json'), JSON.stringify(stats, null, 2));
      await testInfo.attach('stats', { body: JSON.stringify(stats, null, 2), contentType: 'application/json' });

      expect(mixer.stems.length, 'stems').toBeGreaterThan(0);
      expect(stemsLater.every((s) => s.decoded), 'every stem decoded').toBe(true);
      expect(stemsLater.every((s) => s.playing), 'every stem still playing').toBe(true);
      expect(new Set(stemsLater.map((s) => s.playbackRate)).size, 'one rate for all stems').toBe(1);
      expect(Math.max(...levels.map((l) => l.rms)), 'the song is audible').toBeGreaterThan(MIN_RMS);
      expect(Math.max(...levels.map((l) => l.peak)), 'no clipping').toBeLessThan(1.0);
      expect(Math.abs(time.songTime - ((time.contextTime - time.t0) * time.rate + time.offset)),
        'song time follows the audio clock').toBeLessThan(1e-6);
      expect(Math.abs(songDelta - wallDelta), `song time keeps real time (${songDelta.toFixed(3)} s in ${wallDelta.toFixed(3)} s)`)
        .toBeLessThan(0.1);
      expect(stats.rate.offOne, `frames not at 1x (${stats.rate.min.toFixed(3)}-${stats.rate.max.toFixed(3)})`)
        .toBeLessThan(MAX_OFF_RATE);
      expect(stats.rate.max, 'no catch-up burst').toBeLessThan(1.1);
      expect(perf.fps, `average FPS (p95 ${perf.p95FrameMs.toFixed(1)} ms)`).toBeGreaterThan(MIN_FPS);
      expect(perf.p95FrameMs, 'p95 frame time').toBeLessThan(1000 / 30);
      expect(log.errors, 'console.error with Exception').toEqual([]);
    });
  }
});
