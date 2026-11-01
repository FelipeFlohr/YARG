using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Localization;
using YARG.Multiplayer.SongShare;

namespace YARG.Song.Web
{
    public enum WebSongError
    {
        UnsupportedFormat,
        NoChart,
        NoAudio,
        ParseFailed,
        TooLarge,
        ReadFailed,
    }

    /// <summary>
    /// A song the player picked could not be loaded. <see cref="Exception.Message"/> is meant for the player.
    /// </summary>
    public sealed class WebSongLoadException : Exception
    {
        public WebSongError Error { get; }

        public WebSongLoadException(WebSongError error, string reason = null)
            : base(GetMessage(error, reason))
        {
            Error = error;
        }

        private static string GetMessage(WebSongError error, string reason)
        {
            string key = "WebSong.Error." + error;
            return error == WebSongError.ParseFailed
                ? Localize.KeyFormat(key, string.IsNullOrEmpty(reason) ? "?" : reason)
                : Localize.Key(key);
        }
    }

    /// <summary>
    /// Loads one song from the player's disk on the web build, where there is no song library: the browser
    /// copies the picked or dropped files into the in-memory filesystem (see YargWebSong.jslib) and the
    /// song is built from there, without a scan or cache. Only one song is kept at a time.
    /// </summary>
    public static class WebSongLoader
    {
        public const string SONG_ROOT = "/tmp/yarg-song";
        public const long MAX_SONG_BYTES = 300L * 1024 * 1024;

        private const string OBJECT_NAME = "YargWebSong";

        private static Receiver _receiver;
        private static UniTaskCompletionSource<string> _pendingPick;

        /// <summary>The song loaded last, or null.</summary>
        public static SongEntry Current { get; private set; }

        /// <summary>Folder or file holding <see cref="Current"/>, deleted by <see cref="Clear"/>.</summary>
        private static string _currentRoot;

        /// <summary>Raised on the main thread when a dropped song was loaded (it is already <see cref="Current"/>).</summary>
        public static event Action<SongEntry> SongDropped;

        /// <summary>Raised on the main thread when a dropped song could not be loaded.</summary>
        public static event Action<WebSongLoadException> SongDropFailed;

        /// <summary>
        /// Whether a dropped song may replace <see cref="Current"/> right now (checked every frame in the menu
        /// scene; drops are never taken elsewhere). Null means always.
        /// </summary>
        public static Func<bool> AcceptsDrops { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (!PlatformInfo.IsWeb || _receiver != null)
            {
                return;
            }

            // No threads on the web
            SngFileStream.UseParallelDecrypt = false;
            WebImageDecoder.Install();

            var go = new GameObject(OBJECT_NAME);
            UnityEngine.Object.DontDestroyOnLoad(go);
            _receiver = go.AddComponent<Receiver>();
            WebSongBridge.Init(OBJECT_NAME);
#if YARG_WEB_TEST_HOOKS
            WebSongTestHooks.Initialize();
#endif
        }

        /// <summary>
        /// Opens the browser's file picker for a .sng or .zip and loads the song. Returns null when the player
        /// cancels. Call it straight from a click or key press: browsers only open pickers after a user gesture.
        /// </summary>
        /// <exception cref="WebSongLoadException">The song could not be loaded.</exception>
        public static UniTask<SongEntry> PickAsync(CancellationToken token) => PickAsync(false, token);

        /// <summary>Like <see cref="PickAsync(CancellationToken)"/>, but picks a song folder.</summary>
        public static UniTask<SongEntry> PickFolderAsync(CancellationToken token) => PickAsync(true, token);

        private static async UniTask<SongEntry> PickAsync(bool folder, CancellationToken token)
        {
            if (!PlatformInfo.IsWeb)
            {
                YargLogger.LogWarning("The song picker only exists on the web build.");
                return null;
            }

            // Only one picker at a time; a forgotten one counts as cancelled
            _pendingPick?.TrySetResult("cancel|");
            var pick = new UniTaskCompletionSource<string>();
            _pendingPick = pick;

            WebSongBridge.Pick(folder);

            string message;
            using (token.Register(() => pick.TrySetResult("cancel|")))
            {
                message = await pick.Task;
            }

            if (_pendingPick == pick)
            {
                _pendingPick = null;
            }

            string path = ParseMessage(message);
            if (path == null)
            {
                return null;
            }

            if (token.IsCancellationRequested)
            {
                DeleteOwned(path);
                return null;
            }

            // Lets the menu show that it is loading before the main thread is busy
            await UniTask.Yield();
            return LoadFromPath(path);
        }

        /// <summary>
        /// Loads a .sng file, a .zip or a song folder that is already in the in-memory filesystem, for example a
        /// song received over the network. On success the previous song's files are deleted and the new one
        /// becomes <see cref="Current"/>.
        /// </summary>
        /// <exception cref="WebSongLoadException">The song could not be loaded.</exception>
        public static SongEntry LoadFromPath(string path)
        {
            if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                throw new WebSongLoadException(WebSongError.ReadFailed);
            }

            path = Path.GetFullPath(path);
            string root = GetRoot(path);
            try
            {
                if (MeasureBytes(path) > MAX_SONG_BYTES)
                {
                    throw new WebSongLoadException(WebSongError.TooLarge);
                }

                string songPath = path;
                if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    songPath = ExtractZip(path);
                }

                var result = SingleSongLoader.Load(songPath);
                if (!result.Success)
                {
                    YargLogger.LogFormatWarning("Could not load the song at {0}: {1} {2}", songPath, result.Error, result.Reason);
                    throw new WebSongLoadException(ToWebError(result.Error), result.Reason);
                }

                if (_currentRoot != null && _currentRoot != root)
                {
                    DeleteQuietly(_currentRoot);
                }

                Current = result.Entry;
                _currentRoot = root;
                YargLogger.LogFormatInfo("[WebSong] Loaded \"{0}\" by \"{1}\" from {2}", Current.Name.Original, Current.Artist.Original, songPath);
                return Current;
            }
            catch (Exception e)
            {
                var error = e as WebSongLoadException;
                if (error == null)
                {
                    YargLogger.LogException(e, $"Unexpected error while loading the song at {path}");
                    error = new WebSongLoadException(WebSongError.ReadFailed);
                }

                YargLogger.LogFormatWarning("[WebSong] Error: {0}", error.Message);
                if (root != _currentRoot)
                {
                    DeleteOwned(root);
                }

                if (error == e)
                {
                    throw;
                }
                throw error;
            }
        }

        /// <summary>Forgets <see cref="Current"/> and deletes its files.</summary>
        public static void Clear()
        {
            if (_currentRoot != null)
            {
                DeleteQuietly(_currentRoot);
            }

            Current = null;
            _currentRoot = null;
        }

        private static string ExtractZip(string zipPath)
        {
            string destination = Path.Combine(Path.GetDirectoryName(zipPath),
                Path.GetFileNameWithoutExtension(zipPath) + "_song");
            try
            {
                using (var stream = File.OpenRead(zipPath))
                {
                    SafeExtractor.Extract(stream, destination, MAX_SONG_BYTES);
                }
            }
            catch (UnsafeArchiveException e) when (e.Message.Contains("exceeds the limit"))
            {
                throw new WebSongLoadException(WebSongError.TooLarge);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, $"Could not extract {zipPath}");
                throw new WebSongLoadException(WebSongError.UnsupportedFormat);
            }

            // The extracted copy is all that is needed now, and memory is tight
            if (IsOwned(zipPath))
            {
                DeleteQuietly(zipPath);
            }
            return destination;
        }

        /// <summary>Turns a jslib message into a path, null for a cancel, or throws for an error.</summary>
        private static string ParseMessage(string message)
        {
            message ??= "cancel|";
            int split = message.IndexOf('|');
            string status = split < 0 ? message : message.Substring(0, split);
            string value = split < 0 ? string.Empty : message.Substring(split + 1);

            switch (status)
            {
                case "ok":
                    return value;
                case "cancel":
                    return null;
            }

            var error = value switch
            {
                "too-large"   => WebSongError.TooLarge,
                "unsupported" => WebSongError.UnsupportedFormat,
                _             => WebSongError.ReadFailed,
            };
            var exception = new WebSongLoadException(error);
            YargLogger.LogFormatWarning("[WebSong] Error: {0}", exception.Message);
            throw exception;
        }

        private static WebSongError ToWebError(SingleSongError error) => error switch
        {
            SingleSongError.NoChart     => WebSongError.NoChart,
            SingleSongError.NoAudio     => WebSongError.NoAudio,
            SingleSongError.ParseFailed => WebSongError.ParseFailed,
            _                           => WebSongError.UnsupportedFormat,
        };

        /// <summary>
        /// Songs copied by the browser live in their own /tmp/yarg-song/&lt;id&gt; folder, which is deleted as a
        /// whole. Anything else (a path handed in by the caller) is deleted as given.
        /// </summary>
        private static string GetRoot(string path)
        {
            string prefix = SONG_ROOT + "/";
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return path;
            }

            int end = normalized.IndexOf('/', prefix.Length);
            return end < 0 ? normalized : normalized.Substring(0, end);
        }

        private static bool IsOwned(string path) => path.Replace('\\', '/').StartsWith(SONG_ROOT + "/", StringComparison.Ordinal);

        private static void DeleteOwned(string path)
        {
            if (IsOwned(path))
            {
                DeleteQuietly(GetRoot(path));
            }
        }

        private static long MeasureBytes(string path)
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += file.Length;
            }
            return total;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, $"Failed to delete {path}");
            }
        }

        /// <summary>
        /// Named GameObject that the browser calls back through SendMessage.
        /// </summary>
        private sealed class Receiver : MonoBehaviour
        {
            private bool? _dropEnabled;

            private void Update()
            {
                // Drops are only taken in the menus; during gameplay they are ignored
                bool enabled = GlobalVariables.Instance != null &&
                    GlobalVariables.Instance.CurrentScene == SceneIndex.Menu &&
                    (AcceptsDrops == null || AcceptsDrops());
                if (_dropEnabled != enabled)
                {
                    _dropEnabled = enabled;
                    WebSongBridge.SetDropEnabled(enabled);
                }
            }

            private void OnPickResult(string message)
            {
                if (_pendingPick != null)
                {
                    _pendingPick.TrySetResult(message);
                    return;
                }

                // Nobody is waiting anymore (cancelled); don't keep the files around
                try
                {
                    string path = ParseMessage(message);
                    if (path != null)
                    {
                        DeleteOwned(path);
                    }
                }
                catch (WebSongLoadException)
                {
                    // Already logged
                }
            }

            private void OnDropResult(string message)
            {
                SongEntry song;
                try
                {
                    string path = ParseMessage(message);
                    if (path == null)
                    {
                        return;
                    }

                    if (_dropEnabled != true)
                    {
                        DeleteOwned(path);
                        return;
                    }

                    song = LoadFromPath(path);
                }
                catch (WebSongLoadException e)
                {
                    SongDropFailed?.Invoke(e);
                    return;
                }

                SongDropped?.Invoke(song);
            }

            // window.yargTest.loadSongFromUrl / loadFolderFromUrls: a drop in test builds, refused otherwise
            private void OnTestDropResult(string message)
            {
#if YARG_WEB_TEST_HOOKS
                OnDropResult(message);
#else
                YargLogger.LogWarning("[WebSong] Test hooks are not in this build; ignoring a test song.");
                try
                {
                    string path = ParseMessage(message);
                    if (path != null)
                    {
                        DeleteOwned(path);
                    }
                }
                catch (WebSongLoadException)
                {
                    // Already logged
                }
#endif
            }

            private void OnTestCommand(string message)
            {
#if YARG_WEB_TEST_HOOKS
                WebSongTestHooks.Handle(message);
#else
                YargLogger.LogWarning("[WebSong] Test hooks are not in this build; ignoring a test command.");
#endif
            }
        }
    }
}
