using System;
using System.IO;
using System.IO.Compression;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.SongShare
{
    public static class SongPackager
    {
        /// <summary>
        /// Zips an ini song folder into <paramref name="tempDir"/>. Files the receiver would reject
        /// (see <see cref="SafeExtractor.AllowedExtensions"/>) are left out instead of failing the transfer.
        /// </summary>
        public static SongPackage FromIniFolder(string folder, string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.zip");
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!SafeExtractor.IsAllowedFile(file))
                    {
                        continue;
                    }

                    string entryName = file.Substring(root.Length + 1)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
                }
            }
            catch
            {
                File.Delete(zipPath);
                throw;
            }

            return new SongPackage(SongPackageKind.IniFolderZip, zipPath, true);
        }

        public static SongPackage FromSng(string sngPath)
        {
            return new SongPackage(SongPackageKind.Sng, sngPath, false);
        }
    }
}
