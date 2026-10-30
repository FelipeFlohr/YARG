using System;
using System.IO;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.SongShare
{
    /// <summary>
    /// The bytes the host sends for a song: a <c>.sng</c> file as-is, or a temporary zip of an ini folder.
    /// </summary>
    public sealed class SongPackage : IDisposable
    {
        private readonly string _path;
        private readonly bool   _deleteOnDispose;

        public SongPackageKind Kind   { get; }
        public long            Length { get; }

        internal SongPackage(SongPackageKind kind, string path, bool deleteOnDispose)
        {
            Kind = kind;
            _path = path;
            _deleteOnDispose = deleteOnDispose;
            Length = new FileInfo(path).Length;
        }

        /// <summary>
        /// Opens a new read stream; one per transfer, so several clients can download at once.
        /// </summary>
        public Stream Open() => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        public void Dispose()
        {
            if (_deleteOnDispose && File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
