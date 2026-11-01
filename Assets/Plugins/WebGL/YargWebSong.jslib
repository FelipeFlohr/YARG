// Song loading for the web build (see Assets/Script/Song/Web/WebSongLoader.cs).
// Files picked or dropped by the player are written to the Emscripten in-memory filesystem under
// /tmp/yarg-song/<id>/ and C# is told the path through SendMessage. Messages are "ok|<path>",
// "cancel|" or "error|<code>" (codes: too-large, unsupported, read-failed).
var YargWebSongLib = {
    $YargWebSong: {
        objectName: "",
        dropEnabled: false,
        initialized: false,
        root: "/tmp/yarg-song",
        maxBytes: 300 * 1024 * 1024,
        counter: 0,
        singleFileExtensions: [".sng", ".yargsong", ".zip"],

        send: function (method, text) {
            try {
                if (typeof Module !== "undefined" && typeof Module.SendMessage === "function") {
                    Module.SendMessage(YargWebSong.objectName, method, text);
                } else if (typeof SendMessage === "function") {
                    SendMessage(YargWebSong.objectName, method, text);
                } else {
                    console.error("YargWebSong: no SendMessage to deliver " + method);
                }
            } catch (e) {
                console.error("YargWebSong: " + method + " callback failed", e);
            }
        },

        newId: function () {
            YargWebSong.counter++;
            return Date.now().toString(36) + "-" + YargWebSong.counter;
        },

        // Keeps only safe path segments, so nothing escapes the song folder
        cleanPath: function (path) {
            var parts = String(path || "").replace(/\\/g, "/").split("/");
            var kept = [];
            for (var i = 0; i < parts.length; i++) {
                var part = parts[i];
                if (part === "" || part === "." || part === "..") {
                    continue;
                }
                kept.push(part);
            }
            return kept.join("/");
        },

        extensionOf: function (name) {
            var dot = name.lastIndexOf(".");
            return dot < 0 ? "" : name.substring(dot).toLowerCase();
        },

        mkdirs: function (path) {
            var parts = path.split("/");
            var current = "";
            for (var i = 0; i < parts.length; i++) {
                if (parts[i] === "") {
                    continue;
                }
                current += "/" + parts[i];
                try {
                    FS.mkdir(current);
                } catch (e) {
                    // Already exists
                }
            }
        },

        removeTree: function (path) {
            try {
                var stat = FS.stat(path);
                if (FS.isDir(stat.mode)) {
                    var names = FS.readdir(path);
                    for (var i = 0; i < names.length; i++) {
                        if (names[i] !== "." && names[i] !== "..") {
                            YargWebSong.removeTree(path + "/" + names[i]);
                        }
                    }
                    FS.rmdir(path);
                } else {
                    FS.unlink(path);
                }
            } catch (e) {
                // Already gone
            }
        },

        listTree: function (path, out) {
            try {
                var names = FS.readdir(path);
                for (var i = 0; i < names.length; i++) {
                    if (names[i] === "." || names[i] === "..") {
                        continue;
                    }
                    var child = path + "/" + names[i];
                    if (FS.isDir(FS.stat(child).mode)) {
                        YargWebSong.listTree(child, out);
                    } else {
                        out.push(child);
                    }
                }
            } catch (e) {
                // Missing folder: nothing to list
            }
            return out;
        },

        readBlob: function (blob) {
            if (blob.arrayBuffer) {
                return blob.arrayBuffer();
            }
            return new Promise(function (resolve, reject) {
                var reader = new FileReader();
                reader.onload = function () { resolve(reader.result); };
                reader.onerror = function () { reject(reader.error); };
                reader.readAsArrayBuffer(blob);
            });
        },

        // entries: [{ path: "relative/name.ext", blob: Blob }]. kind: "file" (one .sng/.zip) or "folder".
        deliver: function (method, entries, kind) {
            if (!entries || entries.length === 0) {
                YargWebSong.send(method, "cancel|");
                return Promise.resolve();
            }

            var total = 0;
            for (var i = 0; i < entries.length; i++) {
                total += entries[i].blob.size || 0;
            }
            if (total > YargWebSong.maxBytes) {
                YargWebSong.send(method, "error|too-large");
                return Promise.resolve();
            }

            if (kind === "file") {
                var ext = YargWebSong.extensionOf(entries[0].path);
                if (YargWebSong.singleFileExtensions.indexOf(ext) < 0) {
                    YargWebSong.send(method, "error|unsupported");
                    return Promise.resolve();
                }
            }

            var dir = YargWebSong.root + "/" + YargWebSong.newId();
            var index = 0;
            var writeNext = function () {
                if (index >= entries.length) {
                    return Promise.resolve();
                }
                var entry = entries[index++];
                var relative = YargWebSong.cleanPath(entry.path);
                if (relative === "") {
                    return writeNext();
                }
                return YargWebSong.readBlob(entry.blob).then(function (buffer) {
                    var full = dir + "/" + relative;
                    YargWebSong.mkdirs(full.substring(0, full.lastIndexOf("/")));
                    FS.writeFile(full, new Uint8Array(buffer));
                    return writeNext();
                });
            };

            YargWebSong.mkdirs(dir);
            return writeNext().then(function () {
                var path = kind === "file" ? dir + "/" + YargWebSong.cleanPath(entries[0].path) : dir;
                YargWebSong.send(method, "ok|" + path);
            }, function (e) {
                console.error("YargWebSong: failed to copy the song", e);
                YargWebSong.removeTree(dir);
                YargWebSong.send(method, "error|read-failed");
            });
        },

        entriesFromFileList: function (files, useRelativePaths) {
            var entries = [];
            for (var i = 0; i < files.length; i++) {
                var file = files[i];
                var path = useRelativePaths && file.webkitRelativePath ? file.webkitRelativePath : file.name;
                entries.push({ path: path, blob: file });
            }
            return entries;
        },

        // Reads a dropped directory through the FileSystem Entry API
        readDirectoryEntry: function (directoryEntry, prefix, out) {
            return new Promise(function (resolve, reject) {
                var reader = directoryEntry.createReader();
                var pending = [];
                var readBatch = function () {
                    reader.readEntries(function (batch) {
                        if (batch.length === 0) {
                            Promise.all(pending).then(resolve, reject);
                            return;
                        }
                        for (var i = 0; i < batch.length; i++) {
                            pending.push(YargWebSong.readEntry(batch[i], prefix, out));
                        }
                        readBatch();
                    }, reject);
                };
                readBatch();
            });
        },

        readEntry: function (entry, prefix, out) {
            if (entry.isDirectory) {
                return YargWebSong.readDirectoryEntry(entry, prefix + entry.name + "/", out);
            }
            return new Promise(function (resolve, reject) {
                entry.file(function (file) {
                    out.push({ path: prefix + entry.name, blob: file });
                    resolve();
                }, reject);
            });
        },

        handleDrop: function (event) {
            var dataTransfer = event.dataTransfer;
            if (!dataTransfer) {
                return;
            }

            var fileSystemEntries = [];
            if (dataTransfer.items) {
                for (var i = 0; i < dataTransfer.items.length; i++) {
                    var item = dataTransfer.items[i];
                    var entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
                    if (entry) {
                        fileSystemEntries.push(entry);
                    }
                }
            }

            var files = dataTransfer.files;
            if (fileSystemEntries.length === 0) {
                // Synthetic drops (tests) and old browsers only have the file list
                var entries = YargWebSong.entriesFromFileList(files || [], false);
                YargWebSong.deliver("OnDropResult", entries, entries.length === 1 ? "file" : "folder");
                return;
            }

            var isSingleFile = fileSystemEntries.length === 1 && fileSystemEntries[0].isFile;
            var out = [];
            Promise.all(fileSystemEntries.map(function (entry) { return YargWebSong.readEntry(entry, "", out); }))
                .then(function () {
                    YargWebSong.deliver("OnDropResult", out, isSingleFile ? "file" : "folder");
                }, function (e) {
                    console.error("YargWebSong: failed to read the dropped files", e);
                    YargWebSong.send("OnDropResult", "error|read-failed");
                });
        },

        // The browser's default for a dropped file is to open it, which would unload the game,
        // so drops are always swallowed and only delivered while C# accepts them
        installDropTarget: function () {
            document.addEventListener("dragover", function (event) {
                event.preventDefault();
                if (event.dataTransfer) {
                    event.dataTransfer.dropEffect = YargWebSong.dropEnabled ? "copy" : "none";
                }
            });
            document.addEventListener("drop", function (event) {
                event.preventDefault();
                if (YargWebSong.dropEnabled) {
                    YargWebSong.handleDrop(event);
                }
            });
        },

        installTestHooks: function () {
            var hooks = window.yargTest || {};
            window.yargTest = hooks;

            var nameFromUrl = function (url) {
                var path = String(url).split("?")[0].split("#")[0];
                return decodeURIComponent(path.substring(path.lastIndexOf("/") + 1));
            };
            var fetchBlob = function (url) {
                return fetch(url).then(function (response) {
                    if (!response.ok) {
                        throw new Error("HTTP " + response.status + " for " + url);
                    }
                    return response.blob();
                });
            };

            // Same C# path as dropping a file on the page
            hooks.loadSongFromUrl = function (url, name) {
                return fetchBlob(url).then(function (blob) {
                    return YargWebSong.deliver("OnDropResult", [{ path: name || nameFromUrl(url), blob: blob }], "file");
                });
            };
            // Same C# path as dropping a folder: baseUrl + each relative file name
            hooks.loadFolderFromUrls = function (baseUrl, names) {
                return Promise.all(names.map(function (name) {
                    return fetchBlob(baseUrl + name).then(function (blob) { return { path: name, blob: blob }; });
                })).then(function (entries) {
                    return YargWebSong.deliver("OnDropResult", entries, "folder");
                });
            };
            hooks.listSongFiles = function () {
                return YargWebSong.listTree(YargWebSong.root, []);
            };
            hooks.isSongDropEnabled = function () {
                return YargWebSong.dropEnabled;
            };
            // Commands handled by WebSongTestHooks (only with ?web-test)
            hooks.songCommand = function (command, argument) {
                YargWebSong.send("OnTestCommand", command + "|" + (argument || ""));
            };
        }
    },

    YargWebSong_Init: function (objectNamePtr) {
        YargWebSong.objectName = UTF8ToString(objectNamePtr);
        if (YargWebSong.initialized) {
            return;
        }
        YargWebSong.initialized = true;
        try {
            YargWebSong.mkdirs(YargWebSong.root);
            YargWebSong.installDropTarget();
            YargWebSong.installTestHooks();
        } catch (e) {
            console.error("YargWebSong_Init failed", e);
        }
    },

    YargWebSong_Pick: function (folder) {
        try {
            var input = document.createElement("input");
            input.type = "file";
            input.style.display = "none";
            if (folder) {
                input.webkitdirectory = true;
                input.multiple = true;
            } else {
                input.accept = YargWebSong.singleFileExtensions.join(",");
            }

            var done = false;
            var finish = function (entries) {
                if (done) {
                    return;
                }
                done = true;
                if (input.parentNode) {
                    input.parentNode.removeChild(input);
                }
                YargWebSong.deliver("OnPickResult", entries, folder ? "folder" : "file");
            };
            input.addEventListener("change", function () {
                finish(YargWebSong.entriesFromFileList(input.files || [], !!folder));
            });
            input.addEventListener("cancel", function () { finish([]); });
            document.body.appendChild(input);
            input.click();
        } catch (e) {
            console.error("YargWebSong_Pick failed", e);
            YargWebSong.send("OnPickResult", "error|read-failed");
        }
    },

    YargWebSong_SetDropEnabled: function (enabled) {
        YargWebSong.dropEnabled = !!enabled;
    }
};

YargWebSongLib.$YargWebSong__deps = ["$FS"];
autoAddDeps(YargWebSongLib, "$YargWebSong");
mergeInto(LibraryManager.library, YargWebSongLib);
