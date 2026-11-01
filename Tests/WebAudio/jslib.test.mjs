// YargWebAudio.jslib against a fake Emscripten heap, plus a cross-check of the C# DllImports, the jslib functions
// and the Module.YargAudio keys they call (the C# side cannot be compiled outside of Unity).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { loadBackend } from './load.mjs';

const root = new URL('../../', import.meta.url);
const read = (path) => fs.readFileSync(fileURLToPath(new URL(path, root)), 'utf8');
const JSLIB = read('Assets/Plugins/WebGL/YargWebAudio.jslib');
const NATIVE_CS = read('Assets/Script/Audio/Web/WebAudioNative.cs');

function loadJslib(yargAudio) {
  const heap = new ArrayBuffer(1 << 16);
  let top = 8;
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const sandbox = {
    LibraryManager: { library: {} },
    mergeInto: (library, functions) => Object.assign(library, functions),
    Module: { YargAudio: yargAudio },
    HEAPU8: new Uint8Array(heap),
    HEAP32: new Int32Array(heap),
    HEAPF32: new Float32Array(heap),
    HEAPF64: new Float64Array(heap),
    _malloc: (size) => { const ptr = top; top += (size + 7) & ~7; return ptr; },
    lengthBytesUTF8: (s) => encoder.encode(s).length,
    stringToUTF8: (s, ptr, size) => { new Uint8Array(heap).set(encoder.encode(s).subarray(0, size - 1), ptr); new Uint8Array(heap)[ptr + Math.min(size - 1, encoder.encode(s).length)] = 0; },
    UTF8ToString: (ptr) => { const bytes = new Uint8Array(heap); let end = ptr; while (bytes[end]) end++; return decoder.decode(bytes.subarray(ptr, end)); },
  };
  vm.createContext(sandbox);
  vm.runInContext(JSLIB, sandbox, { filename: 'YargWebAudio.jslib' });
  return { lib: sandbox.LibraryManager.library, sandbox, alloc: sandbox._malloc };
}

test('every C# DllImport has a jslib function, and every jslib call has an API key', () => {
  const imported = [...NATIVE_CS.matchAll(/extern\s+\w+\s+(WebAudio_\w+)\s*\(/g)].map((m) => m[1]);
  assert.ok(imported.length > 40, `found ${imported.length} imports`);
  const { lib } = loadJslib({});
  for (const name of imported) {
    assert.equal(typeof lib[name], 'function', `jslib ${name}`);
  }
  for (const name of Object.keys(lib)) {
    assert.ok(imported.includes(name), `${name} is imported by C#`);
  }

  // The stub half of WebAudioNative must declare the same functions
  const stubs = NATIVE_CS.split('#else')[1];
  for (const name of imported) {
    assert.match(stubs, new RegExp(`\\b${name}\\(`), `stub for ${name}`);
  }

  const api = loadBackend().api;
  const called = new Set([...JSLIB.matchAll(/Module\["YargAudio"\]\["(\w+)"\]/g)].map((m) => m[1]));
  for (const key of called) {
    assert.equal(typeof api[key], 'function', `Module.YargAudio.${key}`);
  }
});

test('MixerAddData unpacks targets, indices and panning from the heap', () => {
  const calls = [];
  const { lib, sandbox, alloc } = loadJslib({
    addStemData: (mixer, bytes, targets) => { calls.push({ mixer, bytes: [...bytes], targets }); return 1; },
  });
  const data = alloc(4);
  sandbox.HEAPU8.set([1, 2, 3, 4], data);
  const handles = alloc(8);
  sandbox.HEAP32.set([11, 12], handles >> 2);
  const gains = alloc(8);
  sandbox.HEAPF32.set([1, 0.5], gains >> 2);
  const counts = alloc(8);
  sandbox.HEAP32.set([0, 2], counts >> 2);
  const indices = alloc(8);
  sandbox.HEAP32.set([3, 4], indices >> 2);
  const panning = alloc(16);
  sandbox.HEAPF32.set([1, 0, 0.25, 0.75], panning >> 2);

  assert.equal(lib.WebAudio_MixerAddData(7, data, 4, handles, gains, counts, indices, panning, 2), 1);
  const call = calls[0];
  assert.equal(call.mixer, 7);
  assert.deepEqual(call.bytes, [1, 2, 3, 4]);
  assert.equal(call.targets[0].stem, 11);
  assert.equal(call.targets[0].indices, null);
  assert.equal(call.targets[1].stem, 12);
  assert.equal(call.targets[1].gain, 0.5);
  assert.deepEqual([...call.targets[1].indices], [3, 4]);
  assert.deepEqual([...call.targets[1].panning], [1, 0, 0.25, 0.75]);

  sandbox.HEAPU8[data] = 99;
  assert.equal(call.bytes[0], 1, 'the bytes were copied out of the heap');
});

test('string results are malloc-ed UTF-8 or 0, and arrays are heap views', () => {
  const { lib, sandbox, alloc } = loadJslib({
    getError: (id) => (id === 1 ? 'Música grande demais' : null),
    micDevices: () => [{ id: 'abc', name: 'USB Mic' }],
    micRead: (id, out) => { out.set([0.5, 0.25]); return 2; },
    openMic: (id) => (id === 'abc' ? 5 : 6),
  });
  const ptr = lib.WebAudio_MixerGetError(1);
  assert.equal(sandbox.UTF8ToString(ptr), 'Música grande demais');
  assert.equal(lib.WebAudio_MixerGetError(2), 0);
  assert.equal(sandbox.UTF8ToString(lib.WebAudio_MicDeviceName(0)), 'USB Mic');
  assert.equal(sandbox.UTF8ToString(lib.WebAudio_MicDeviceId(1)), '');

  const buffer = alloc(16);
  assert.equal(lib.WebAudio_MicRead(1, buffer, 4), 2);
  assert.deepEqual([...sandbox.HEAPF32.subarray(buffer >> 2, (buffer >> 2) + 2)], [0.5, 0.25]);

  const id = alloc(4);
  sandbox.stringToUTF8('abc', id, 4);
  assert.equal(lib.WebAudio_MicOpen(id), 5);
  assert.equal(lib.WebAudio_MicOpen(0), 6);
});

test('the jslib forwards to a real backend end to end', async () => {
  const { api } = loadBackend();
  const { lib, sandbox, alloc } = loadJslib(api);
  assert.equal(lib.WebAudio_Init(), 1);
  const mixer = lib.WebAudio_MixerCreate(1, 1);
  const stem = lib.WebAudio_StemCreate(mixer, 0, 1);
  assert.ok(mixer > 0 && stem > 0);

  const times = alloc(16);
  sandbox.HEAPF64.set([1.5, 0.5], times >> 3);
  const shot = lib.WebAudio_OneShotCreate(mixer, 0, times, 2, 0);
  assert.ok(shot > 0);
  assert.equal(api.debug.mixers()[0].oneShots, 1);
  assert.equal(lib.WebAudio_MixerPlay(mixer), 0);
  assert.equal(lib.WebAudio_DebugTakeRequest(), 0);
});
