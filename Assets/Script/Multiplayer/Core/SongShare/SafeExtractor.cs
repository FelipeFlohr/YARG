using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace YARG.Multiplayer.SongShare
{
    public sealed class UnsafeArchiveException : IOException
    {
        public UnsafeArchiveException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Extracts a song zip received from another player without trusting it: every entry is checked
    /// before anything is written, and the result only appears at the destination once complete.
    /// </summary>
    public static class SafeExtractor
    {
        public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".mid", ".midi", ".chart", ".ogg", ".opus", ".mp3", ".wav", ".flac",
            ".png", ".jpg", ".jpeg", ".webm", ".mp4", ".yarground", ".txt",
        };

        public static bool IsAllowedFile(string path) => AllowedExtensions.Contains(Path.GetExtension(path));

        public static void Extract(Stream zip, string destinationDir, long maxTotalBytes)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, true);

            string partialDir = Path.GetFullPath(destinationDir + ".partial");
            var files = Validate(archive, partialDir, maxTotalBytes);

            if (Directory.Exists(partialDir))
            {
                Directory.Delete(partialDir, true);
            }

            try
            {
                Directory.CreateDirectory(partialDir);
                long written = 0;
                foreach (var (entry, path) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = entry.Open();
                    using var output = File.Create(path);
                    written = CopyLimited(input, output, written, maxTotalBytes);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, true);
                }
                Directory.Move(partialDir, destinationDir);
            }
            catch
            {
                if (Directory.Exists(partialDir))
                {
                    Directory.Delete(partialDir, true);
                }
                throw;
            }
        }

        private static List<(ZipArchiveEntry Entry, string Path)> Validate(ZipArchive archive, string root,
            long maxTotalBytes)
        {
            var files = new List<(ZipArchiveEntry, string)>();
            long total = 0;
            string rootWithSeparator = root + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith("/") || name.Contains(":") || Path.IsPathRooted(name))
                {
                    throw new UnsafeArchiveException($"Absolute path in song archive: {entry.FullName}");
                }

                var segments = name.Split('/');
                if (Array.Exists(segments, s => s == ".."))
                {
                    throw new UnsafeArchiveException($"Parent directory reference in song archive: {entry.FullName}");
                }

                // Directory entry
                if (name.EndsWith("/"))
                {
                    continue;
                }

                if (!IsAllowedFile(name))
                {
                    throw new UnsafeArchiveException($"File type not allowed in song archive: {entry.FullName}");
                }

                total += entry.Length;
                if (total > maxTotalBytes)
                {
                    throw new UnsafeArchiveException($"Song archive exceeds the limit of {maxTotalBytes} bytes");
                }

                string path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
                if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                {
                    throw new UnsafeArchiveException($"Entry escapes the destination: {entry.FullName}");
                }
                files.Add((entry, path));
            }

            return files;
        }

        // The declared sizes were already checked, but don't trust them while decompressing either
        private static long CopyLimited(Stream input, Stream output, long written, long maxTotalBytes)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > maxTotalBytes)
                {
                    throw new UnsafeArchiveException($"Song archive exceeds the limit of {maxTotalBytes} bytes");
                }
                output.Write(buffer, 0, read);
            }
            return written;
        }
    }
}
