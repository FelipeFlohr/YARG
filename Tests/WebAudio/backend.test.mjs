// The browser half of YargWebAudio.jspre, run against fake-webaudio.mjs. Covers what the C# side relies on: the
// decode queue and background mixing, start/seek/speed scheduling, song end, one-shots, samples, the microphone
// ring buffer, recording and the debug hooks.
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadBackend, loadPure, flush } from './load.mjs';

const pure = loadPure();
const RATE = 48000;

function wavBytes(seconds, value, channels = 2) {
  const frames = Math.round(RATE * seconds);
  const planar = [];
  for (let c = 0; c < channels; c++) planar.push(new Float32Array(frames).fill(typeof value === 'number' ? value : value[c]));
  return new Uint8Array(pure.encodeWav(planar, RATE));
}

function tagged(tag) {
  const bytes = new Uint8Array(64);
  bytes.set(Buffer.from(tag, 'ascii'));
  return bytes;
}

function target(stem, extra = {}) {
  return { stem, gain: 1, indices: null, panning: null, ...extra };
}

function setup(options) {
  const env = loadBackend(options);
  const api = env.api;
  assert.ok(api.ensureContext(), 'context created');
  return { env, api, ctx: env.ctx };
}

test('context is created once, interactive, and resumed by a user gesture', () => {
  const env = loadBackend();
  assert.equal(env.contexts.length, 0, 'nothing is created before use');
  for (const type of ['pointerdown', 'keydown']) {
    assert.ok(env.listeners[type]?.length, `${type} listener`);
  }
  env.listeners.pointerdown[0]();
  assert.equal(env.contexts.length, 1);
  assert.equal(env.ctx.options.latencyHint, 'interactive');
  assert.equal(env.ctx.state, 'running');
  assert.equal(env.api.ctx, env.ctx, 'Module.YargAudio.ctx exposes the context');
  env.api.ensureContext();
  assert.equal(env.contexts.length, 1);
  assert.equal(env.api.state(), 1);
  assert.ok(Math.abs(env.api.latency() - 0.025) < 1e-9, 'outputLatency + baseLatency');
});

test('a missing AudioContext shows the error screen', () => {
  const env = loadBackend();
  delete env.window.AudioContext;
  assert.equal(env.api.ensureContext(), null);
  assert.equal(env.errors.length, 1);
});

test('stems decode one at a time and merged parts are summed into one buffer', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  const song = api.createStem(mixer, 0, 1);
  const drums = api.createStem(mixer, 6, 1);

  api.addStemData(mixer, wavBytes(1, 0.25), [target(song)]);
  api.addStemData(mixer, wavBytes(2, 0.125), [target(song)]); // an unplayed stem mixed into the background
  api.addStemData(mixer, wavBytes(0.5, 0.5), [target(drums)]);
  assert.equal(api.isLoading(mixer), 1);

  await flush();
  assert.equal(api.isLoading(mixer), 0);
  assert.equal(ctx.maxConcurrentDecodes, 1, 'never two decodes at once');
  assert.equal(ctx.decodeCount, 3);
  assert.equal(api.getError(mixer), null);
  assert.equal(api.getLength(mixer), 2);

  const stems = api.debug.stems(mixer);
  // nothing played yet, so the debug view falls back to the given mixer
  const background = stems.find((s) => s.id === song);
  assert.equal(background.parts, 2);
  assert.equal(background.duration, 2);
  const songBuffer = findStemBuffer(api, mixer, song, ctx);
  assert.ok(Math.abs(songBuffer.getChannelData(0)[100] - 0.375) < 1e-3, 'first second has both parts');
  assert.ok(Math.abs(songBuffer.getChannelData(1)[RATE + 100] - 0.125) < 1e-3, 'second second only the longer part');
});

// The stem buffer, read back through a played source
function findStemBuffer(api, mixer, stem, ctx) {
  const before = ctx.sources.length;
  api.play(mixer);
  const sources = ctx.sources.slice(before);
  api.pause(mixer);
  const stems = api.debug.stems(mixer);
  const index = stems.findIndex((s) => s.id === stem);
  return sources[index].buffer;
}

test('mogg-style targets build each stem from its own channels', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  const kick = api.createStem(mixer, 6, 1);
  const kit = api.createStem(mixer, 7, 1);
  api.addStemData(mixer, wavBytes(0.1, [0.1, 0.2, 0.3], 3), [
    target(kick, { indices: [0], panning: [1, 1] }),
    target(kit, { indices: [1, 2], panning: [1, 0, 0, 1] }),
  ]);
  await flush();
  assert.equal(ctx.decodeCount, 1, 'decoded once for both stems');
  const kitBuffer = findStemBuffer(api, mixer, kit, ctx);
  assert.ok(Math.abs(kitBuffer.getChannelData(0)[5] - 0.2) < 1e-3);
  assert.ok(Math.abs(kitBuffer.getChannelData(1)[5] - 0.3) < 1e-3);
  const kickBuffer = findStemBuffer(api, mixer, kick, ctx);
  assert.ok(Math.abs(kickBuffer.getChannelData(1)[5] - 0.1) < 1e-3);
});

test('play before decoding finishes starts every stem together afterwards', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  const a = api.createStem(mixer, 0, 1);
  const b = api.createStem(mixer, 1, 1);
  api.addStemData(mixer, wavBytes(2, 0.1), [target(a)]);
  api.addStemData(mixer, wavBytes(2, 0.1), [target(b)]);
  assert.equal(api.play(mixer), 0);
  assert.equal(ctx.sources.length, 0, 'waits for the decodes');

  ctx.currentTime = 3;
  await flush();
  assert.equal(ctx.sources.length, 2);
  const starts = ctx.sources.map((s) => s.started);
  assert.deepEqual(starts[0], starts[1], 'same context time and offset');
  assert.ok(Math.abs(starts[0].when - 3.05) < 1e-9);
  assert.equal(starts[0].offset, 0);

  ctx.currentTime = 4.05;
  assert.ok(Math.abs(api.getPosition(mixer) - 1) < 1e-9);
  const t = api.debug.songTime();
  assert.equal(t.mixer, mixer);
  assert.ok(Math.abs(t.songTime - ((t.contextTime - t.t0) * t.rate + t.offset)) < 1e-12);
});

test('seek and pause recreate the sources at the right offset', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  const stem = api.createStem(mixer, 0, 1);
  api.addStemData(mixer, wavBytes(10, 0.1), [target(stem)]);
  await flush();

  api.play(mixer);
  ctx.currentTime = 2.05;
  assert.ok(Math.abs(api.getPosition(mixer) - 2) < 1e-9);

  api.setPosition(mixer, 5);
  assert.ok(ctx.sources[0].stopped, 'old source stopped');
  const second = ctx.sources[1];
  assert.equal(second.started.offset, 5);
  assert.ok(Math.abs(second.started.when - 2.1) < 1e-9);

  ctx.currentTime = 3.1;
  api.pause(mixer);
  assert.ok(second.stopped);
  assert.ok(Math.abs(api.getPosition(mixer) - 6) < 1e-9);
  ctx.currentTime = 50;
  assert.ok(Math.abs(api.getPosition(mixer) - 6) < 1e-9, 'paused position holds');

  api.setPosition(mixer, -1.5);
  assert.equal(ctx.sources.length, 2, 'seeking while paused starts nothing');
  api.play(mixer);
  const third = ctx.sources[2];
  assert.equal(third.started.offset, 0);
  assert.ok(Math.abs(third.started.when - 51.55) < 1e-9, 'negative position delays the start');
});

test('speed changes playbackRate and keeps the position continuous', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1.5);
  const stem = api.createStem(mixer, 0, 1);
  api.addStemData(mixer, wavBytes(10, 0.1), [target(stem)]);
  await flush();
  api.play(mixer);
  assert.equal(ctx.sources[0].playbackRate.value, 1.5);

  ctx.currentTime = 1.05;
  assert.ok(Math.abs(api.getPosition(mixer) - 1.5) < 1e-9);
  api.setSpeed(mixer, 0.5);
  assert.equal(ctx.sources[0].playbackRate.value, 0.5);
  assert.ok(Math.abs(api.getPosition(mixer) - 1.5) < 1e-9);
  ctx.currentTime = 3.05;
  assert.ok(Math.abs(api.getPosition(mixer) - 2.5) < 1e-9);
});

test('stem volume ramps, mixer volume and fades are applied', async () => {
  const { api } = setup();
  const mixer = api.createMixer(0.5, 1);
  const stem = api.createStem(mixer, 1, 0.8);
  api.setStemVolume(stem, 0);
  const info = api.debug.stems(mixer).find((s) => s.id === stem);
  assert.equal(info.gain, 0);
  api.fadeMixer(mixer, 0.25, 1);
  assert.equal(api.debug.mixers()[0].gain, 0.25);
});

test('the song ends once, at its length', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  const stem = api.createStem(mixer, 0, 1);
  api.addStemData(mixer, wavBytes(1, 0.1), [target(stem)]);
  await flush();
  api.play(mixer);
  ctx.currentTime = 0.9;
  assert.equal(api.takeEnded(mixer), 0);
  ctx.currentTime = 1.06;
  assert.equal(api.takeEnded(mixer), 1);
  assert.equal(api.takeEnded(mixer), 0);
  api.setPosition(mixer, 0);
  ctx.currentTime = 2.2;
  assert.equal(api.takeEnded(mixer), 1, 'ends again after a seek');
});

test('decode failures: a bad stem is skipped, no stem or no memory is an error', async () => {
  const { api } = setup();
  const partial = api.createMixer(1, 1);
  api.addStemData(partial, tagged('FAIL'), [target(api.createStem(partial, 1, 1))]);
  api.addStemData(partial, wavBytes(1, 0.1), [target(api.createStem(partial, 0, 1))]);

  const none = api.createMixer(1, 1);
  api.addStemData(none, tagged('FAIL'), [target(api.createStem(none, 0, 1))]);

  const memory = api.createMixer(1, 1);
  api.addStemData(memory, tagged('OOMM'), [target(api.createStem(memory, 0, 1))]);
  await flush();

  assert.equal(api.getError(partial), null);
  assert.match(api.getError(none), /could not decode/);
  assert.match(api.getError(memory), /too big/);
  assert.equal(api.play(memory), -1, 'a failed song does not play');
});

test('a stem buffer that cannot be allocated is a memory error', async () => {
  const { api, ctx } = setup();
  ctx.maxBufferFrames = RATE;
  const mixer = api.createMixer(1, 1);
  api.addStemData(mixer, wavBytes(2, 0.1), [target(api.createStem(mixer, 0, 1))]);
  await flush();
  assert.match(api.getError(mixer), /too big/);
});

test('freeing a mixer while it decodes drops the rest of its queue', async () => {
  const { api, ctx } = setup();
  const mixer = api.createMixer(1, 1);
  api.addStemData(mixer, wavBytes(1, 0.1), [target(api.createStem(mixer, 0, 1))]);
  api.addStemData(mixer, wavBytes(1, 0.1), [target(api.createStem(mixer, 1, 1))]);
  api.freeMixer(mixer);
  await flush();
  assert.ok(ctx.decodeCount <= 1);
  assert.equal(api.debug.mixers().length, 0);
});

test('one-shots are scheduled half a second ahead and rescheduled on seek', async () => {
  const { api, ctx } = setup();
  const sample = api.loadSample(wavBytes(0.05, 0.3));
  const mixer = api.createMixer(1, 1);
  api.addStemData(mixer, wavBytes(10, 0.1), [target(api.createStem(mixer, 0, 1))]);
  await flush();
  const shot = api.createOneShot(mixer, sample, Float64Array.from([3, 0.2, 0.4, 1]), 0.02);

  api.play(mixer); // starts at 0.05
  const songSources = ctx.sources.length;
  api.tick();
  const scheduled = ctx.sources.slice(songSources);
  assert.equal(scheduled.length, 2, 'only 0.2 and 0.4 are within the lookahead');
  assert.ok(Math.abs(scheduled[0].started.when - (0.05 + 0.2 - 0.02)) < 1e-9);
  api.tick();
  assert.equal(ctx.sources.length - songSources, 2, 'nothing scheduled twice');

  ctx.currentTime = 0.6;
  api.tick();
  assert.equal(ctx.sources.length - songSources, 3, '1.0 enters the window');

  api.setPosition(mixer, 2.8);
  assert.ok(scheduled.every((s) => s.stopped), 'pending one-shots cancelled');
  const afterSeek = ctx.sources.length;
  api.tick();
  const rescheduled = ctx.sources.slice(afterSeek);
  assert.equal(rescheduled.length, 1);
  assert.ok(Math.abs(rescheduled[0].started.when - (0.65 + 0.2 - 0.02)) < 1e-9);

  api.setOneShotEnabled(shot, false);
  api.setOneShotSchedule(shot, Float64Array.from([2.9]));
  api.tick();
  assert.equal(ctx.sources.length, afterSeek + 1, 'disabled one-shots do not play');
  api.freeOneShot(shot);
  assert.equal(api.debug.mixers()[0].oneShots, 0);
});

test('samples play, queue, pause, resume and stop', async () => {
  const { api, ctx } = setup();
  const sample = api.loadSample(wavBytes(1, 0.3));
  assert.equal(api.playSample(sample, 1, false, false), 0, 'not decoded yet');
  await flush();

  assert.equal(api.playSample(sample, 0.5, true, false), 1);
  assert.equal(api.sampleState(sample), 1);
  const voice = ctx.sources.at(-1);
  assert.equal(voice.loop, true);

  ctx.currentTime = 2.25;
  api.pauseSample(sample);
  assert.equal(api.sampleState(sample), 2);
  api.resumeSample(sample);
  const resumed = ctx.sources.at(-1);
  assert.ok(Math.abs(resumed.started.offset - 0.25) < 1e-9, 'loop resumes where it was');
  api.stopSample(sample, 0);
  resumed.end();
  assert.equal(api.sampleState(sample), 0);

  const vox = api.loadSample(wavBytes(1, 0.3));
  await flush();
  api.playSample(vox, 1, false, true);
  api.playSample(vox, 1, false, true);
  const [first, second] = ctx.sources.slice(-2);
  assert.ok(Math.abs(second.started.when - first.started.when - 1) < 1e-9, 'vox clips play one after another');
});

test('microphone: constraints, device list, ring buffer and pitch report', async () => {
  const { api, ctx, env } = setup();
  api.refreshMicDevices();
  await flush();
  const devices = api.micDevices();
  assert.deepEqual(devices.map((d) => d.name), ['Default microphone', 'USB Mic']);

  const mic = api.openMic('usb-1');
  assert.equal(api.micState(mic), 0);
  await flush();
  assert.equal(api.micState(mic), 1);
  const constraints = env.mediaDevices.requests[0].audio;
  assert.equal(constraints.echoCancellation, false);
  assert.equal(constraints.noiseSuppression, false);
  assert.equal(constraints.autoGainControl, false);
  assert.equal(constraints.deviceId.exact, 'usb-1');

  // No AudioWorklet in the fake, so the ScriptProcessor fallback taps the input
  const tap = ctx.processors.at(-1);
  tap.emit([Float32Array.from([0.5, -0.5, 0.5, -0.5])]);
  assert.equal(api.micAvailable(mic), 4);
  const out = new Float32Array(3);
  assert.equal(api.micRead(mic, out), 3);
  assert.deepEqual([...out], [0.5, -0.5, 0.5]);
  api.micReset(mic);
  assert.equal(api.micAvailable(mic), 0);

  api.micReportPitch(mic, 441, 30);
  const debug = api.debug.mic()[0];
  assert.equal(debug.lastPitch, 441);
  assert.equal(debug.pitchFrames, 1);
  assert.ok(Math.abs(debug.inputRms - 0.5) < 1e-9);

  api.micSetMonitoring(mic, 0.7);
  api.closeMic(mic);
  assert.equal(api.micState(mic), -1);
  assert.equal(env.mediaDevices.requests.length, 1);
});

test('a denied microphone reports the error name', async () => {
  const { FakeMediaDevices } = await import('./fake-webaudio.mjs');
  const { api } = setup({ mediaDevices: new FakeMediaDevices({ deny: true }) });
  const mic = api.openMic('');
  await flush();
  assert.equal(api.micState(mic), -1);
  assert.equal(api.micError(mic), 'NotAllowedError');
});

test('recording returns a WAV of what reached the master', async () => {
  const { api, ctx } = setup();
  await api.debug.startRecording();
  const tap = ctx.processors.at(-1);
  tap.emit([new Float32Array(1024).fill(0.25), new Float32Array(1024).fill(-0.25)]);
  tap.emit([new Float32Array(1024).fill(0.5), new Float32Array(1024).fill(-0.5)]);
  const wav = await api.debug.stopRecording();
  const decoded = pure.decodeWav(wav);
  assert.equal(decoded.sampleRate, RATE);
  assert.equal(decoded.channels[0].length, 2048);
  assert.ok(Math.abs(decoded.channels[0][1500] - 0.5) < 1e-3);
  assert.ok(Math.abs(decoded.channels[1][10] + 0.25) < 1e-3);
  await assert.rejects(api.debug.stopRecording());
});

test('debug hooks: state, master level, test requests and the frame probe', async () => {
  const { api, env } = setup();
  const state = api.debug.state();
  assert.equal(state.created, true);
  assert.equal(state.sampleRate, RATE);
  assert.equal(state.outputLatency, 0.02);
  assert.equal(state.baseLatency, 0.005);
  assert.equal(typeof api.debug.master().rms, 'number');

  assert.equal(api.takeDebugRequest(), 0);
  assert.ok(api.debug.requestToneTest(2, 440));
  api.debug.requestMicTest();
  assert.equal(api.takeDebugRequest(), 1);
  assert.equal(api.takeDebugRequest(), 3);
  const tone = pure.decodeWav(api.debugToneWav().slice().buffer);
  assert.equal(tone.channels[0].length, 2 * RATE);

  assert.equal(env.window.yargAudioDebug, api.debug);
  assert.equal(env.window.YargAudio, api);
  const perf = env.window.yargPerf;
  [0, 16, 32, 48].forEach((t) => {
    const frame = env.frames.shift();
    frame(t);
  });
  const snap = perf.snapshot();
  assert.equal(snap.samples, 3);
  assert.ok(Math.abs(snap.fps - 62.5) < 1e-9);
});

test('a gesture before the script loaded still lets the context resume', async () => {
  const env = loadBackend();
  env.window.yargUserGesture = true;
  env.api.ensureContext();
  await flush(2);
  assert.equal(env.ctx.state, 'running');

  const later = loadBackend();
  later.api.ensureContext();
  assert.equal(later.ctx.state, 'suspended', 'no gesture yet');
  later.window.yargUserGesture = true;
  later.api.tick();
  await flush(2);
  assert.equal(later.ctx.state, 'running', 'tick resumes once the page saw a gesture');
});
