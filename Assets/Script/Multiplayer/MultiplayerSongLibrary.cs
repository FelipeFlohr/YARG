using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.SongShare;
using YARG.Settings;
using YARG.Song;
using YARG.Song.Web;

namespace YARG.Multiplayer
{
    /// <summary>
    /// Where songs downloaded from a multiplayer host live, and how they get into the song library.
    /// </summary>
    public static class MultiplayerSongLibrary
    {
        /// <summary>
        /// Everything here is deleted when leaving a session and at startup. On the web it is in the in-memory
        /// file system, since the persistent data path there is IndexedDB.
        /// </summary>
        public static string TempRoot => PlatformInfo.IsWeb
            ? WEB_TEMP_ROOT
            : Path.Combine(PathHelper.PersistentDataPath, "Multiplayer Temp");

        private const string WEB_TEMP_ROOT = "/tmp/yarg-mp";

        // The web song loader's own folder; each download gets a subfolder
        private const string WEB_SONGS_ROOT = "/tmp/yarg-song";

        public static string StagingRoot  => Path.Combine(TempRoot, "Staging");
        public static string PackagesRoot => Path.Combine(TempRoot, "Packages");

        private static string TempSongsRoot   => Path.Combine(TempRoot, "Songs");
        private static string DefaultSaveRoot => Path.Combine(PathHelper.PersistentDataPath, "Multiplayer Songs");

        public static string DownloadRoot
        {
            get
            {
                var settings = SettingsManager.Settings;
                if (!settings.SaveMultiplayerSongs.Value)
                {
                    return TempSongsRoot;
                }

                return string.IsNullOrEmpty(settings.MultiplayerSongsFolder)
                    ? DefaultSaveRoot
                    : settings.MultiplayerSongsFolder;
            }
        }

        public static IEnumerable<string> ExtraScanDirectories
        {
            get
            {
                // Downloaded songs stay playable even after the save setting is turned off
                foreach (string path in new[] { DownloadRoot, DefaultSaveRoot, TempSongsRoot }.Distinct())
                {
                    if (Directory.Exists(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        public static void CleanupTemp()
        {
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                YargLogger.LogException(e, "Failed to clean up the multiplayer temp folder");
            }
        }

#nullable enable
        public static SongEntry? FindByHash(HashWrapper hash)
        {
            // The web has no library, only the one song loaded (picked by the host, downloaded by a client)
            if (PlatformInfo.IsWeb)
            {
                var current = WebSongLoader.Current;
                return current != null && current.Hash.Equals(hash) ? current : null;
            }

            // The cache keeps listing songs whose files were deleted (temp downloads are) until a
            // full rescan, and claiming one of those would make loading fail mid-match
            if (SongContainer.SongsByHash.TryGetValue(hash, out var songs))
            {
                var found = songs.FirstOrDefault(IsOnDisk);
                if (found != null)
                {
                    return found;
                }
            }

            // Also songs hidden by filters such as the max rating: the player still has them
            return SongContainer.UnfilteredSongs.FirstOrDefault(song => song.Hash.Equals(hash) && IsOnDisk(song));
        }

        private static bool IsOnDisk(SongEntry song)
        {
            string location = song.ActualLocation;
            return File.Exists(location) || Directory.Exists(location);
        }

        /// <summary>
        /// Packs a song for sending, or returns null for song types that can't be shared (CON).
        /// </summary>
        public static SongPackage? CreatePackage(SongEntry song)
        {
            return song.SubType switch
            {
                EntryType.Ini => SongPackager.FromIniFolder(song.ActualLocation, PackagesRoot),
                EntryType.Sng => SongPackager.FromSng(song.ActualLocation),
                _             => null,
            };
        }

        /// <summary>
        /// Installs a fully received song, rescans the library and returns the entry,
        /// or null when the song that came out doesn't have the offered hash.
        /// </summary>
        public static async UniTask<SongEntry?> InstallAndRegister(SongReceiver receiver, SongOffer offer)
        {
            if (PlatformInfo.IsWeb)
            {
                return await InstallForWeb(receiver, offer);
            }

            string root = DownloadRoot;
            string path = await PlatformTask.Run(() => receiver.Install(root));
            YargLogger.LogFormatInfo("Installed multiplayer song to {0}", path);

            await SongContainer.RunRefresh(false);
            return FindByHash(offer.Hash);
        }

        /// <summary>
        /// Installs into the in-memory file system and loads it as the one current song, replacing the last one
        /// (only one song fits in the page's memory comfortably).
        /// </summary>
        private static async UniTask<SongEntry?> InstallForWeb(SongReceiver receiver, SongOffer offer)
        {
            WebSongLoader.Clear();

            string root = Path.Combine(WEB_SONGS_ROOT, "mp-" + SongShareNaming.HashHex(offer.Hash)).Replace('\\', '/');
            string path = await PlatformTask.Run(() => receiver.Install(root));
            YargLogger.LogFormatInfo("Installed multiplayer song to {0}", path);

            var entry = await PlatformTask.Run(() => WebSongLoader.LoadFromPath(path));
            return entry != null && entry.Hash.Equals(offer.Hash) ? entry : null;
        }
#nullable disable
    }
}
