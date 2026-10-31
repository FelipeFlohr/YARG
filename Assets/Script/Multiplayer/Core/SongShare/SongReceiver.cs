using System;
using System.IO;
using YARG.Multiplayer.Protocol;

namespace YARG.Multiplayer.SongShare
{
    /// <summary>
    /// Writes the chunks of an offered song to a staging file and installs it into the library
    /// once complete. Anything not installed is deleted on <see cref="Abort"/> or <see cref="Dispose"/>.
    /// </summary>
    public sealed class SongReceiver : IDisposable
    {
        private readonly SongOffer   _offer;
        private readonly string      _partialPath;
        private          FileStream? _file;
        private          bool        _installed;

        public long BytesWritten { get; private set; }
        public bool IsComplete   => BytesWritten == _offer.TotalBytes;

        public SongReceiver(SongOffer offer, string stagingDir)
        {
            if (offer.TotalBytes < 0 || offer.TotalBytes > ProtocolInfo.MaxSongBytes)
            {
                throw new InvalidDataException(
                    $"Song size {offer.TotalBytes} is outside the limit of {ProtocolInfo.MaxSongBytes} bytes");
            }

            _offer = offer;
            Directory.CreateDirectory(stagingDir);
            _partialPath = Path.Combine(stagingDir, SongShareNaming.HashHex(offer.Hash) + ".partial");
            _file = new FileStream(_partialPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }

        public void Write(SongChunk chunk)
        {
            if (!chunk.Hash.Equals(_offer.Hash))
            {
                return;
            }

            if (_file == null)
            {
                throw new InvalidOperationException("The receiver was already closed");
            }
            if (chunk.Offset != BytesWritten)
            {
                throw new InvalidDataException($"Expected a chunk at offset {BytesWritten}, got {chunk.Offset}");
            }
            if (BytesWritten + chunk.Data.Length > _offer.TotalBytes)
            {
                throw new InvalidDataException("Received more bytes than the song's announced size");
            }

            _file.Write(chunk.Data, 0, chunk.Data.Length);
            BytesWritten += chunk.Data.Length;
        }

        /// <summary>
        /// Moves the completed song into <paramref name="destinationRoot"/> and returns its path:
        /// a <c>.sng</c> file, or a folder for an ini song.
        /// </summary>
        public string Install(string destinationRoot)
        {
            if (!IsComplete || _file == null)
            {
                throw new InvalidOperationException("The song hasn't been fully received");
            }

            Directory.CreateDirectory(destinationRoot);
            string target = Path.Combine(destinationRoot,
                SongShareNaming.EntryName(_offer.Artist, _offer.Name, _offer.Hash));

            try
            {
                if (_offer.Kind == SongPackageKind.Sng)
                {
                    _file.Dispose();
                    _file = null;

                    target += ".sng";
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    File.Move(_partialPath, target);
                }
                else
                {
                    _file.Position = 0;
                    SafeExtractor.Extract(_file, target, ProtocolInfo.MaxSongBytes);
                }
            }
            finally
            {
                Abort();
            }

            _installed = true;
            return target;
        }

        public void Abort()
        {
            _file?.Dispose();
            _file = null;
            if (File.Exists(_partialPath))
            {
                File.Delete(_partialPath);
            }
        }

        public void Dispose()
        {
            if (!_installed)
            {
                Abort();
            }
        }
    }
}
