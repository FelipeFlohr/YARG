// Just enough of the Web Audio API to run the YargWebAudio.jspre backend in node. Time only moves when a test sets
// ctx.currentTime. decodeAudioData reads 16 bit WAV files and resolves on a later tick, one call at a time being
// tracked so a test can check that decodes never overlap. A buffer starting with "FAIL" rejects like a bad file,
// one starting with "OOMM" like an allocation failure.

class FakeParam {
  constructor(value) {
    this.value = value;
    this.events = [];
  }

  setValueAtTime(value, time) { this.value = value; this.events.push(['set', value, time]); }
  setTargetAtTime(value, time, constant) { this.value = value; this.events.push(['target', value, time, constant]); }
  linearRampToValueAtTime(value, time) { this.value = value; this.events.push(['ramp', value, time]); }
  cancelScheduledValues(time) { this.events.push(['cancel', time]); }
}

class FakeNode {
  constructor(ctx) {
    this.context = ctx;
    this.outputs = new Set();
  }

  connect(node) {
    this.outputs.add(node);
    return node;
  }

  disconnect(node) {
    if (node) {
      this.outputs.delete(node);
    } else {
      this.outputs.clear();
    }
  }
}

export class FakeAudioBuffer {
  constructor(channels, length, sampleRate) {
    this.numberOfChannels = channels;
    this.length = length;
    this.sampleRate = sampleRate;
    this.data = [];
    for (let i = 0; i < channels; i++) this.data.push(new Float32Array(length));
  }

  get duration() { return this.length / this.sampleRate; }
  getChannelData(i) { return this.data[i]; }
}

class FakeSource extends FakeNode {
  constructor(ctx) {
    super(ctx);
    this.buffer = null;
    this.loop = false;
    this.playbackRate = new FakeParam(1);
    this.started = null;
    this.stopped = null;
    this.onended = null;
    ctx.sources.push(this);
  }

  start(when = 0, offset = 0) {
    if (this.started) throw new Error('InvalidStateError: start called twice');
    this.started = { when, offset };
  }

  stop(when = 0) {
    if (!this.started) throw new Error('InvalidStateError: not started');
    this.stopped = { when };
  }

  // Test helper: the source finished playing
  end() {
    if (this.onended) this.onended();
  }
}

class FakeGain extends FakeNode {
  constructor(ctx) {
    super(ctx);
    this.gain = new FakeParam(1);
  }
}

class FakeAnalyser extends FakeNode {
  constructor(ctx) {
    super(ctx);
    this.fftSize = 2048;
    this.signal = 0;
  }

  get frequencyBinCount() { return this.fftSize / 2; }
  getFloatTimeDomainData(array) { array.fill(this.signal); }
  getFloatFrequencyData(array) { array.fill(-20); }
}

class FakeScriptProcessor extends FakeNode {
  constructor(ctx, size, inputs) {
    super(ctx);
    this.bufferSize = size;
    this.inputs = inputs;
    this.onaudioprocess = null;
    ctx.processors.push(this);
  }

  // Test helper: delivers planar input samples like the audio thread would
  emit(channels) {
    if (!this.onaudioprocess) return;
    this.onaudioprocess({
      inputBuffer: { numberOfChannels: channels.length, getChannelData: (i) => channels[i] },
    });
  }
}

function decodeWav16(buffer) {
  const view = new DataView(buffer);
  const channels = view.getUint16(22, true);
  const sampleRate = view.getUint32(24, true);
  const bytes = view.getUint32(40, true);
  const frames = bytes / (2 * channels);
  const out = new FakeAudioBuffer(channels, frames, sampleRate);
  let offset = 44;
  for (let f = 0; f < frames; f++) {
    for (let c = 0; c < channels; c++) {
      out.data[c][f] = view.getInt16(offset, true) / 0x8000;
      offset += 2;
    }
  }
  return out;
}

export class FakeAudioContext {
  constructor(options) {
    this.options = options;
    this.currentTime = 0;
    this.sampleRate = 48000;
    this.state = 'suspended';
    this.outputLatency = 0.02;
    this.baseLatency = 0.005;
    this.destination = new FakeNode(this);
    this.sources = [];
    this.processors = [];
    this.activeDecodes = 0;
    this.maxConcurrentDecodes = 0;
    this.decodeCount = 0;
    this.maxBufferFrames = Infinity;
  }

  addEventListener() {}

  resume() {
    this.state = 'running';
    return Promise.resolve();
  }

  createGain() { return new FakeGain(this); }
  createAnalyser() { return new FakeAnalyser(this); }
  createBufferSource() { return new FakeSource(this); }
  createScriptProcessor(size, inputs) { return new FakeScriptProcessor(this, size, inputs); }
  createMediaStreamSource(stream) { const node = new FakeNode(this); node.stream = stream; return node; }

  createBuffer(channels, length, sampleRate) {
    if (length > this.maxBufferFrames) {
      throw new RangeError('Array buffer allocation failed');
    }
    return new FakeAudioBuffer(channels, length, sampleRate);
  }

  decodeAudioData(buffer) {
    this.activeDecodes++;
    this.decodeCount++;
    this.maxConcurrentDecodes = Math.max(this.maxConcurrentDecodes, this.activeDecodes);
    const tag = Buffer.from(buffer.slice(0, 4)).toString('ascii');
    return new Promise((resolve, reject) => {
      setImmediate(() => {
        this.activeDecodes--;
        if (tag === 'FAIL') {
          const error = new Error('Unable to decode audio data');
          error.name = 'EncodingError';
          reject(error);
        } else if (tag === 'OOMM') {
          reject(new RangeError('Array buffer allocation failed'));
        } else {
          resolve(decodeWav16(buffer));
        }
      });
    });
  }
}

export class FakeMediaDevices {
  constructor({ deny = false } = {}) {
    this.deny = deny;
    this.requests = [];
    this.devices = [
      { kind: 'audioinput', deviceId: 'default', label: '' },
      { kind: 'audiooutput', deviceId: 'out', label: 'Speakers' },
      { kind: 'audioinput', deviceId: 'usb-1', label: 'USB Mic' },
    ];
  }

  addEventListener() {}

  enumerateDevices() {
    return Promise.resolve(this.devices);
  }

  getUserMedia(constraints) {
    this.requests.push(constraints);
    if (this.deny) {
      const error = new Error('Permission denied');
      error.name = 'NotAllowedError';
      return Promise.reject(error);
    }
    const track = { stopped: false, stop() { this.stopped = true; } };
    return Promise.resolve({ track, getTracks: () => [track] });
  }
}
