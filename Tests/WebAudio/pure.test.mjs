// Pure helpers of Assets/Plugins/WebGL/YargWebAudio.jspre. Run with: node --test Tests/WebAudio
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadPure } from './load.mjs';

const pure = loadPure();

test('song clock follows (now - t0) * rate + offset', () => {
  const clock = new pure.SongClock();
  assert.equal(clock.position(5), 0);

  clock.seek(10);
  clock.start(2);
  assert.equal(clock.position(1), 10, 'holds at offset before the scheduled start');
  assert.equal(clock.position(2), 10);
  assert.equal(clock.position(4), 12);

  clock.pause(5);
  assert.equal(clock.position(100), 13);
});

test('song clock stays continuous across rate changes', () => {
  const clock = new pure.SongClock();
  clock.start(0);
  clock.setRate(4, 0.5);
  assert.equal(clock.position(4), 4);
  assert.equal(clock.position(6), 5);
  clock.setRate(6, 2);
  assert.equal(clock.position(6), 5);
  assert.equal(clock.position(7), 7);
  assert.equal(clock.contextTimeOf(9), 8);
});

test('song clock keeps a pending start when the rate changes before it', () => {
  const clock = new pure.SongClock();
  clock.seek(3);
  clock.start(10);
  clock.setRate(9, 2);
  assert.equal(clock.t0, 10);
  assert.equal(clock.position(11), 5);
});

test('song clock with a negative offset reaches 0 after the delay', () => {
  const clock = new pure.SongClock();
  clock.seek(-2);
  clock.start(1);
  assert.equal(clock.position(3), 0);
  assert.equal(clock.contextTimeOf(0), 3);
});

test('ring buffer reads in order and wraps', () => {
  const ring = new pure.RingBuffer(4);
  ring.write(Float32Array.from([1, 2, 3]));
  const out = new Float32Array(2);
  assert.equal(ring.read(out), 2);
  assert.deepEqual([...out], [1, 2]);
  ring.write(Float32Array.from([4, 5, 6]));
  const all = new Float32Array(8);
  assert.equal(ring.read(all), 4);
  assert.deepEqual([...all.subarray(0, 4)], [3, 4, 5, 6]);
  assert.equal(ring.count, 0);
});

test('ring buffer drops the oldest samples when full', () => {
  const ring = new pure.RingBuffer(3);
  ring.write(Float32Array.from([1, 2]));
  ring.write(Float32Array.from([3, 4]));
  assert.equal(ring.dropped, 1);
  const out = new Float32Array(3);
  assert.equal(ring.read(out), 3);
  assert.deepEqual([...out], [2, 3, 4]);

  ring.write(Float32Array.from([5, 6, 7, 8, 9]));
  assert.equal(ring.read(out), 3);
  assert.deepEqual([...out], [7, 8, 9]);
});

test('ring buffer read respects max and clear', () => {
  const ring = new pure.RingBuffer(8);
  ring.write(Float32Array.from([1, 2, 3, 4]));
  const out = new Float32Array(8);
  assert.equal(ring.read(out, 1), 1);
  ring.clear();
  assert.equal(ring.read(out), 0);
});

test('WAV encoding round-trips 16 bit stereo', () => {
  const left = Float32Array.from([0, 0.5, -0.5, 1, -1]);
  const right = Float32Array.from([0.25, -0.25, 0, 0.75, -0.75]);
  const wav = pure.encodeWav([left, right], 48000);
  assert.equal(wav.byteLength, 44 + 5 * 2 * 2);
  const header = Buffer.from(wav).toString('ascii', 0, 4) + Buffer.from(wav).toString('ascii', 8, 16);
  assert.equal(header, 'RIFFWAVEfmt ');

  const decoded = pure.decodeWav(wav);
  assert.equal(decoded.sampleRate, 48000);
  assert.equal(decoded.channels.length, 2);
  for (let i = 0; i < left.length; i++) {
    assert.ok(Math.abs(decoded.channels[0][i] - left[i]) < 1e-4, `left ${i}`);
    assert.ok(Math.abs(decoded.channels[1][i] - right[i]) < 1e-4, `right ${i}`);
  }
});

test('mixToStereo: mono goes to both sides, extra channels are ignored', () => {
  const left = new Float32Array(3);
  const right = new Float32Array(3);
  pure.mixToStereo([Float32Array.from([1, 2, 3])], null, null, 0.5, left, right);
  assert.deepEqual([...left], [0.5, 1, 1.5]);
  assert.deepEqual([...right], [0.5, 1, 1.5]);

  const l2 = new Float32Array(2);
  const r2 = new Float32Array(2);
  pure.mixToStereo([Float32Array.from([1, 1]), Float32Array.from([2, 2]), Float32Array.from([9, 9])],
    [], null, 1, l2, r2);
  assert.deepEqual([...l2], [1, 1]);
  assert.deepEqual([...r2], [2, 2]);
});

test('mixToStereo follows the mogg panning matrix and adds to what is there', () => {
  const sources = [Float32Array.from([1, 1]), Float32Array.from([2, 2]), Float32Array.from([4, 4])];
  const left = Float32Array.from([10, 10]);
  const right = new Float32Array(2);
  // channel 1 hard left, channel 2 half on both sides
  pure.mixToStereo(sources, [1, 2], [1, 0, 0.5, 0.5], 1, left, right);
  assert.deepEqual([...left], [14, 14]);
  assert.deepEqual([...right], [2, 2]);
});

test('lowerBound and dueUntil pick the one-shots in a window', () => {
  const times = [0.5, 1, 1, 2, 3.5];
  assert.equal(pure.lowerBound(times, 1), 1);
  assert.equal(pure.lowerBound(times, 4), 5);
  assert.equal(pure.lowerBound(times, -1), 0);
  assert.equal(pure.dueUntil(times, 1, 2), 3);
  assert.equal(pure.dueUntil(times, 3, 2), 3);
  assert.equal(pure.dueUntil(times, 0, 100), 5);
});

test('tone WAV has the requested pitch, length and channel balance', () => {
  const wav = pure.makeToneWav(48000, 1, 440, 0.5);
  const decoded = pure.decodeWav(wav);
  assert.equal(decoded.channels[0].length, 48000);
  const pitch = pure.zeroCrossingPitch(decoded.channels[0], 48000);
  assert.ok(Math.abs(pitch - 440) < 2, `pitch ${pitch}`);
  const l = pure.measure(decoded.channels[0]);
  const r = pure.measure(decoded.channels[1]);
  assert.ok(Math.abs(l.rms - 0.5 / Math.SQRT2) < 0.01, `rms ${l.rms}`);
  assert.ok(Math.abs(r.rms / l.rms - 0.8) < 0.01);
});

test('measure gives RMS and peak', () => {
  const m = pure.measure(Float32Array.from([0.5, -1, 0.5, 0]));
  assert.equal(m.peak, 1);
  assert.ok(Math.abs(m.rms - Math.sqrt(1.5 / 4)) < 1e-9);
  assert.deepEqual(pure.measure(new Float32Array(0)), { rms: 0, peak: 0 });
});

test('frame stats average a rolling window', () => {
  const stats = new pure.FrameStats(3);
  assert.equal(stats.snapshot().fps, 0);
  [0, 10, 30, 40, 60].forEach((t) => stats.tick(t));
  const snap = stats.snapshot();
  assert.equal(snap.samples, 3);
  assert.equal(snap.frames, 5);
  assert.ok(Math.abs(snap.avgFrameMs - 50 / 3) < 1e-9);
  assert.equal(snap.maxFrameMs, 20);
  assert.ok(Math.abs(snap.fps - 60) < 1e-9);
});
