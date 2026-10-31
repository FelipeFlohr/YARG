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

namespace YARG.Multiplayer
{
    /// <summary>
    /// Where songs downloaded from a multiplayer host live, and how they get into the song library.
    /// </summary>
    public static class MultiplayerSongLibrary
    {
        /// <summary>
        /// Everything here is deleted when leaving a session and at startup.
        /// </summary>
        public static string TempRoot => Path.Combine(PathHelper.PersistentDataPath, "Multiplayer Temp");

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
            string root = DownloadRoot;
            string path = await UniTask.RunOnThreadPool(() => receiver.Install(root));
            YargLogger.LogFormatInfo("Installed multiplayer song to {0}", path);

            await SongContainer.RunRefresh(false);
            return FindByHash(offer.Hash);
        }
#nullable disable
    }
}
