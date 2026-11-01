// Loads Assets/Plugins/WebGL/YargWebAudio.jspre outside of Unity: as a plain module (pure helpers only) or inside
// a vm context with a fake Web Audio API (see fake-webaudio.mjs), which runs the browser backend too.
import fs from 'node:fs';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { FakeAudioContext, FakeMediaDevices } from './fake-webaudio.mjs';

export const JSPRE_PATH = fileURLToPath(new URL('../../Assets/Plugins/WebGL/YargWebAudio.jspre', import.meta.url));

export function loadPure() {
  // .jspre is not a known extension, but require() reads unknown extensions as CommonJS
  return createRequire(import.meta.url)(JSPRE_PATH);
}

export function loadBackend(options = {}) {
  const contexts = [];
  const frames = [];
  const listeners = {};
  const errors = [];
  class AudioContext extends FakeAudioContext {
    constructor(opts) {
      super(opts);
      contexts.push(this);
    }
  }
  const window = {
    AudioContext,
    requestAnimationFrame: (cb) => frames.push(cb),
    addEventListener: (type, cb) => { (listeners[type] ||= []).push(cb); },
    yargShowError: (message) => errors.push(message),
  };
  const mediaDevices = options.mediaDevices ?? new FakeMediaDevices();
  const sandbox = {
    Module: {},
    window,
    navigator: { mediaDevices },
    performance: { now: () => 0 },
    console: options.console ?? { log() {}, warn() {}, error() {} },
    URL: { createObjectURL: () => 'blob:worklet' },
    Blob: class { constructor(parts) { this.parts = parts; } },
    Float32Array, Uint8Array, ArrayBuffer, DataView, Math, Promise, Object, Array, String, Error, Infinity,
  };
  sandbox.window.Module = sandbox.Module;
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(JSPRE_PATH, 'utf8'), sandbox, { filename: 'YargWebAudio.jspre' });
  return {
    api: sandbox.Module.YargAudio,
    window,
    contexts,
    frames,
    listeners,
    errors,
    mediaDevices,
    get ctx() { return contexts[0]; },
  };
}

// Lets queued promise callbacks (decodes, getUserMedia) run
export async function flush(times = 20) {
  for (let i = 0; i < times; i++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}
