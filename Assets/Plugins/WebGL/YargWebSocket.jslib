// Browser WebSockets for the web multiplayer (see BrowserWebSocket.cs). The C# side polls: received
// frames wait in a JS queue until it reads them, so nothing calls into C# from a socket event.
var YargWebSocketLibrary = {
    $YargSockets: {
        next: 1,
        sockets: {},
        encoder: null,

        get: function (id) {
            return YargSockets.sockets[id] || null;
        },

        toPtr: function (s) {
            var size = lengthBytesUTF8(s) + 1;
            var ptr = _malloc(size);
            stringToUTF8(s, ptr, size);
            return ptr;
        }
    },

    // Returns a socket id (> 0). Opening errors show up as a closed socket with a reason.
    YargWebSocket_Open: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        var id = YargSockets.next++;
        var entry = { ws: null, queue: [], state: 0, reason: "" };
        YargSockets.sockets[id] = entry;
        if (!YargSockets.encoder) {
            YargSockets.encoder = new TextEncoder();
        }

        try {
            var ws = new WebSocket(url);
            ws.binaryType = "arraybuffer";
            entry.ws = ws;
            ws.onopen = function () { entry.state = 1; };
            ws.onmessage = function (e) {
                if (typeof e.data === "string") {
                    entry.queue.push({ text: true, bytes: YargSockets.encoder.encode(e.data) });
                } else {
                    entry.queue.push({ text: false, bytes: new Uint8Array(e.data) });
                }
            };
            ws.onerror = function () { entry.errored = true; };
            ws.onclose = function (e) {
                entry.state = 3;
                entry.reason = e.reason || ((entry.errored ? "connection error" : "connection closed") +
                    " (code " + e.code + ")");
            };
        } catch (e) {
            entry.state = 3;
            entry.reason = "could not connect: " + e;
        }
        return id;
    },

    // 0 connecting, 1 open, 3 closed (closing counts as open until the close completes)
    YargWebSocket_State: function (id) {
        var entry = YargSockets.get(id);
        return entry ? entry.state : 3;
    },

    YargWebSocket_CloseReason: function (id) {
        var entry = YargSockets.get(id);
        return YargSockets.toPtr(entry ? entry.reason : "");
    },

    YargWebSocket_BufferedAmount: function (id) {
        var entry = YargSockets.get(id);
        return entry && entry.ws ? entry.ws.bufferedAmount : 0;
    },

    YargWebSocket_SendText: function (id, textPtr) {
        var entry = YargSockets.get(id);
        if (entry && entry.ws && entry.ws.readyState === 1) {
            entry.ws.send(UTF8ToString(textPtr));
        }
    },

    YargWebSocket_SendBinary: function (id, dataPtr, length) {
        var entry = YargSockets.get(id);
        if (entry && entry.ws && entry.ws.readyState === 1) {
            // A copy: the wasm heap may move or be reused before the browser sends it
            entry.ws.send(HEAPU8.slice(dataPtr, dataPtr + length));
        }
    },

    // Size in bytes of the oldest received frame, or -1 when there is none
    YargWebSocket_PeekSize: function (id) {
        var entry = YargSockets.get(id);
        return entry && entry.queue.length > 0 ? entry.queue[0].bytes.length : -1;
    },

    YargWebSocket_PeekIsText: function (id) {
        var entry = YargSockets.get(id);
        return entry && entry.queue.length > 0 && entry.queue[0].text ? 1 : 0;
    },

    // Copies the oldest frame (UTF-8 for text) into the buffer and drops it from the queue
    YargWebSocket_Receive: function (id, bufferPtr, length) {
        var entry = YargSockets.get(id);
        if (!entry || entry.queue.length === 0) {
            return -1;
        }
        var frame = entry.queue[0];
        if (frame.bytes.length > length) {
            return -1;
        }
        entry.queue.shift();
        HEAPU8.set(frame.bytes, bufferPtr);
        return frame.bytes.length;
    },

    YargWebSocket_Close: function (id) {
        var entry = YargSockets.get(id);
        if (!entry) {
            return;
        }
        if (entry.ws && (entry.ws.readyState === 0 || entry.ws.readyState === 1)) {
            entry.ws.close(1000);
        } else if (!entry.ws) {
            entry.state = 3;
        }
    },

    YargWebSocket_Free: function (id) {
        var entry = YargSockets.get(id);
        if (entry && entry.ws) {
            entry.ws.onopen = entry.ws.onmessage = entry.ws.onerror = entry.ws.onclose = null;
            try {
                entry.ws.close(1000);
            } catch (e) {
            }
        }
        delete YargSockets.sockets[id];
    },

    // The relay on the page's own origin: wss://host/ws (ws:// when the page is plain http)
    YargWebSocket_DefaultRelayUrl: function () {
        var url = "";
        try {
            var protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
            url = protocol + "//" + window.location.host + "/ws";
        } catch (e) {
            url = "";
        }
        var size = lengthBytesUTF8(url) + 1;
        var ptr = _malloc(size);
        stringToUTF8(url, ptr, size);
        return ptr;
    }
};

autoAddDeps(YargWebSocketLibrary, '$YargSockets');
mergeInto(LibraryManager.library, YargWebSocketLibrary);
