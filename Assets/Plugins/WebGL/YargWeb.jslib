mergeInto(LibraryManager.library, {
    YargWeb_GetQueryString: function () {
        var s = "";
        try {
            s = window.location.search || "";
        } catch (e) {
            s = "";
        }
        var size = lengthBytesUTF8(s) + 1;
        var ptr = _malloc(size);
        stringToUTF8(s, ptr, size);
        return ptr;
    },

    YargWeb_StorageGet: function (keyPtr) {
        try {
            var value = window.localStorage.getItem(UTF8ToString(keyPtr));
            if (value === null || value === undefined) {
                return 0;
            }
            var size = lengthBytesUTF8(value) + 1;
            var ptr = _malloc(size);
            stringToUTF8(value, ptr, size);
            return ptr;
        } catch (e) {
            return 0;
        }
    },

    YargWeb_StorageSet: function (keyPtr, valuePtr) {
        try {
            window.localStorage.setItem(UTF8ToString(keyPtr), UTF8ToString(valuePtr));
            return 1;
        } catch (e) {
            return 0;
        }
    },

    YargWeb_DownloadText: function (fileNamePtr, textPtr) {
        try {
            var fileName = UTF8ToString(fileNamePtr);
            var text = UTF8ToString(textPtr);
            var blob = new Blob([text], { type: "text/plain;charset=utf-8" });
            var url = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = url;
            a.download = fileName;
            a.style.display = "none";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        } catch (e) {
            console.error("YargWeb_DownloadText failed", e);
        }
    },

    YargWeb_PickTextFile: function (acceptPtr, objectNamePtr, methodPtr) {
        var accept = UTF8ToString(acceptPtr);
        var objectName = UTF8ToString(objectNamePtr);
        var method = UTF8ToString(methodPtr);

        // Unity 6 exposes SendMessage on the module instance (Module.SendMessage).
        var send = function (text) {
            try {
                if (typeof Module !== "undefined" && typeof Module.SendMessage === "function") {
                    Module.SendMessage(objectName, method, text);
                } else if (typeof SendMessage === "function") {
                    SendMessage(objectName, method, text);
                }
            } catch (e) {
                console.error("YargWeb_PickTextFile callback failed", e);
            }
        };

        try {
            var input = document.createElement("input");
            input.type = "file";
            input.accept = accept;
            input.style.display = "none";
            var done = false;
            var finish = function (text) {
                if (done) {
                    return;
                }
                done = true;
                if (input.parentNode) {
                    input.parentNode.removeChild(input);
                }
                send(text);
            };
            input.addEventListener("change", function () {
                var file = input.files && input.files[0];
                if (!file) {
                    finish("");
                    return;
                }
                var reader = new FileReader();
                reader.onload = function () { finish(String(reader.result)); };
                reader.onerror = function () { finish(""); };
                reader.readAsText(file);
            });
            input.addEventListener("cancel", function () { finish(""); });
            document.body.appendChild(input);
            input.click();
        } catch (e) {
            console.error("YargWeb_PickTextFile failed", e);
            send("");
        }
    },

    YargWeb_OnPageHide: function (objectNamePtr, methodPtr) {
        var objectName = UTF8ToString(objectNamePtr);
        var method = UTF8ToString(methodPtr);
        var send = function () {
            try {
                if (typeof Module !== "undefined" && typeof Module.SendMessage === "function") {
                    Module.SendMessage(objectName, method);
                } else if (typeof SendMessage === "function") {
                    SendMessage(objectName, method);
                }
            } catch (e) {
                console.error("YargWeb_OnPageHide callback failed", e);
            }
        };
        // Unity does not run OnDisable/OnApplicationQuit when the tab is closed or reloaded
        window.addEventListener("pagehide", send);
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "hidden") {
                send();
            }
        });
    },

    YargWeb_Reload: function () {
        try {
            window.location.reload();
        } catch (e) {
            console.error("YargWeb_Reload failed", e);
        }
    }
});
