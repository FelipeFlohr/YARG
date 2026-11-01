// Entry points of the web audio backend, called by YARG.Audio.Web.WebAudioNative. Each one only copies its
// arguments out of the heap and forwards them to Module.YargAudio (YargWebAudio.jspre). The API is read with
// brackets because its keys are quoted there.
mergeInto(LibraryManager.library, {
    WebAudio_Init: function () {
        return Module["YargAudio"]["ensureContext"]() ? 1 : 0;
    },

    WebAudio_State: function () {
        return Module["YargAudio"]["state"]();
    },

    WebAudio_Latency: function () {
        return Module["YargAudio"]["latency"]();
    },

    WebAudio_SampleRate: function () {
        return Module["YargAudio"]["sampleRate"]();
    },

    WebAudio_SetMasterVolume: function (volume) {
        Module["YargAudio"]["setMasterVolume"](volume);
    },

    WebAudio_Tick: function () {
        Module["YargAudio"]["tick"]();
    },

    // Mixers and stems

    WebAudio_MixerCreate: function (volume, speed) {
        return Module["YargAudio"]["createMixer"](volume, speed);
    },

    WebAudio_MixerFree: function (mixer) {
        Module["YargAudio"]["freeMixer"](mixer);
    },

    WebAudio_StemCreate: function (mixer, stemId, volume) {
        return Module["YargAudio"]["createStem"](mixer, stemId, volume);
    },

    WebAudio_StemFree: function (stem) {
        Module["YargAudio"]["freeStem"](stem);
    },

    WebAudio_StemSetVolume: function (stem, volume) {
        Module["YargAudio"]["setStemVolume"](stem, volume);
    },

    // One encoded file feeding targetCount stems. For target t: stemHandles[t], gains[t] and indexCounts[t]
    // source channel indices (0 = the whole file), taken in order from indices, with two panning values each.
    WebAudio_MixerAddData: function (mixer, dataPtr, length, stemHandlesPtr, gainsPtr, indexCountsPtr, indicesPtr,
        panningPtr, targetCount) {
        var bytes = HEAPU8.slice(dataPtr, dataPtr + length);
        var targets = [];
        var cursor = 0;
        for (var t = 0; t < targetCount; t++) {
            var count = HEAP32[(indexCountsPtr >> 2) + t];
            var indices = null;
            var panning = null;
            if (count > 0) {
                indices = [];
                panning = [];
                for (var i = 0; i < count; i++) {
                    indices.push(HEAP32[(indicesPtr >> 2) + cursor + i]);
                    panning.push(HEAPF32[(panningPtr >> 2) + 2 * (cursor + i)]);
                    panning.push(HEAPF32[(panningPtr >> 2) + 2 * (cursor + i) + 1]);
                }
                cursor += count;
            }
            targets.push({
                stem: HEAP32[(stemHandlesPtr >> 2) + t],
                gain: HEAPF32[(gainsPtr >> 2) + t],
                indices: indices,
                panning: panning,
            });
        }
        return Module["YargAudio"]["addStemData"](mixer, bytes, targets);
    },

    WebAudio_MixerPlay: function (mixer) {
        return Module["YargAudio"]["play"](mixer);
    },

    WebAudio_MixerPause: function (mixer) {
        return Module["YargAudio"]["pause"](mixer);
    },

    WebAudio_MixerSetPosition: function (mixer, position) {
        Module["YargAudio"]["setPosition"](mixer, position);
    },

    WebAudio_MixerGetPosition: function (mixer) {
        return Module["YargAudio"]["getPosition"](mixer);
    },

    WebAudio_MixerSetSpeed: function (mixer, speed) {
        Module["YargAudio"]["setSpeed"](mixer, speed);
    },

    WebAudio_MixerSetVolume: function (mixer, volume) {
        Module["YargAudio"]["setMixerVolume"](mixer, volume);
    },

    WebAudio_MixerFade: function (mixer, target, seconds) {
        Module["YargAudio"]["fadeMixer"](mixer, target, seconds);
    },

    WebAudio_MixerIsLoading: function (mixer) {
        return Module["YargAudio"]["isLoading"](mixer);
    },

    WebAudio_MixerGetLength: function (mixer) {
        return Module["YargAudio"]["getLength"](mixer);
    },

    WebAudio_MixerGetError: function (mixer) {
        var text = Module["YargAudio"]["getError"](mixer);
        if (!text) {
            return 0;
        }
        var size = lengthBytesUTF8(text) + 1;
        var ptr = _malloc(size);
        stringToUTF8(text, ptr, size);
        return ptr;
    },

    WebAudio_MixerTakeEnded: function (mixer) {
        return Module["YargAudio"]["takeEnded"](mixer);
    },

    WebAudio_MixerGetSamples: function (mixer, bufferPtr, count) {
        return Module["YargAudio"]["getSamples"](mixer, HEAPF32.subarray(bufferPtr >> 2, (bufferPtr >> 2) + count));
    },

    WebAudio_MixerGetFft: function (mixer, bufferPtr, count, log2Size) {
        return Module["YargAudio"]["getFft"](mixer, HEAPF32.subarray(bufferPtr >> 2, (bufferPtr >> 2) + count),
            log2Size);
    },

    WebAudio_MixerGetPeak: function (mixer) {
        return Module["YargAudio"]["getPeak"](mixer);
    },

    // One-shots on the song timeline

    WebAudio_OneShotCreate: function (mixer, sample, timesPtr, count, lead) {
        var times = HEAPF64.slice(timesPtr >> 3, (timesPtr >> 3) + count);
        return Module["YargAudio"]["createOneShot"](mixer, sample, times, lead);
    },

    WebAudio_OneShotSetSchedule: function (shot, timesPtr, count) {
        Module["YargAudio"]["setOneShotSchedule"](shot, HEAPF64.slice(timesPtr >> 3, (timesPtr >> 3) + count));
    },

    WebAudio_OneShotSetEnabled: function (shot, enabled) {
        Module["YargAudio"]["setOneShotEnabled"](shot, enabled !== 0);
    },

    WebAudio_OneShotSetVolume: function (shot, volume) {
        Module["YargAudio"]["setOneShotVolume"](shot, volume);
    },

    WebAudio_OneShotFree: function (shot) {
        Module["YargAudio"]["freeOneShot"](shot);
    },

    // Samples

    WebAudio_SampleLoad: function (dataPtr, length) {
        return Module["YargAudio"]["loadSample"](HEAPU8.slice(dataPtr, dataPtr + length));
    },

    WebAudio_SamplePlay: function (sample, volume, loop, queued) {
        return Module["YargAudio"]["playSample"](sample, volume, loop !== 0, queued !== 0);
    },

    WebAudio_SampleStop: function (sample, fade) {
        Module["YargAudio"]["stopSample"](sample, fade);
    },

    WebAudio_SamplePause: function (sample) {
        Module["YargAudio"]["pauseSample"](sample);
    },

    WebAudio_SampleResume: function (sample) {
        Module["YargAudio"]["resumeSample"](sample);
    },

    WebAudio_SampleSetVolume: function (sample, volume) {
        Module["YargAudio"]["setSampleVolume"](sample, volume);
    },

    WebAudio_SampleState: function (sample) {
        return Module["YargAudio"]["sampleState"](sample);
    },

    WebAudio_SampleFree: function (sample) {
        Module["YargAudio"]["freeSample"](sample);
    },

    // Microphone

    WebAudio_MicRefreshDevices: function () {
        Module["YargAudio"]["refreshMicDevices"]();
    },

    WebAudio_MicDeviceCount: function () {
        return Module["YargAudio"]["micDevices"]().length;
    },

    WebAudio_MicDeviceName: function (index) {
        var device = Module["YargAudio"]["micDevices"]()[index];
        var text = device ? device.name : "";
        var size = lengthBytesUTF8(text) + 1;
        var ptr = _malloc(size);
        stringToUTF8(text, ptr, size);
        return ptr;
    },

    WebAudio_MicDeviceId: function (index) {
        var device = Module["YargAudio"]["micDevices"]()[index];
        var text = device ? device.id : "";
        var size = lengthBytesUTF8(text) + 1;
        var ptr = _malloc(size);
        stringToUTF8(text, ptr, size);
        return ptr;
    },

    WebAudio_MicOpen: function (deviceIdPtr) {
        return Module["YargAudio"]["openMic"](deviceIdPtr ? UTF8ToString(deviceIdPtr) : "");
    },

    WebAudio_MicClose: function (mic) {
        Module["YargAudio"]["closeMic"](mic);
    },

    WebAudio_MicState: function (mic) {
        return Module["YargAudio"]["micState"](mic);
    },

    WebAudio_MicError: function (mic) {
        var text = Module["YargAudio"]["micError"](mic);
        if (!text) {
            return 0;
        }
        var size = lengthBytesUTF8(text) + 1;
        var ptr = _malloc(size);
        stringToUTF8(text, ptr, size);
        return ptr;
    },

    WebAudio_MicRead: function (mic, bufferPtr, count) {
        return Module["YargAudio"]["micRead"](mic, HEAPF32.subarray(bufferPtr >> 2, (bufferPtr >> 2) + count));
    },

    WebAudio_MicAvailable: function (mic) {
        return Module["YargAudio"]["micAvailable"](mic);
    },

    WebAudio_MicReset: function (mic) {
        Module["YargAudio"]["micReset"](mic);
    },

    WebAudio_MicSetMonitoring: function (mic, volume) {
        Module["YargAudio"]["micSetMonitoring"](mic, volume);
    },

    WebAudio_MicReportPitch: function (mic, pitch, db) {
        Module["YargAudio"]["micReportPitch"](mic, pitch, db);
    },

    // Debug requests from window.yargAudioDebug, served by WebAudioManager.Update

    WebAudio_DebugTakeRequest: function () {
        return Module["YargAudio"]["takeDebugRequest"]();
    },

    WebAudio_DebugToneWavLength: function () {
        var wav = Module["YargAudio"]["debugToneWav"]();
        return wav ? wav.length : 0;
    },

    WebAudio_DebugCopyToneWav: function (bufferPtr, length) {
        var wav = Module["YargAudio"]["debugToneWav"]();
        if (!wav) {
            return 0;
        }
        var n = Math.min(length, wav.length);
        HEAPU8.set(wav.subarray(0, n), bufferPtr);
        return n;
    },
});
