// Web Audio backend for the web build (see docs/superpowers/specs/2026-11-01-web-port-2-audio-design.md).
// One AudioContext, exposed as Module.YargAudio.ctx. YargWebAudio.jslib holds the thin WebAudio_* functions
// that the C# side (Assets/Script/Audio/Web) calls; everything else lives here so the pure parts can be tested
// with node (Tests/WebAudio): outside the Unity framework there is no Module, and this file only exports them.
(function () {
    "use strict";

    // ---------------------------------------------------------------------------------------------------------
    // Pure helpers (no Web Audio objects; tested with node)
    // ---------------------------------------------------------------------------------------------------------

    // Song position from the context clock: (now - t0) * rate + offset. Before t0 (a start scheduled slightly
    // in the future) the position holds at offset.
    function SongClock() {
        this.playing = false;
        this.t0 = 0;
        this.offset = 0;
        this.rate = 1;
    }

    SongClock.prototype.position = function (now) {
        if (!this.playing) {
            return this.offset;
        }
        return this.offset + Math.max(0, now - this.t0) * this.rate;
    };

    SongClock.prototype.start = function (when) {
        this.t0 = when;
        this.playing = true;
    };

    SongClock.prototype.pause = function (now) {
        this.offset = this.position(now);
        this.playing = false;
    };

    SongClock.prototype.seek = function (position) {
        this.offset = position;
    };

    // Re-anchors so the position stays continuous across the rate change
    SongClock.prototype.setRate = function (now, rate) {
        if (this.playing && now > this.t0) {
            this.offset = this.position(now);
            this.t0 = now;
        }
        this.rate = rate;
    };

    // Context time at which the given song position is (or was) reached while playing
    SongClock.prototype.contextTimeOf = function (position) {
        return this.t0 + (position - this.offset) / this.rate;
    };

    // Mono ring buffer of float samples. Writing past capacity drops the oldest samples.
    function RingBuffer(capacity) {
        this.data = new Float32Array(Math.max(1, capacity | 0));
        this.readIndex = 0;
        this.count = 0;
        this.dropped = 0;
    }

    RingBuffer.prototype.write = function (samples) {
        var capacity = this.data.length;
        var n = samples.length;
        var start = 0;
        if (n > capacity) {
            this.dropped += n - capacity;
            start = n - capacity;
        }
        var overflow = this.count + (n - start) - capacity;
        if (overflow > 0) {
            this.readIndex = (this.readIndex + overflow) % capacity;
            this.count -= overflow;
            this.dropped += overflow;
        }
        var writeIndex = (this.readIndex + this.count) % capacity;
        for (var i = start; i < n; i++) {
            this.data[writeIndex] = samples[i];
            writeIndex = writeIndex + 1 === capacity ? 0 : writeIndex + 1;
        }
        this.count += n - start;
    };

    RingBuffer.prototype.read = function (destination, max) {
        var n = Math.min(this.count, max === undefined ? destination.length : max, destination.length);
        var capacity = this.data.length;
        for (var i = 0; i < n; i++) {
            destination[i] = this.data[this.readIndex];
            this.readIndex = this.readIndex + 1 === capacity ? 0 : this.readIndex + 1;
        }
        this.count -= n;
        return n;
    };

    RingBuffer.prototype.clear = function () {
        this.readIndex = 0;
        this.count = 0;
    };

    // 16 bit PCM WAV from planar float channels
    function encodeWav(channels, sampleRate) {
        var channelCount = channels.length;
        var frames = channelCount > 0 ? channels[0].length : 0;
        var dataBytes = frames * channelCount * 2;
        var buffer = new ArrayBuffer(44 + dataBytes);
        var view = new DataView(buffer);

        function ascii(offset, text) {
            for (var i = 0; i < text.length; i++) {
                view.setUint8(offset + i, text.charCodeAt(i));
            }
        }

        ascii(0, "RIFF");
        view.setUint32(4, 36 + dataBytes, true);
        ascii(8, "WAVE");
        ascii(12, "fmt ");
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, channelCount, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * channelCount * 2, true);
        view.setUint16(32, channelCount * 2, true);
        view.setUint16(34, 16, true);
        ascii(36, "data");
        view.setUint32(40, dataBytes, true);

        var offset = 44;
        for (var f = 0; f < frames; f++) {
            for (var c = 0; c < channelCount; c++) {
                var s = Math.max(-1, Math.min(1, channels[c][f]));
                view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
                offset += 2;
            }
        }
        return buffer;
    }

    // Reads a 16 bit PCM WAV back into planar floats (used by the tests)
    function decodeWav(buffer) {
        var view = new DataView(buffer);
        var channelCount = view.getUint16(22, true);
        var sampleRate = view.getUint32(24, true);
        var dataBytes = view.getUint32(40, true);
        var frames = dataBytes / (2 * channelCount);
        var channels = [];
        for (var c = 0; c < channelCount; c++) {
            channels.push(new Float32Array(frames));
        }
        var offset = 44;
        for (var f = 0; f < frames; f++) {
            for (var k = 0; k < channelCount; k++) {
                channels[k][f] = view.getInt16(offset, true) / 0x8000;
                offset += 2;
            }
        }
        return { sampleRate: sampleRate, channels: channels };
    }

    // Builds the stereo pair of one stem from a decoded source, like BassHelpers.BuildVolumeMatrix:
    // panning[2i] is the left gain and panning[2i + 1] the right gain of source channel indices[i].
    // Without indices a mono source goes to both sides and a source with more channels keeps its first two.
    function mixToStereo(sourceChannels, indices, panning, gain, left, right) {
        var frames = left.length;
        var g = gain === undefined ? 1 : gain;
        var i, f, src;
        if (!indices || indices.length === 0) {
            var l = sourceChannels[0];
            var r = sourceChannels.length > 1 ? sourceChannels[1] : sourceChannels[0];
            var n = Math.min(frames, l.length);
            for (f = 0; f < n; f++) {
                left[f] += l[f] * g;
                right[f] += r[f] * g;
            }
            return;
        }
        for (i = 0; i < indices.length; i++) {
            src = sourceChannels[indices[i]];
            if (!src) {
                continue;
            }
            var lg = (panning && panning.length > 2 * i ? panning[2 * i] : 1) * g;
            var rg = (panning && panning.length > 2 * i + 1 ? panning[2 * i + 1] : 1) * g;
            var count = Math.min(frames, src.length);
            for (f = 0; f < count; f++) {
                left[f] += src[f] * lg;
                right[f] += src[f] * rg;
            }
        }
    }

    // Index of the first time >= value in a sorted array
    function lowerBound(times, value) {
        var lo = 0;
        var hi = times.length;
        while (lo < hi) {
            var mid = (lo + hi) >> 1;
            if (times[mid] < value) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }
        return lo;
    }

    // One-shot plays that fall in [from, until) of song time, starting at index next: returns the end index
    function dueUntil(times, next, until) {
        var i = next;
        while (i < times.length && times[i] < until) {
            i++;
        }
        return i;
    }

    function measure(samples) {
        var sum = 0;
        var peak = 0;
        for (var i = 0; i < samples.length; i++) {
            var s = samples[i];
            sum += s * s;
            var a = s < 0 ? -s : s;
            if (a > peak) {
                peak = a;
            }
        }
        return { rms: samples.length > 0 ? Math.sqrt(sum / samples.length) : 0, peak: peak };
    }

    // Stereo sine WAV used by the tone test: a pure tone, louder on the left so a swapped matrix shows up
    function makeToneWav(sampleRate, seconds, frequency, amplitude) {
        var frames = Math.round(sampleRate * seconds);
        var a = amplitude === undefined ? 0.5 : amplitude;
        var left = new Float32Array(frames);
        var right = new Float32Array(frames);
        var fade = Math.min(frames >> 1, Math.round(sampleRate * 0.01));
        for (var f = 0; f < frames; f++) {
            var env = 1;
            if (f < fade) {
                env = f / fade;
            } else if (f >= frames - fade) {
                env = (frames - 1 - f) / fade;
            }
            var s = Math.sin(2 * Math.PI * frequency * f / sampleRate) * a * env;
            left[f] = s;
            right[f] = s * 0.8;
        }
        return encodeWav([left, right], sampleRate);
    }

    // Rough pitch of a signal from its zero crossings (tests and debug only; the game uses PitchTracker)
    function zeroCrossingPitch(samples, sampleRate) {
        var crossings = 0;
        var first = -1;
        var last = -1;
        for (var i = 1; i < samples.length; i++) {
            if (samples[i - 1] < 0 && samples[i] >= 0) {
                if (first < 0) {
                    first = i;
                }
                last = i;
                crossings++;
            }
        }
        if (crossings < 2) {
            return 0;
        }
        return (crossings - 1) * sampleRate / (last - first);
    }

    // Frame time statistics over a rolling window of requestAnimationFrame deltas
    function FrameStats(windowSize) {
        this.size = windowSize;
        this.deltas = [];
        this.last = -1;
        this.frames = 0;
    }

    FrameStats.prototype.tick = function (now) {
        if (this.last >= 0) {
            this.deltas.push(now - this.last);
            if (this.deltas.length > this.size) {
                this.deltas.shift();
            }
        }
        this.last = now;
        this.frames++;
    };

    FrameStats.prototype.snapshot = function () {
        var n = this.deltas.length;
        // Quoted keys: read by the tests from outside the (possibly minified) framework
        if (n === 0) {
            return { "frames": this.frames, "samples": 0, "avgFrameMs": 0, "p95FrameMs": 0, "maxFrameMs": 0, "fps": 0 };
        }
        var sorted = this.deltas.slice().sort(function (a, b) { return a - b; });
        var sum = 0;
        for (var i = 0; i < n; i++) {
            sum += sorted[i];
        }
        var avg = sum / n;
        return {
            "frames": this.frames,
            "samples": n,
            "avgFrameMs": avg,
            "p95FrameMs": sorted[Math.min(n - 1, Math.floor(n * 0.95))],
            "maxFrameMs": sorted[n - 1],
            "fps": avg > 0 ? 1000 / avg : 0,
        };
    };

    var pure = {
        SongClock: SongClock,
        RingBuffer: RingBuffer,
        encodeWav: encodeWav,
        decodeWav: decodeWav,
        mixToStereo: mixToStereo,
        lowerBound: lowerBound,
        dueUntil: dueUntil,
        measure: measure,
        makeToneWav: makeToneWav,
        zeroCrossingPitch: zeroCrossingPitch,
        FrameStats: FrameStats,
    };

    if (typeof Module === "undefined") {
        if (typeof module !== "undefined" && module.exports) {
            module.exports = pure;
        }
        return;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Browser backend
    // ---------------------------------------------------------------------------------------------------------

    var START_LEAD = 0.05;          // seconds between a play request and the scheduled start
    var STEM_RAMP = 0.015;          // time constant of stem volume changes
    var ONE_SHOT_LOOKAHEAD = 0.5;   // seconds of one-shots scheduled ahead of the song position
    var MIC_RING_SECONDS = 2;
    var TAP_BLOCK = 1024;           // samples per message from the worklet taps

    var MEMORY_ERROR = "This song is too big for the browser's memory. Try a shorter song or one with fewer stems.";
    var DECODE_ERROR = "The browser could not decode the audio of this song.";

    var WORKLET_SOURCE = [
        "class YargTap extends AudioWorkletProcessor {",
        "  constructor(options) {",
        "    super();",
        "    this.channels = (options.processorOptions && options.processorOptions.channels) || 1;",
        "    this.block = (options.processorOptions && options.processorOptions.block) || " + TAP_BLOCK + ";",
        "    this.buffers = [];",
        "    for (let c = 0; c < this.channels; c++) this.buffers.push(new Float32Array(this.block));",
        "    this.fill = 0;",
        "  }",
        "  process(inputs) {",
        "    const input = inputs[0];",
        "    if (!input || input.length === 0) return true;",
        "    const frames = input[0].length;",
        "    let offset = 0;",
        "    while (offset < frames) {",
        "      const n = Math.min(frames - offset, this.block - this.fill);",
        "      for (let c = 0; c < this.channels; c++) {",
        "        const src = input[Math.min(c, input.length - 1)];",
        "        this.buffers[c].set(src.subarray(offset, offset + n), this.fill);",
        "      }",
        "      this.fill += n;",
        "      offset += n;",
        "      if (this.fill === this.block) {",
        "        const out = this.buffers;",
        "        this.port.postMessage(out, out.map((b) => b.buffer));",
        "        this.buffers = [];",
        "        for (let c = 0; c < this.channels; c++) this.buffers.push(new Float32Array(this.block));",
        "        this.fill = 0;",
        "      }",
        "    }",
        "    return true;",
        "  }",
        "}",
        "registerProcessor('yarg-tap', YargTap);",
    ].join("\n");

    var ctx = null;
    var contextFailed = false;
    var master = null;         // { gain, analyser }
    var silentSink = null;      // keeps taps pulled by the graph without making sound
    var workletReady = null;    // Promise<boolean>
    var nextHandle = 1;
    var mixers = {};
    var stems = {};
    var samples = {};
    var oneShots = {};
    var mics = {};
    var micDevices = [];
    var voxQueueEnd = 0;
    var lastPlayedMixer = 0;
    var decodeChain = Promise.resolve();
    var debugRequests = [];
    var debugToneWav = null;
    var scratch = new Float32Array(2048);

    function log(message) {
        console.log("[YargAudio] " + message);
    }

    function warn(message, error) {
        console.warn("[YargAudio] " + message + (error ? ": " + (error.message || error) : ""));
    }

    function handle() {
        return nextHandle++;
    }

    function ensureContext() {
        if (ctx) {
            return ctx;
        }
        if (contextFailed) {
            return null;
        }
        var Ctor = window.AudioContext || window["webkitAudioContext"];
        try {
            if (!Ctor) {
                throw new Error("Web Audio is not available in this browser");
            }
            ctx = new Ctor({ "latencyHint": "interactive" });
        } catch (e) {
            // Without audio the game cannot be played: show the template's error screen
            contextFailed = true;
            warn("Failed to create the AudioContext", e);
            if (typeof window["yargShowError"] === "function") {
                window["yargShowError"]("Não foi possível iniciar o áudio do navegador: " + (e.message || e));
            }
            return null;
        }

        master = { gain: ctx.createGain(), analyser: ctx.createAnalyser() };
        master.analyser.fftSize = 2048;
        master.gain.connect(master.analyser);
        master.analyser.connect(ctx.destination);

        silentSink = ctx.createGain();
        silentSink.gain.value = 0;
        silentSink.connect(ctx.destination);

        ctx.addEventListener("statechange", function () {
            log("AudioContext " + ctx.state);
        });
        log("AudioContext created: " + ctx.sampleRate + " Hz, " + ctx.state);
        resumeIfActivated();
        return ctx;
    }

    // The page already had a user gesture (the #yarg-start click may come before this script runs): browsers let
    // a context resume without a new one
    function resumeIfActivated() {
        var activation = navigator["userActivation"];
        var activated = window["yargUserGesture"] === true || (activation && activation["hasBeenActive"]);
        if (ctx && activated && ctx.state === "suspended") {
            ctx.resume().catch(function (e) { warn("resume failed", e); });
        }
    }

    // Autoplay: the context can only start from a user gesture, such as the click on #yarg-start
    function unlock() {
        var c = ensureContext();
        if (c && c.state !== "running" && c.state !== "closed") {
            c.resume().catch(function (e) { warn("resume failed", e); });
        }
    }

    function loadWorklet() {
        if (workletReady) {
            return workletReady;
        }
        if (!ctx.audioWorklet) {
            workletReady = Promise.resolve(false);
            return workletReady;
        }
        var url = URL.createObjectURL(new Blob([WORKLET_SOURCE], { "type": "application/javascript" }));
        workletReady = ctx.audioWorklet.addModule(url).then(function () {
            return true;
        }, function (e) {
            warn("AudioWorklet unavailable, using ScriptProcessor", e);
            return false;
        });
        return workletReady;
    }

    // Calls onBlock(Float32Array[] channels) with blocks of the input's samples. Returns { node, stop }.
    function createTap(channelCount, onBlock) {
        return loadWorklet().then(function (useWorklet) {
            var node;
            if (useWorklet) {
                // Quoted keys: the worklet source is not part of the framework, so these names must survive
                // minification
                node = new AudioWorkletNode(ctx, "yarg-tap", {
                    "numberOfInputs": 1,
                    "numberOfOutputs": 1,
                    "outputChannelCount": [1],
                    "processorOptions": { "channels": channelCount, "block": TAP_BLOCK },
                });
                node.port.onmessage = function (e) { onBlock(e.data); };
            } else {
                node = ctx.createScriptProcessor(TAP_BLOCK, channelCount, 1);
                node.onaudioprocess = function (e) {
                    var blocks = [];
                    for (var c = 0; c < channelCount; c++) {
                        var index = Math.min(c, e.inputBuffer.numberOfChannels - 1);
                        blocks.push(new Float32Array(e.inputBuffer.getChannelData(index)));
                    }
                    onBlock(blocks);
                };
            }
            node.connect(silentSink);
            return {
                node: node,
                stop: function () {
                    try {
                        node.disconnect();
                    } catch (e) { /* already disconnected */ }
                    if (node.port) {
                        node.port.onmessage = null;
                    } else {
                        node.onaudioprocess = null;
                    }
                },
            };
        });
    }

    function readAnalyser(analyser) {
        if (scratch.length !== analyser.fftSize) {
            scratch = new Float32Array(analyser.fftSize);
        }
        analyser.getFloatTimeDomainData(scratch);
        return measure(scratch);
    }

    function now() {
        return ctx ? ctx.currentTime : 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Mixers and stems
    // ---------------------------------------------------------------------------------------------------------

    function createMixer(volume, speed) {
        if (!ensureContext()) {
            return 0;
        }
        var id = handle();
        var mixer = {
            id: id,
            gain: ctx.createGain(),
            analyser: ctx.createAnalyser(),
            clock: new SongClock(),
            stems: [],
            oneShots: [],
            length: 0,
            loading: 0,
            error: null,
            playRequested: false,
            ended: false,
            endReported: false,
        };
        mixer.gain.gain.value = volume;
        mixer.analyser.fftSize = 2048;
        mixer.gain.connect(mixer.analyser);
        mixer.analyser.connect(master.gain);
        mixer.clock.rate = speed > 0 ? speed : 1;
        mixers[id] = mixer;
        return id;
    }

    function freeMixer(id) {
        var mixer = mixers[id];
        if (!mixer) {
            return;
        }
        stopSources(mixer);
        mixer.stems.slice().forEach(function (stem) { freeStem(stem.id); });
        mixer.oneShots.slice().forEach(function (shot) { freeOneShot(shot.id); });
        mixer.gain.disconnect();
        mixer.analyser.disconnect();
        delete mixers[id];
        if (lastPlayedMixer === id) {
            lastPlayedMixer = 0;
        }
    }

    function createStem(mixerId, stemId, volume) {
        var mixer = mixers[mixerId];
        if (!mixer) {
            return 0;
        }
        var id = handle();
        var stem = {
            id: id,
            mixer: mixer,
            stemId: stemId,
            buffer: null,
            gain: ctx.createGain(),
            analyser: ctx.createAnalyser(),
            source: null,
            volume: volume,
            parts: 0,
        };
        stem.gain.gain.value = volume;
        stem.analyser.fftSize = 2048;
        stem.gain.connect(stem.analyser);
        stem.analyser.connect(mixer.gain);
        mixer.stems.push(stem);
        stems[id] = stem;
        return id;
    }

    function freeStem(id) {
        var stem = stems[id];
        if (!stem) {
            return;
        }
        stopStemSource(stem);
        stem.gain.disconnect();
        stem.analyser.disconnect();
        var list = stem.mixer.stems;
        var index = list.indexOf(stem);
        if (index >= 0) {
            list.splice(index, 1);
        }
        stem.buffer = null;
        delete stems[id];
    }

    // Makes the stem buffer at least frames long (zeroed at first), keeping what it already holds. The decoded
    // audio is then mixed straight into it, so a stem costs no more than its own stereo buffer.
    function ensureStemFrames(stem, frames) {
        var old = stem.buffer;
        if (old && old.length >= frames) {
            return;
        }
        var grown = ctx.createBuffer(2, frames, ctx.sampleRate);
        if (old) {
            grown.getChannelData(0).set(old.getChannelData(0));
            grown.getChannelData(1).set(old.getChannelData(1));
        }
        stem.buffer = grown;
        if (grown.duration > stem.mixer.length) {
            stem.mixer.length = grown.duration;
        }
    }

    function isMemoryError(e) {
        var name = e && e.name ? e.name : "";
        var message = e && e.message ? String(e.message).toLowerCase() : "";
        return name === "RangeError" || name === "NotSupportedError" || message.indexOf("memory") >= 0 ||
            message.indexOf("allocation") >= 0;
    }

    // Queues bytes for decoding. targets: [{ stem, indices, panning, gain }]. Decoding runs one file at a time,
    // so a decoded file is summed into its stems and dropped before the next one is decoded.
    function queueDecode(mixer, bytes, targets) {
        mixer.loading++;
        decodeChain = decodeChain.then(function () {
            if (!mixers[mixer.id]) {
                return null;
            }
            return ctx.decodeAudioData(bytes.buffer).then(function (decoded) {
                bytes = null;
                var sourceChannels = [];
                for (var c = 0; c < decoded.numberOfChannels; c++) {
                    sourceChannels.push(decoded.getChannelData(c));
                }
                targets.forEach(function (target) {
                    var stem = stems[target.stem];
                    if (!stem) {
                        return;
                    }
                    ensureStemFrames(stem, decoded.length);
                    var left = stem.buffer.getChannelData(0).subarray(0, decoded.length);
                    var right = stem.buffer.getChannelData(1).subarray(0, decoded.length);
                    mixToStereo(sourceChannels, target.indices, target.panning, target.gain, left, right);
                    stem.parts++;
                });
            });
        }).catch(function (e) {
            bytes = null;
            if (isMemoryError(e)) {
                mixer.error = MEMORY_ERROR;
            } else if (!mixer.error) {
                // A stem that does not decode is skipped; the song only fails when nothing decodes
                mixer.decodeFailures = (mixer.decodeFailures || 0) + 1;
            }
            warn("decode failed", e);
        }).then(function () {
            mixer.loading--;
            if (mixer.loading === 0) {
                finishLoading(mixer);
            }
        });
    }

    function finishLoading(mixer) {
        if (!mixers[mixer.id]) {
            return;
        }
        var decoded = mixer.stems.some(function (stem) { return stem.buffer !== null; });
        if (!decoded && !mixer.error) {
            mixer.error = DECODE_ERROR;
        }
        log("mixer " + mixer.id + " loaded: " + mixer.stems.length + " stems, " + mixer.length.toFixed(2) + " s" +
            (mixer.error ? ", error: " + mixer.error : ""));
        if (mixer.playRequested && !mixer.error) {
            mixer.playRequested = false;
            startSources(mixer);
        }
    }

    function stopStemSource(stem) {
        if (!stem.source) {
            return;
        }
        try {
            stem.source.stop();
        } catch (e) { /* not started */ }
        stem.source.disconnect();
        stem.source = null;
    }

    function stopSources(mixer) {
        mixer.stems.forEach(stopStemSource);
        mixer.oneShots.forEach(cancelOneShot);
    }

    function startSources(mixer) {
        var clock = mixer.clock;
        var when = now() + START_LEAD;
        clock.start(when);
        mixer.ended = false;
        mixer.endReported = false;
        lastPlayedMixer = mixer.id;

        var position = clock.offset;
        mixer.stems.forEach(function (stem) {
            stopStemSource(stem);
            if (!stem.buffer || position >= stem.buffer.duration) {
                return;
            }
            var source = ctx.createBufferSource();
            source.buffer = stem.buffer;
            source.playbackRate.value = clock.rate;
            source.connect(stem.gain);
            if (position >= 0) {
                source.start(when, position);
            } else {
                source.start(when - position / clock.rate, 0);
            }
            stem.source = source;
        });
        mixer.oneShots.forEach(resetOneShot);
    }

    function play(id) {
        var mixer = mixers[id];
        if (!mixer) {
            return -1;
        }
        if (mixer.error) {
            return -1;
        }
        if (mixer.clock.playing) {
            return 0;
        }
        if (mixer.loading > 0) {
            mixer.playRequested = true;
            return 0;
        }
        startSources(mixer);
        return 0;
    }

    function pause(id) {
        var mixer = mixers[id];
        if (!mixer) {
            return -1;
        }
        mixer.playRequested = false;
        if (!mixer.clock.playing) {
            return 0;
        }
        mixer.clock.pause(now());
        stopSources(mixer);
        return 0;
    }

    function setPosition(id, position) {
        var mixer = mixers[id];
        if (!mixer) {
            return;
        }
        var wasPlaying = mixer.clock.playing;
        if (wasPlaying) {
            mixer.clock.pause(now());
            stopSources(mixer);
        }
        mixer.clock.seek(position);
        mixer.ended = false;
        mixer.endReported = false;
        if (wasPlaying) {
            startSources(mixer);
        }
    }

    function getPosition(id) {
        var mixer = mixers[id];
        return mixer ? mixer.clock.position(now()) : 0;
    }

    function setSpeed(id, rate) {
        var mixer = mixers[id];
        if (!mixer || !(rate > 0) || rate === mixer.clock.rate) {
            return;
        }
        var t = now();
        mixer.clock.setRate(t, rate);
        mixer.stems.forEach(function (stem) {
            if (stem.source) {
                stem.source.playbackRate.setValueAtTime(rate, t);
            }
        });
        if (mixer.clock.playing) {
            mixer.oneShots.forEach(resetOneShot);
        }
    }

    function setMixerVolume(id, volume) {
        var mixer = mixers[id];
        if (!mixer) {
            return;
        }
        var g = mixer.gain.gain;
        g.cancelScheduledValues(now());
        g.setValueAtTime(volume, now());
    }

    function fadeMixer(id, target, seconds) {
        var mixer = mixers[id];
        if (!mixer) {
            return;
        }
        var t = now();
        var g = mixer.gain.gain;
        g.cancelScheduledValues(t);
        g.setValueAtTime(g.value, t);
        g.linearRampToValueAtTime(target, t + Math.max(0.001, seconds));
    }

    function setStemVolume(id, volume) {
        var stem = stems[id];
        if (!stem) {
            return;
        }
        stem.volume = volume;
        var t = now();
        stem.gain.gain.cancelScheduledValues(t);
        stem.gain.gain.setTargetAtTime(volume, t, STEM_RAMP);
    }

    // Song end, polled by C# once per frame
    function takeEnded(id) {
        var mixer = mixers[id];
        if (!mixer || mixer.endReported || !mixer.clock.playing || mixer.length <= 0) {
            return 0;
        }
        if (mixer.clock.position(now()) >= mixer.length) {
            mixer.ended = true;
            mixer.endReported = true;
            return 1;
        }
        return 0;
    }

    // Analysis for the visualizers: time domain samples or magnitudes in [0, 1]
    function getSamples(id, out) {
        var mixer = mixers[id];
        if (!mixer) {
            return -1;
        }
        var n = Math.min(out.length, mixer.analyser.fftSize);
        var data = new Float32Array(mixer.analyser.fftSize);
        mixer.analyser.getFloatTimeDomainData(data);
        out.set(data.subarray(0, n));
        return n;
    }

    function getFft(id, out, log2Size) {
        var mixer = mixers[id];
        if (!mixer) {
            return -1;
        }
        var size = Math.max(32, Math.min(32768, 1 << log2Size));
        if (mixer.analyser.fftSize !== size) {
            mixer.analyser.fftSize = size;
        }
        var bins = new Float32Array(mixer.analyser.frequencyBinCount);
        mixer.analyser.getFloatFrequencyData(bins);
        var n = Math.min(out.length, bins.length);
        for (var i = 0; i < n; i++) {
            var db = bins[i];
            out[i] = isFinite(db) ? Math.min(1, Math.pow(10, db / 20)) : 0;
        }
        return n;
    }

    // ---------------------------------------------------------------------------------------------------------
    // One-shots scheduled on the song timeline (metronome, crowd claps)
    // ---------------------------------------------------------------------------------------------------------

    function createOneShot(mixerId, sampleId, times, lead) {
        var mixer = mixers[mixerId];
        if (!mixer) {
            return 0;
        }
        var id = handle();
        var shot = {
            id: id,
            mixer: mixer,
            sample: sampleId,
            times: sortedCopy(times),
            lead: lead,
            next: 0,
            enabled: true,
            gain: ctx.createGain(),
            nodes: [],
        };
        shot.gain.connect(mixer.gain);
        mixer.oneShots.push(shot);
        oneShots[id] = shot;
        if (mixer.clock.playing) {
            resetOneShot(shot);
        }
        return id;
    }

    function sortedCopy(times) {
        var copy = Array.prototype.slice.call(times);
        copy.sort(function (a, b) { return a - b; });
        return copy;
    }

    function cancelOneShot(shot) {
        shot.nodes.forEach(function (node) {
            try {
                node.stop();
            } catch (e) { /* not started */ }
            node.disconnect();
        });
        shot.nodes = [];
    }

    function resetOneShot(shot) {
        cancelOneShot(shot);
        var position = shot.mixer.clock.position(now());
        shot.next = lowerBound(shot.times, position);
    }

    function scheduleOneShots(mixer) {
        if (!mixer.clock.playing) {
            return;
        }
        var t = now();
        var position = mixer.clock.position(t);
        var until = position + ONE_SHOT_LOOKAHEAD * mixer.clock.rate;
        mixer.oneShots.forEach(function (shot) {
            var end = dueUntil(shot.times, shot.next, until);
            var sample = samples[shot.sample];
            for (var i = shot.next; i < end; i++) {
                if (!shot.enabled || !sample || !sample.buffer) {
                    continue;
                }
                var when = mixer.clock.contextTimeOf(shot.times[i]) - shot.lead;
                if (when < t) {
                    continue;
                }
                var node = ctx.createBufferSource();
                node.buffer = sample.buffer;
                node.connect(shot.gain);
                node.start(when);
                node.onended = removeNode(shot, node);
                shot.nodes.push(node);
            }
            shot.next = end;
        });
    }

    function removeNode(shot, node) {
        return function () {
            var index = shot.nodes.indexOf(node);
            if (index >= 0) {
                shot.nodes.splice(index, 1);
            }
            node.disconnect();
        };
    }

    function setOneShotSchedule(id, times) {
        var shot = oneShots[id];
        if (!shot) {
            return;
        }
        shot.times = sortedCopy(times);
        resetOneShot(shot);
    }

    function freeOneShot(id) {
        var shot = oneShots[id];
        if (!shot) {
            return;
        }
        cancelOneShot(shot);
        shot.gain.disconnect();
        var list = shot.mixer.oneShots;
        var index = list.indexOf(shot);
        if (index >= 0) {
            list.splice(index, 1);
        }
        delete oneShots[id];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Samples (sound effects, drum samples, vox, metronome, venue): preloaded AudioBuffers played as one-shots
    // ---------------------------------------------------------------------------------------------------------

    function loadSample(bytes) {
        if (!ensureContext()) {
            return 0;
        }
        var id = handle();
        var sample = { id: id, buffer: null, gain: ctx.createGain(), voices: [], paused: [] };
        sample.gain.connect(master.gain);
        samples[id] = sample;
        ctx.decodeAudioData(bytes.buffer).then(function (buffer) {
            sample.buffer = buffer;
        }, function (e) {
            warn("sample decode failed", e);
        });
        return id;
    }

    function startVoice(sample, volume, loop, offset, when) {
        var source = ctx.createBufferSource();
        source.buffer = sample.buffer;
        source.loop = !!loop;
        var gain = ctx.createGain();
        gain.gain.value = volume;
        source.connect(gain);
        gain.connect(sample.gain);
        var voice = { source: source, gain: gain, loop: !!loop, volume: volume, startedAt: when - offset };
        source.onended = function () {
            var index = sample.voices.indexOf(voice);
            if (index >= 0) {
                sample.voices.splice(index, 1);
            }
            gain.disconnect();
        };
        source.start(when, offset);
        sample.voices.push(voice);
        return voice;
    }

    function playSample(id, volume, loop, queued) {
        var sample = samples[id];
        if (!sample || !sample.buffer) {
            return 0;
        }
        var t = now();
        var when = t;
        if (queued) {
            when = Math.max(t, voxQueueEnd);
            voxQueueEnd = when + sample.buffer.duration;
        }
        startVoice(sample, volume, loop, 0, when);
        return 1;
    }

    function stopSample(id, fade) {
        var sample = samples[id];
        if (!sample) {
            return;
        }
        var t = now();
        sample.paused = [];
        sample.voices.slice().forEach(function (voice) {
            if (fade > 0) {
                voice.gain.gain.setValueAtTime(voice.gain.gain.value, t);
                voice.gain.gain.linearRampToValueAtTime(0, t + fade);
                voice.source.stop(t + fade);
            } else {
                voice.source.stop();
            }
        });
    }

    function pauseSample(id) {
        var sample = samples[id];
        if (!sample || !sample.buffer) {
            return;
        }
        var t = now();
        var duration = sample.buffer.duration;
        sample.paused = sample.voices.map(function (voice) {
            var elapsed = Math.max(0, t - voice.startedAt);
            return { offset: voice.loop ? elapsed % duration : Math.min(elapsed, duration), loop: voice.loop,
                volume: voice.volume };
        }).filter(function (p) { return p.loop || p.offset < duration; });
        sample.voices.slice().forEach(function (voice) {
            voice.source.onended = null;
            voice.source.stop();
            voice.gain.disconnect();
        });
        sample.voices = [];
    }

    function resumeSample(id) {
        var sample = samples[id];
        if (!sample || !sample.buffer) {
            return;
        }
        var t = now();
        sample.paused.forEach(function (p) {
            startVoice(sample, p.volume, p.loop, p.offset, t);
        });
        sample.paused = [];
    }

    function setSampleVolume(id, volume) {
        var sample = samples[id];
        if (sample) {
            sample.gain.gain.setValueAtTime(volume, now());
        }
    }

    function sampleState(id) {
        var sample = samples[id];
        if (!sample) {
            return 0;
        }
        if (sample.voices.length > 0) {
            return 1;
        }
        return sample.paused.length > 0 ? 2 : 0;
    }

    function freeSample(id) {
        var sample = samples[id];
        if (!sample) {
            return;
        }
        sample.voices.slice().forEach(function (voice) {
            try {
                voice.source.stop();
            } catch (e) { /* not started */ }
        });
        sample.gain.disconnect();
        delete samples[id];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Microphone: getUserMedia -> worklet tap -> ring buffer, drained by C# every frame
    // ---------------------------------------------------------------------------------------------------------

    function refreshMicDevices() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
            return Promise.resolve(micDevices);
        }
        return navigator.mediaDevices.enumerateDevices().then(function (devices) {
            var inputs = devices.filter(function (d) { return d.kind === "audioinput"; });
            micDevices = inputs.map(function (d, i) {
                var label = d.label;
                if (!label) {
                    label = d.deviceId === "default" || i === 0 ? "Default microphone" : "Microphone " + (i + 1);
                }
                return { id: d.deviceId, name: label };
            });
            if (micDevices.length === 0) {
                micDevices = [{ id: "", name: "Default microphone" }];
            }
            return micDevices;
        }, function (e) {
            warn("enumerateDevices failed", e);
            return micDevices;
        });
    }

    function openMic(deviceId) {
        if (!ensureContext()) {
            return 0;
        }
        var id = handle();
        var mic = {
            id: id,
            state: 0,          // 0 opening, 1 open, -1 failed
            error: null,
            ring: new RingBuffer(Math.round(ctx.sampleRate * MIC_RING_SECONDS)),
            stream: null,
            source: null,
            tap: null,
            monitor: ctx.createGain(),
            rms: 0,
            peak: 0,
            blocks: 0,
            lastPitch: 0,
            lastDb: -160,
            pitchFrames: 0,
        };
        mic.monitor.gain.value = 0;
        mic.monitor.connect(ctx.destination);
        mics[id] = mic;

        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            mic.state = -1;
            mic.error = "getUserMedia unavailable";
            return id;
        }

        var audio = { "echoCancellation": false, "noiseSuppression": false, "autoGainControl": false };
        if (deviceId) {
            audio["deviceId"] = { "exact": deviceId };
        }
        navigator.mediaDevices.getUserMedia({ "audio": audio, "video": false }).then(function (stream) {
            if (!mics[id]) {
                stream.getTracks().forEach(function (track) { track.stop(); });
                return null;
            }
            mic.stream = stream;
            mic.source = ctx.createMediaStreamSource(stream);
            mic.source.connect(mic.monitor);
            return createTap(1, function (blocks) {
                var block = blocks[0];
                mic.ring.write(block);
                var m = measure(block);
                mic.rms = m.rms;
                mic.peak = m.peak;
                mic.blocks++;
            }).then(function (tap) {
                if (!mics[id]) {
                    tap.stop();
                    return;
                }
                mic.tap = tap;
                mic.source.connect(tap.node);
                mic.state = 1;
                log("microphone " + id + " open" + (deviceId ? " (" + deviceId + ")" : ""));
                refreshMicDevices();
            });
        }).catch(function (e) {
            mic.state = -1;
            mic.error = e && e.name ? e.name : String(e);
            warn("microphone failed", e);
        });
        return id;
    }

    function closeMic(id) {
        var mic = mics[id];
        if (!mic) {
            return;
        }
        if (mic.tap) {
            mic.tap.stop();
        }
        if (mic.source) {
            mic.source.disconnect();
        }
        if (mic.stream) {
            mic.stream.getTracks().forEach(function (track) { track.stop(); });
        }
        mic.monitor.disconnect();
        delete mics[id];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Debug and test hooks
    // ---------------------------------------------------------------------------------------------------------

    var recording = null;

    function mixerDebug(mixer) {
        return {
            "id": mixer.id,
            "playing": mixer.clock.playing,
            "loading": mixer.loading,
            "error": mixer.error,
            "length": mixer.length,
            "rate": mixer.clock.rate,
            "t0": mixer.clock.t0,
            "offset": mixer.clock.offset,
            "songTime": mixer.clock.position(now()),
            "gain": mixer.gain.gain.value,
            "rms": readAnalyser(mixer.analyser).rms,
            "peak": readAnalyser(mixer.analyser).peak,
            "stems": mixer.stems.map(function (stem) {
                var level = readAnalyser(stem.analyser);
                return {
                    "id": stem.id,
                    "stem": stem.stemId,
                    "parts": stem.parts,
                    "decoded": stem.buffer !== null,
                    "duration": stem.buffer ? stem.buffer.duration : 0,
                    "playing": stem.source !== null,
                    "gain": stem.gain.gain.value,
                    "playbackRate": stem.source ? stem.source.playbackRate.value : mixer.clock.rate,
                    "rms": level.rms,
                    "peak": level.peak,
                };
            }),
            "oneShots": mixer.oneShots.length,
        };
    }

    function activeMixer(id) {
        if (id && mixers[id]) {
            return mixers[id];
        }
        return mixers[lastPlayedMixer] || null;
    }

    var debug = {
        "state": function () {
            return {
                "created": ctx !== null,
                "state": ctx ? ctx.state : "none",
                "sampleRate": ctx ? ctx.sampleRate : 0,
                "currentTime": now(),
                "outputLatency": ctx && ctx.outputLatency ? ctx.outputLatency : 0,
                "baseLatency": ctx && ctx.baseLatency ? ctx.baseLatency : 0,
                "performanceNow": performance.now(),
                "mixers": Object.keys(mixers).length,
                "samples": Object.keys(samples).length,
                "mics": Object.keys(mics).length,
                "activeMixer": lastPlayedMixer,
            };
        },
        "mixers": function () {
            return Object.keys(mixers).map(function (k) { return mixerDebug(mixers[k]); });
        },
        // Stems of the given mixer, or of the last one that started playing
        "stems": function (mixerId) {
            var mixer = activeMixer(mixerId);
            return mixer ? mixerDebug(mixer)["stems"] : [];
        },
        "master": function () {
            if (!master) {
                return { "rms": 0, "peak": 0, "gain": 0 };
            }
            var level = readAnalyser(master.analyser);
            return { "rms": level.rms, "peak": level.peak, "gain": master.gain.gain.value };
        },
        // Song time computed from the context clock, with the raw values to check the math against
        "songTime": function (mixerId) {
            var mixer = activeMixer(mixerId);
            if (!mixer) {
                return null;
            }
            var t = now();
            return {
                "mixer": mixer.id,
                "songTime": mixer.clock.position(t),
                "contextTime": t,
                "performanceNow": performance.now(),
                "t0": mixer.clock.t0,
                "rate": mixer.clock.rate,
                "offset": mixer.clock.offset,
                "playing": mixer.clock.playing,
            };
        },
        "startRecording": function () {
            if (!ensureContext()) {
                return Promise.reject(new Error("no AudioContext"));
            }
            if (recording) {
                return Promise.reject(new Error("already recording"));
            }
            recording = { "left": [], "right": [], "frames": 0, "tap": null };
            var current = recording;
            return createTap(2, function (blocks) {
                current.left.push(blocks[0]);
                current.right.push(blocks[1]);
                current.frames += blocks[0].length;
            }).then(function (tap) {
                current.tap = tap;
                master.analyser.connect(tap.node);
                return true;
            });
        },
        // Resolves to an ArrayBuffer with a 16 bit stereo WAV of everything that reached the master
        "stopRecording": function () {
            if (!recording) {
                return Promise.reject(new Error("not recording"));
            }
            var current = recording;
            recording = null;
            if (current.tap) {
                try {
                    master.analyser.disconnect(current.tap.node);
                } catch (e) { /* already disconnected */ }
                current.tap.stop();
            }
            function flatten(blocks) {
                var out = new Float32Array(current.frames);
                var offset = 0;
                blocks.forEach(function (b) {
                    out.set(b.subarray(0, Math.min(b.length, out.length - offset)), offset);
                    offset += b.length;
                });
                return out;
            }
            return Promise.resolve(encodeWav([flatten(current.left), flatten(current.right)], ctx.sampleRate));
        },
        "mic": function () {
            return Object.keys(mics).map(function (k) {
                var mic = mics[k];
                return {
                    "id": mic.id,
                    "state": mic.state,
                    "error": mic.error,
                    "inputRms": mic.rms,
                    "inputPeak": mic.peak,
                    "blocks": mic.blocks,
                    "buffered": mic.ring.count,
                    "dropped": mic.ring.dropped,
                    "lastPitch": mic.lastPitch,
                    "lastDb": mic.lastDb,
                    "pitchFrames": mic.pitchFrames,
                };
            });
        },
        "micDevices": function () {
            return micDevices.slice();
        },
        // Asks the C# side to load a generated tone through the real mixer path (WebAudioManager.Update)
        "requestToneTest": function (seconds, frequency) {
            if (!ensureContext()) {
                return false;
            }
            debugToneWav = new Uint8Array(makeToneWav(ctx.sampleRate, seconds || 10, frequency || 440));
            debugRequests.push(1);
            return true;
        },
        "stopToneTest": function () {
            debugRequests.push(2);
        },
        // Asks the C# side to open the default microphone with a WebMicDevice and analyze it every frame
        "requestMicTest": function () {
            debugRequests.push(3);
        },
        "stopMicTest": function () {
            debugRequests.push(4);
        },
        "pure": pure,
    };

    // Frame time probe
    var frameStats = new FrameStats(120);
    var perf = {
        "snapshot": function () { return frameStats.snapshot(); },
        "reset": function () { frameStats = new FrameStats(120); },
    };
    function frame(t) {
        frameStats.tick(t);
        window.requestAnimationFrame(frame);
    }
    if (typeof window.requestAnimationFrame === "function") {
        window.requestAnimationFrame(frame);
    }

    // Called by C# once per frame: one-shot scheduling
    var lastResumeCheck = -Infinity;

    function tick() {
        if (!ctx) {
            return;
        }
        var wall = performance.now();
        if (ctx.state === "suspended" && wall - lastResumeCheck > 1000) {
            lastResumeCheck = wall;
            resumeIfActivated();
        }
        for (var k in mixers) {
            scheduleOneShots(mixers[k]);
        }
    }

    // Keys are quoted (and the jslib reads them with brackets) so minification of the framework cannot rename
    // what other scripts and the tests use
    var api = {
        "ensureContext": ensureContext,
        "unlock": unlock,
        "tick": tick,
        "createMixer": createMixer,
        "freeMixer": freeMixer,
        "createStem": createStem,
        "freeStem": freeStem,
        "addStemData": function (mixerId, bytes, targets) {
            var mixer = mixers[mixerId];
            if (!mixer) {
                return 0;
            }
            queueDecode(mixer, bytes, targets);
            return 1;
        },
        "play": play,
        "pause": pause,
        "setPosition": setPosition,
        "getPosition": getPosition,
        "setSpeed": setSpeed,
        "setMixerVolume": setMixerVolume,
        "fadeMixer": fadeMixer,
        "setStemVolume": setStemVolume,
        "isLoading": function (id) { return mixers[id] && mixers[id].loading > 0 ? 1 : 0; },
        "getLength": function (id) { return mixers[id] ? mixers[id].length : 0; },
        "getError": function (id) { return mixers[id] ? mixers[id].error : null; },
        "takeEnded": takeEnded,
        "getSamples": getSamples,
        "getFft": getFft,
        "getPeak": function (id) {
            var mixer = mixers[id];
            return mixer ? readAnalyser(mixer.analyser).peak : 0;
        },
        "createOneShot": createOneShot,
        "setOneShotSchedule": setOneShotSchedule,
        "setOneShotEnabled": function (id, enabled) {
            var shot = oneShots[id];
            if (shot) {
                shot.enabled = !!enabled;
                if (!enabled) {
                    cancelOneShot(shot);
                }
            }
        },
        "setOneShotVolume": function (id, volume) {
            var shot = oneShots[id];
            if (shot) {
                shot.gain.gain.setValueAtTime(volume, now());
            }
        },
        "freeOneShot": freeOneShot,
        "loadSample": loadSample,
        "playSample": playSample,
        "stopSample": stopSample,
        "pauseSample": pauseSample,
        "resumeSample": resumeSample,
        "setSampleVolume": setSampleVolume,
        "sampleState": sampleState,
        "freeSample": freeSample,
        "setMasterVolume": function (volume) {
            if (ensureContext()) {
                master.gain.gain.setValueAtTime(volume, now());
            }
        },
        "state": function () {
            if (!ctx) {
                return -1;
            }
            switch (ctx.state) {
                case "running": return 1;
                case "closed": return 2;
                case "suspended": return 0;
                default: return 3;
            }
        },
        "latency": function () {
            if (!ctx) {
                return 0;
            }
            return (ctx.outputLatency || 0) + (ctx.baseLatency || 0);
        },
        "sampleRate": function () { return ctx ? ctx.sampleRate : 0; },
        "refreshMicDevices": refreshMicDevices,
        "micDevices": function () { return micDevices; },
        "openMic": openMic,
        "closeMic": closeMic,
        "micState": function (id) { return mics[id] ? mics[id].state : -1; },
        "micError": function (id) { return mics[id] ? mics[id].error : null; },
        "micRead": function (id, out) { return mics[id] ? mics[id].ring.read(out) : -1; },
        "micAvailable": function (id) { return mics[id] ? mics[id].ring.count : -1; },
        "micReset": function (id) {
            if (mics[id]) {
                mics[id].ring.clear();
            }
        },
        "micSetMonitoring": function (id, volume) {
            if (mics[id]) {
                mics[id].monitor.gain.setValueAtTime(volume, now());
            }
        },
        "micReportPitch": function (id, pitch, db) {
            var mic = mics[id];
            if (mic) {
                mic.lastPitch = pitch;
                mic.lastDb = db;
                if (pitch > 0) {
                    mic.pitchFrames++;
                }
            }
        },
        "takeDebugRequest": function () { return debugRequests.length > 0 ? debugRequests.shift() : 0; },
        "debugToneWav": function () { return debugToneWav; },
        "debug": debug,
        "perf": perf,
    };

    // The single AudioContext, for any other code that needs one (null until the first use or user gesture)
    Object.defineProperty(api, "ctx", { "get": function () { return ctx; } });
    Object.defineProperty(api, "master", { "get": function () { return master ? master.gain : null; } });

    Module["YargAudio"] = api;
    window["YargAudio"] = api;
    window["yargAudioDebug"] = debug;
    window["yargPerf"] = perf;

    ["pointerdown", "keydown", "touchend", "click"].forEach(function (type) {
        window.addEventListener(type, unlock, true);
    });
    if (navigator.mediaDevices && navigator.mediaDevices.addEventListener) {
        navigator.mediaDevices.addEventListener("devicechange", function () { refreshMicDevices(); });
    }
})();
