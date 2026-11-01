// Runs YargWebAudio.jspre in headless Chromium, without the Unity build: real decodeAudioData, AudioWorklet taps,
// recording and Chrome's fake microphone. Needs Playwright with Chromium (Tests/WebSmoke: npm ci and
// npx playwright install chromium).
//
//   node Tests/WebAudio/browser-check.mjs
//
// PLAYWRIGHT_DIR points to another folder with node_modules/playwright, if Tests/WebSmoke has none.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { JSPRE_PATH, loadPure } from './load.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const playwrightRoot = process.env.PLAYWRIGHT_DIR || path.join(here, '..', 'WebSmoke');
const { chromium } = createRequire(path.join(playwrightRoot, 'package.json'))('playwright');

const pure = loadPure();
const micWav = path.join(os.tmpdir(), 'yarg-fake-mic-440.wav');
fs.writeFileSync(micWav, Buffer.from(pure.makeToneWav(48000, 4, 440, 0.6)));

const page = `<!DOCTYPE html><html><body><script>window.Module = {};</script>
<script src="/yarg-web-audio.js"></script></body></html>`;
const server = http.createServer((req, res) => {
  if (req.url === '/yarg-web-audio.js') {
    res.writeHead(200, { 'Content-Type': 'application/javascript' }).end(fs.readFileSync(JSPRE_PATH));
  } else {
    res.writeHead(200, { 'Content-Type': 'text/html' }).end(page);
  }
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;

const browser = await chromium.launch({
  headless: true,
  args: ['--mute-audio', '--autoplay-policy=no-user-gesture-required', '--use-fake-ui-for-media-stream',
    '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${micWav}`],
});

const checks = [];
function check(name, ok, detail) {
  checks.push({ name, ok, detail });
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail === undefined ? '' : ': ' + JSON.stringify(detail)}`);
}

try {
  const context = await browser.newContext({ permissions: ['microphone'] });
  const tab = await context.newPage();
  tab.on('console', (msg) => { if (msg.type() !== 'log') console.log(`  [page ${msg.type()}] ${msg.text()}`); });
  tab.on('pageerror', (err) => check('no page errors', false, String(err)));
  await tab.goto(url);

  const result = await tab.evaluate(async () => {
    const A = window.YargAudio;
    const debug = window.yargAudioDebug;
    const pure = debug.pure;
    const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
    const out = {};

    A.ensureContext();
    await A.ctx.resume();
    out.state = debug.state();

    // A song with two stems: a tone and a quieter copy mixed into the same background stem
    const mixer = A.createMixer(1, 1);
    const stem = A.createStem(mixer, 0, 1);
    const sr = A.ctx.sampleRate;
    A.addStemData(mixer, new Uint8Array(pure.makeToneWav(sr, 8, 440, 0.5)), [{ stem, gain: 1, indices: null, panning: null }]);
    A.addStemData(mixer, new Uint8Array(pure.makeToneWav(sr, 8, 440, 0.25)), [{ stem, gain: 1, indices: null, panning: null }]);
    A.play(mixer);
    out.loadingRightAfterPlay = A.isLoading(mixer);
    const t0 = performance.now();
    while (A.isLoading(mixer)) await sleep(10);
    out.decodeMs = performance.now() - t0;
    out.length = A.getLength(mixer);
    out.error = A.getError(mixer);

    await sleep(600);
    let maxRms = 0;
    for (let i = 0; i < 10; i++) {
      maxRms = Math.max(maxRms, debug.master().rms);
      await sleep(30);
    }
    out.masterRms = maxRms;
    out.stems = debug.stems(mixer);

    const a = debug.songTime(mixer);
    await sleep(2000);
    const b = debug.songTime(mixer);
    out.time = {
      songDelta: b.songTime - a.songTime,
      contextDelta: b.contextTime - a.contextTime,
      wallDelta: (b.performanceNow - a.performanceNow) / 1000,
      formulaError: Math.abs(b.songTime - ((b.contextTime - b.t0) * b.rate + b.offset)),
    };

    A.setSpeed(mixer, 1.5);
    const c = debug.songTime(mixer);
    await sleep(1000);
    const d = debug.songTime(mixer);
    out.speed = { songDelta: d.songTime - c.songTime, contextDelta: d.contextTime - c.contextTime,
      rate: debug.stems(mixer)[0].playbackRate };
    A.setSpeed(mixer, 1);

    await debug.startRecording();
    await sleep(2000);
    const wav = await debug.stopRecording();
    const decoded = pure.decodeWav(wav);
    out.recording = {
      seconds: decoded.channels[0].length / decoded.sampleRate,
      rms: pure.measure(decoded.channels[0]).rms,
      pitch: pure.zeroCrossingPitch(decoded.channels[0], decoded.sampleRate),
    };

    A.setStemVolume(stem, 0);
    await sleep(300);
    out.mutedRms = debug.master().rms;
    A.pause(mixer);

    // Samples and one-shots
    const sample = A.loadSample(new Uint8Array(pure.makeToneWav(sr, 0.2, 880, 0.5)));
    await sleep(200);
    out.samplePlayed = A.playSample(sample, 1, false, false);
    A.setPosition(mixer, 0);
    A.createOneShot(mixer, sample, [0.2, 0.4, 3], 0);
    A.play(mixer);
    A.tick();
    out.oneShotsScheduled = debug.mixers()[0].oneShots;

    // Microphone through the AudioWorklet tap
    await A.refreshMicDevices();
    out.micDevices = A.micDevices();
    const mic = A.openMic('');
    while (A.micState(mic) === 0) await sleep(20);
    out.micState = A.micState(mic);
    out.micError = A.micError(mic);
    await sleep(1000);
    out.micAvailable = A.micAvailable(mic);
    const samples = new Float32Array(A.micAvailable(mic));
    A.micRead(mic, samples);
    // The first block after the device opens holds its start-up transient: measure the settled half
    out.micPitch = pure.zeroCrossingPitch(samples.subarray(samples.length >> 1), sr);
    out.micSegmentPitch = [0, 1, 2, 3].map((i) => pure.zeroCrossingPitch(
      samples.subarray(i * (samples.length >> 2), (i + 1) * (samples.length >> 2)), sr));
    out.micRms = pure.measure(samples).rms;
    out.micDebug = debug.mic()[0];
    A.closeMic(mic);
    A.freeMixer(mixer);

    out.perf = window.yargPerf.snapshot();
    return out;
  });

  fs.writeFileSync(path.join(os.tmpdir(), 'yarg-web-audio-browser-check.json'), JSON.stringify(result, null, 2));
  check('context running', result.state.state === 'running', result.state);
  check('decoded without error', result.error === null && result.loadingRightAfterPlay === 1,
    { decodeMs: Math.round(result.decodeMs), length: result.length });
  check('length of the longest part', Math.abs(result.length - 8) < 0.01, result.length);
  check('master RMS while playing', result.masterRms > 0.1, result.masterRms);
  check('stem playing with both parts', result.stems[0].playing && result.stems[0].parts === 2, result.stems[0]);
  check('song time = formula', result.time.formulaError < 1e-6, result.time.formulaError);
  check('song time follows the context (+-50 ms)', Math.abs(result.time.songDelta - result.time.contextDelta) < 0.05, result.time);
  check('context renders in real time (+-50 ms)', Math.abs(result.time.contextDelta - result.time.wallDelta) < 0.05, result.time);
  check('speed 1.5 advances song time 1.5x', Math.abs(result.speed.songDelta - 1.5 * result.speed.contextDelta) < 0.05 && result.speed.rate === 1.5, result.speed);
  check('recording length', Math.abs(result.recording.seconds - 2) < 0.2, result.recording);
  check('recording has the tone', result.recording.rms > 0.1 && Math.abs(result.recording.pitch - 440) < 5, result.recording);
  check('muted stem goes quiet', result.mutedRms < 0.01, result.mutedRms);
  check('sample plays', result.samplePlayed === 1);
  check('one-shot channel registered', result.oneShotsScheduled === 1);
  check('microphone opens', result.micState === 1, { error: result.micError, devices: result.micDevices });
  check('microphone fills the ring buffer', result.micAvailable > 20000, result.micAvailable);
  check('microphone hears 440 Hz', Math.abs(result.micPitch - 440) < 10 && result.micRms > 0.05,
    { pitch: result.micPitch, segments: result.micSegmentPitch, rms: result.micRms });
  check('frame probe runs', result.perf.samples > 30 && result.perf.fps > 10, result.perf);
} finally {
  await browser.close();
  server.close();
}

const failed = checks.filter((c) => !c.ok);
console.log(`${checks.length - failed.length}/${checks.length} checks passed`);
process.exit(failed.length === 0 ? 0 : 1);
