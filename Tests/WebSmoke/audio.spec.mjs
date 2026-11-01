// Audio, microphone and frame time checks of the web build (subprojects 2 and 4 of the web port). They go through
// the debug hooks of YargWebAudio.jspre: window.yargAudioDebug and window.yargPerf.
//
// - Tone test: the C# side loads a tone generated in JS through the real mixer path (AddChannel, decode, Play).
// - Microphone: Chrome's fake capture device plays a 440 Hz WAV; the C# WebMicDevice and PitchTracker must hear it.
// - Song (optional): set YARG_WEB_SONG_QUERY to the query string that starts a song, e.g.
//   "?mp-autotest=offline&mp-players=1", once the web build can load one (subproject 3).
//
// Artifacts (WAV recordings, screenshots, stats) go to each test's folder in test-results/. Browsers run headless and
// muted.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { loadPure } from '../WebAudio/load.mjs';

const pure = loadPure();

const MIN_RMS = Number(process.env.YARG_WEB_MIN_RMS || 0.02);
// Software rendering (SwiftShader) here: only a sanity floor. gameplay.spec.mjs measures real frame rates on the GPU.
const MIN_FPS = Number(process.env.YARG_WEB_MIN_FPS || 5);
const TIME_TOLERANCE = 0.05;

// Chrome reads the fake microphone from a 16 bit WAV, looping it. Outside test-results/, which Playwright empties.
const micWav = path.join(os.tmpdir(), 'yarg-fake-mic-440.wav');
fs.writeFileSync(micWav, Buffer.from(pure.makeToneWav(48000, 4, 440, 0.6)));

// A file in the current test's folder under test-results/
function artifact(name) {
  return test.info().outputPath(name);
}

async function boot(page, url = '/', { waitForMenu = true } = {}) {
  const errors = [];
  let ready;
  const menuReady = new Promise((resolve) => { ready = resolve; });
  page.on('console', (msg) => {
    const text = msg.text();
    if (msg.type() === 'error' && text.includes('Exception')) errors.push(text);
    if (text.includes('Main menu ready')) ready();
  });
  page.on('pageerror', (err) => errors.push(String(err.message || err)));

  await page.goto(url);
  await page.locator('#yarg-start').click();
  if (waitForMenu) {
    const failed = new Promise((_, reject) => {
      page.locator('#yarg-error').waitFor({ state: 'visible', timeout: 170_000 })
        .then(async () => reject(new Error('Error screen: ' + await page.locator('#yarg-error-message').textContent())))
        .catch(() => {});
    });
    await Promise.race([menuReady, failed]);
  }
  await expect.poll(() => page.evaluate(() => window.yargAudioDebug?.state().state), { timeout: 30_000 })
    .toBe('running');
  return errors;
}

// The mixer that is playing with every stem decoded, or null
async function playingMixer(page) {
  return page.evaluate(() => {
    const mixers = window.yargAudioDebug.mixers();
    return mixers.find((m) => m.playing && m.loading === 0 && m.stems.some((s) => s.decoded)) ?? null;
  });
}

async function maxMasterRms(page, millis) {
  return page.evaluate(async (ms) => {
    let max = 0;
    const end = performance.now() + ms;
    while (performance.now() < end) {
      max = Math.max(max, window.yargAudioDebug.master().rms);
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    return max;
  }, millis);
}

// Song time against the context clock and against the wall clock, over a few seconds
async function checkSongTime(page, mixerId, seconds) {
  const a = await page.evaluate((id) => window.yargAudioDebug.songTime(id), mixerId);
  await page.waitForTimeout(seconds * 1000);
  const b = await page.evaluate((id) => window.yargAudioDebug.songTime(id), mixerId);

  const songDelta = b.songTime - a.songTime;
  const contextDelta = b.contextTime - a.contextTime;
  const wallDelta = (b.performanceNow - a.performanceNow) / 1000;
  const expected = (b.contextTime - b.t0) * b.rate + b.offset;

  expect(Math.abs(b.songTime - expected), 'song time = (ctx.currentTime - t0) * rate + offset').toBeLessThan(1e-6);
  expect(Math.abs(songDelta - contextDelta * b.rate), 'song time follows the context').toBeLessThan(TIME_TOLERANCE);
  expect(Math.abs(contextDelta - wallDelta), 'the context renders in real time').toBeLessThan(TIME_TOLERANCE);
  return { songDelta, contextDelta, wallDelta };
}

async function record(page, seconds, file) {
  await page.evaluate(() => window.yargAudioDebug.startRecording());
  await page.waitForTimeout(seconds * 1000);
  const base64 = await page.evaluate(async () => {
    const wav = await window.yargAudioDebug.stopRecording();
    const bytes = new Uint8Array(wav);
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
  });
  const buffer = Buffer.from(base64, 'base64');
  fs.writeFileSync(artifact(file), buffer);
  const wav = pure.decodeWav(buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength));
  const duration = wav.channels[0].length / wav.sampleRate;
  const level = pure.measure(wav.channels[0]);
  return { wav, duration, rms: level.rms, peak: level.peak };
}

async function checkFrameTimes(page, name) {
  const perf = await page.evaluate(() => window.yargPerf.snapshot());
  fs.writeFileSync(artifact(`${name}-perf.json`), JSON.stringify(perf, null, 2));
  expect(perf.samples, 'frame probe collected frames').toBeGreaterThanOrEqual(60);
  expect(perf.fps, `average FPS (avg ${perf.avgFrameMs.toFixed(1)} ms)`).toBeGreaterThan(MIN_FPS);
  // Software rendering takes a few hundred ms on heavy frames; this only catches multi-second stalls
  expect(perf.p95FrameMs, 'p95 frame time').toBeLessThan(1000);
  return perf;
}

test.describe('web audio', () => {
  test('tone plays through the C# mixer, keeps time and records', async ({ page }) => {
    const errors = await boot(page);
    const state = await page.evaluate(() => window.yargAudioDebug.state());
    fs.writeFileSync(artifact('audio-state.json'), JSON.stringify(state, null, 2));
    expect(state.sampleRate).toBeGreaterThan(0);

    await page.evaluate(() => window.yargAudioDebug.requestToneTest(20, 440));
    await expect.poll(() => playingMixer(page), { timeout: 15_000 }).not.toBeNull();
    const mixer = await playingMixer(page);
    expect(mixer.stems.length).toBe(1);
    expect(mixer.stems[0].playing).toBe(true);
    expect(mixer.length).toBeGreaterThan(19);

    expect(await maxMasterRms(page, 1000), 'master RMS while playing').toBeGreaterThan(MIN_RMS);
    const stems = await page.evaluate((id) => window.yargAudioDebug.stems(id), mixer.id);
    expect(stems[0].rms, 'stem RMS').toBeGreaterThan(MIN_RMS);
    expect(stems[0].playbackRate).toBe(1);

    await checkSongTime(page, mixer.id, 3);
    await page.screenshot({ path: artifact('audio-tone.png') });

    const recording = await record(page, 5, 'audio-tone.wav');
    expect(Math.abs(recording.duration - 5), 'recording length').toBeLessThan(0.5);
    expect(recording.rms, 'recorded RMS').toBeGreaterThan(MIN_RMS);
    const pitch = pure.zeroCrossingPitch(recording.wav.channels[0], recording.wav.sampleRate);
    expect(Math.abs(pitch - 440), `recorded pitch ${pitch.toFixed(1)} Hz`).toBeLessThan(10);

    await checkFrameTimes(page, 'audio-tone');

    await page.evaluate(() => window.yargAudioDebug.stopToneTest());
    await expect.poll(() => page.evaluate(() => window.yargAudioDebug.mixers().length), { timeout: 5_000 }).toBe(0);
    expect(errors, 'page errors').toEqual([]);
  });
});

test.describe('web microphone', () => {
  // The fake capture flags (and the WAV path) live in playwright.config.mjs: launch options cannot change per group
  test.use({ permissions: ['microphone'] });

  test('fake microphone pitch reaches the PitchTracker', async ({ page }) => {
    const errors = await boot(page);
    await page.evaluate(() => window.yargAudioDebug.requestMicTest());

    await expect.poll(() => page.evaluate(() => window.yargAudioDebug.mic()[0]?.state), { timeout: 15_000 }).toBe(1);
    await expect.poll(() => page.evaluate(() => window.yargAudioDebug.mic()[0].pitchFrames), { timeout: 15_000 })
      .toBeGreaterThan(10);

    const mic = (await page.evaluate(() => window.yargAudioDebug.mic()))[0];
    fs.writeFileSync(artifact('mic.json'), JSON.stringify(mic, null, 2));
    expect(mic.inputRms, 'microphone input RMS').toBeGreaterThan(0.05);
    expect(mic.dropped, 'C# keeps up with the ring buffer').toBe(0);
    const midi = 12 * Math.log2(mic.lastPitch / 440) + 69;
    expect(Math.abs(midi - 69), `detected pitch ${mic.lastPitch.toFixed(1)} Hz`).toBeLessThan(1);

    await page.evaluate(() => window.yargAudioDebug.stopMicTest());
    await expect.poll(() => page.evaluate(() => window.yargAudioDebug.mic().length), { timeout: 5_000 }).toBe(0);
    expect(errors, 'page errors').toEqual([]);
  });
});

test.describe('web song', () => {
  test('a song plays its stems in time', async ({ page }) => {
    const query = process.env.YARG_WEB_SONG_QUERY;
    test.skip(!query, 'set YARG_WEB_SONG_QUERY to the query string that starts a song');

    const errors = await boot(page, '/' + query, { waitForMenu: false });
    await expect.poll(() => playingMixer(page), { timeout: 170_000, intervals: [1000] }).not.toBeNull();
    const mixer = await playingMixer(page);
    fs.writeFileSync(artifact('song-mixer.json'), JSON.stringify(mixer, null, 2));

    await page.waitForTimeout(2000);
    expect(await maxMasterRms(page, 2000), 'master RMS while playing').toBeGreaterThan(MIN_RMS);
    const stems = await page.evaluate((id) => window.yargAudioDebug.stems(id), mixer.id);
    expect(stems.every((s) => s.playing || !s.decoded), 'every decoded stem plays').toBe(true);
    expect(new Set(stems.map((s) => s.playbackRate)).size, 'one rate for all stems').toBe(1);

    await checkSongTime(page, mixer.id, 3);
    await page.screenshot({ path: artifact('audio-song.png') });
    const recording = await record(page, 5, 'audio-song.wav');
    expect(Math.abs(recording.duration - 5)).toBeLessThan(0.5);
    expect(recording.rms, 'recorded RMS').toBeGreaterThan(MIN_RMS);
    await page.screenshot({ path: artifact('audio-song-after.png') });
    await checkFrameTimes(page, 'audio-song');
    expect(errors, 'page errors').toEqual([]);
  });
});
