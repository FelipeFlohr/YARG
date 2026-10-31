using System.IO;
using System.Linq;
using System.Text;
using YARG.Core.Song;

namespace YARG.Multiplayer.SongShare
{
    public static class SongShareNaming
    {
        private const int MAX_NAME_LENGTH = 120;

        // Path.GetInvalidFileNameChars() depends on the OS; these are invalid on Windows too
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            .Distinct()
            .ToArray();

        /// <summary>
        /// File or folder name for a downloaded song: readable, valid on every OS, and unique per chart hash.
        /// </summary>
        public static string EntryName(string artist, string name, HashWrapper hash)
        {
            var builder = new StringBuilder($"{artist} - {name}");
            for (int i = 0; i < builder.Length; i++)
            {
                if (InvalidChars.Contains(builder[i]) || char.IsControl(builder[i]))
                {
                    builder[i] = '_';
                }
            }

            string readable = builder.ToString();
            if (readable.Length > MAX_NAME_LENGTH)
            {
                readable = readable.Substring(0, MAX_NAME_LENGTH);
            }
            // Windows silently drops trailing dots and spaces
            readable = readable.TrimEnd('.', ' ');

            return $"{readable} [{ShortHash(hash)}]";
        }

        public static string HashHex(HashWrapper hash)
        {
            return string.Concat(hash.HashBytes.Select(b => b.ToString("x2")));
        }

        private static string ShortHash(HashWrapper hash) => HashHex(hash).Substring(0, 8);
    }
}
