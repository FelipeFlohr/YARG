// Copies the build's StreamingAssets into the in-memory file system at /streaming before the game starts,
// so the C# side can read them with plain file IO (PathHelper.StreamingAssetsPath is "/streaming" on the web).
// The list of files comes from StreamingAssets/web-manifest.json, written by Editor.WebBuild.
(function () {
    var ROOT = "/streaming";
    var DEPENDENCY = "yarg-streaming";
    var PARALLEL_DOWNLOADS = 8;

    function fail(error) {
        var message = "[YargWeb] StreamingAssets preload failed: " + (error && error.message ? error.message : error);
        console.error(message);
        if (typeof window !== "undefined" && typeof window.yargShowError === "function") {
            window.yargShowError(message);
        }
    }

    function encodePath(path) {
        return path.split("/").map(encodeURIComponent).join("/");
    }

    function fetchOk(url) {
        return fetch(url).then(function (response) {
            if (!response.ok) {
                throw new Error(url + ": HTTP " + response.status);
            }
            return response;
        });
    }

    function writeFile(path, buffer) {
        var target = ROOT + "/" + path;
        var slash = target.lastIndexOf("/");
        FS.mkdirTree(target.substring(0, slash));
        FS.writeFile(target, new Uint8Array(buffer));
    }

    function preload() {
        addRunDependency(DEPENDENCY);

        var baseUrl = Module["streamingAssetsUrl"] || "StreamingAssets";
        baseUrl = baseUrl.replace(/\/+$/, "");

        fetchOk(baseUrl + "/web-manifest.json")
            .then(function (response) { return response.json(); })
            .then(function (files) {
                if (!Array.isArray(files)) {
                    throw new Error("web-manifest.json is not an array");
                }

                FS.mkdirTree(ROOT);

                var next = 0;
                function worker() {
                    if (next >= files.length) {
                        return Promise.resolve();
                    }
                    var path = String(files[next++]);
                    return fetchOk(baseUrl + "/" + encodePath(path))
                        .then(function (response) { return response.arrayBuffer(); })
                        .then(function (buffer) {
                            writeFile(path, buffer);
                            return worker();
                        });
                }

                var workers = [];
                for (var i = 0; i < PARALLEL_DOWNLOADS; i++) {
                    workers.push(worker());
                }
                return Promise.all(workers);
            })
            .then(function () {
                removeRunDependency(DEPENDENCY);
            })
            .catch(fail);
    }

    if (!Module["preRun"]) {
        Module["preRun"] = [];
    } else if (typeof Module["preRun"] === "function") {
        Module["preRun"] = [Module["preRun"]];
    }
    Module["preRun"].push(preload);
})();
