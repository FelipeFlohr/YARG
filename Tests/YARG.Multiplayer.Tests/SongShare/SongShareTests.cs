using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Song;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.SongShare;

namespace YARG.Multiplayer.Tests.SongShare
{
    public class SongShareTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "yarg-mp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        private string Dir(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static byte[] RandomBytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static HashWrapper Hash(byte seed) => HashWrapper.Hash(new[] { seed });

        private static void Transfer(SongPackage package, SongReceiver receiver, SongOffer offer)
        {
            using var stream = package.Open();
            var buffer = new byte[ProtocolInfo.MaxChunkSize];
            long offset = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                receiver.Write(new SongChunk(offer.Hash, offset, buffer.Take(read).ToArray()));
                offset += read;
            }
        }

        private static byte[] Zip(params (string Name, byte[] Data)[] entries)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var (name, data) in entries)
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(data);
                }
            }
            return memory.ToArray();
        }

        [Test]
        public void IniFolder_PackThenReceiveThenInstall_ReproducesFiles()
        {
            var song = Dir("source/My Song");
            File.WriteAllBytes(Path.Combine(song, "song.ini"), RandomBytes(300, 1));
            File.WriteAllBytes(Path.Combine(song, "notes.mid"), RandomBytes(5000, 2));
            File.WriteAllBytes(Path.Combine(song, "song.ogg"), RandomBytes(300_000, 3));
            Directory.CreateDirectory(Path.Combine(song, "extra"));
            File.WriteAllBytes(Path.Combine(song, "extra", "album.png"), RandomBytes(100, 4));

            using var package = SongPackager.FromIniFolder(song, Dir("temp"));
            Assert.That(package.Kind, Is.EqualTo(SongPackageKind.IniFolderZip));

            var offer = new SongOffer(Hash(1), "My Song", "Band", package.Kind, package.Length, 1f);
            using var receiver = new SongReceiver(offer, Dir("staging"));
            Transfer(package, receiver, offer);
            Assert.That(receiver.IsComplete, Is.True);

            string installed = receiver.Install(Dir("library"));

            Assert.That(installed, Is.EqualTo(Path.Combine(_root, "library", SongShareNaming.EntryName("Band", "My Song", Hash(1)))));
            foreach (var file in new[] { "song.ini", "notes.mid", "song.ogg", Path.Combine("extra", "album.png") })
            {
                Assert.That(File.ReadAllBytes(Path.Combine(installed, file)),
                    Is.EqualTo(File.ReadAllBytes(Path.Combine(song, file))), file);
            }
            Assert.That(Directory.GetFiles(Path.Combine(_root, "staging")), Is.Empty);
        }

        [Test]
        public void IniFolder_PackageSkipsFilesTheReceiverWouldReject()
        {
            var song = Dir("source/Song");
            File.WriteAllBytes(Path.Combine(song, "notes.chart"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(song, "notes.chart.bak"), new byte[] { 2 });

            using var package = SongPackager.FromIniFolder(song, Dir("temp"));
            using var stream = package.Open();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            Assert.That(archive.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "notes.chart" }));
        }

        [Test]
        public void IniFolder_PackageDisposeDeletesTemporaryZip()
        {
            var song = Dir("source/Song");
            File.WriteAllBytes(Path.Combine(song, "notes.chart"), new byte[] { 1 });
            var temp = Dir("temp");

            var package = SongPackager.FromIniFolder(song, temp);
            Assert.That(Directory.GetFiles(temp), Has.Length.EqualTo(1));
            package.Dispose();
            Assert.That(Directory.GetFiles(temp), Is.Empty);
        }

        [Test]
        public void Sng_InstallsAsSingleFile()
        {
            var sngPath = Path.Combine(Dir("source"), "song.sng");
            var data = RandomBytes(200_000, 5);
            File.WriteAllBytes(sngPath, data);

            using var package = SongPackager.FromSng(sngPath);
            Assert.That(package.Kind, Is.EqualTo(SongPackageKind.Sng));
            Assert.That(package.Length, Is.EqualTo(data.Length));

            var offer = new SongOffer(Hash(2), "Name", "Artist", package.Kind, package.Length, 1f);
            using var receiver = new SongReceiver(offer, Dir("staging"));
            Transfer(package, receiver, offer);
            string installed = receiver.Install(Dir("library"));

            Assert.That(Path.GetFileName(installed), Is.EqualTo(SongShareNaming.EntryName("Artist", "Name", Hash(2)) + ".sng"));
            Assert.That(File.ReadAllBytes(installed), Is.EqualTo(data));
            Assert.That(File.Exists(sngPath), Is.True, "the host's file is untouched");
        }

        [Test]
        public void Receiver_RejectsOutOfOrderChunk()
        {
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, 10, 1f);
            using var receiver = new SongReceiver(offer, Dir("staging"));
            receiver.Write(new SongChunk(offer.Hash, 0, new byte[4]));

            Assert.Throws<InvalidDataException>(() => receiver.Write(new SongChunk(offer.Hash, 5, new byte[4])));
        }

        [Test]
        public void Receiver_RejectsMoreBytesThanOffered()
        {
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, 4, 1f);
            using var receiver = new SongReceiver(offer, Dir("staging"));
            Assert.Throws<InvalidDataException>(() => receiver.Write(new SongChunk(offer.Hash, 0, new byte[5])));
        }

        [Test]
        public void Receiver_IgnoresChunksOfAnotherSong()
        {
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, 4, 1f);
            using var receiver = new SongReceiver(offer, Dir("staging"));
            receiver.Write(new SongChunk(Hash(4), 0, new byte[4]));
            Assert.That(receiver.BytesWritten, Is.EqualTo(0));
        }

        [Test]
        public void Receiver_RejectsOfferAboveSizeLimit()
        {
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, ProtocolInfo.MaxSongBytes + 1, 1f);
            Assert.Throws<InvalidDataException>(() => new SongReceiver(offer, Dir("staging")));
        }

        [Test]
        public void Receiver_AbortDeletesPartialFile()
        {
            // Review Focus 4
            var staging = Dir("staging");
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, 10, 1f);
            var receiver = new SongReceiver(offer, staging);
            receiver.Write(new SongChunk(offer.Hash, 0, new byte[4]));
            Assert.That(Directory.GetFiles(staging), Is.Not.Empty);

            receiver.Abort();

            Assert.That(Directory.GetFiles(staging), Is.Empty);
            receiver.Dispose();
        }

        [Test]
        public void Receiver_DisposeWithoutInstallDeletesPartialFile()
        {
            var staging = Dir("staging");
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.Sng, 10, 1f);
            using (var receiver = new SongReceiver(offer, staging))
            {
                receiver.Write(new SongChunk(offer.Hash, 0, new byte[4]));
            }
            Assert.That(Directory.GetFiles(staging), Is.Empty);
        }

        [Test]
        public void Receiver_CorruptZipInstallFailsWithoutLeavingAnything()
        {
            var garbage = RandomBytes(100, 9);
            var offer = new SongOffer(Hash(3), "N", "A", SongPackageKind.IniFolderZip, garbage.Length, 1f);
            var library = Dir("library");
            using var receiver = new SongReceiver(offer, Dir("staging"));
            receiver.Write(new SongChunk(offer.Hash, 0, garbage));

            Assert.Throws<InvalidDataException>(() => receiver.Install(library));
            Assert.That(Directory.GetFileSystemEntries(library), Is.Empty);
        }

        [Test]
        public void Extractor_RejectsParentTraversal()
        {
            AssertRejected(Zip(("notes.chart", new byte[1]), ("../evil.ini", new byte[1])));
        }

        [TestCase("/tmp/x.ini")]
        [TestCase("C:/x.ini")]
        [TestCase("\\x.ini")]
        public void Extractor_RejectsAbsolutePath(string name)
        {
            AssertRejected(Zip((name, new byte[1])));
        }

        [Test]
        public void Extractor_RejectsDisallowedExtension()
        {
            AssertRejected(Zip(("notes.chart", new byte[1]), ("run.exe", new byte[1])));
        }

        [Test]
        public void Extractor_RejectsOversizedArchive()
        {
            AssertRejected(Zip(("song.ogg", new byte[11])), maxTotalBytes: 10);
        }

        [Test]
        public void Extractor_ReplacesPreviousInstall()
        {
            var destination = Path.Combine(_root, "library", "Song");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old.txt"), "old");

            using var zip = new MemoryStream(Zip(("notes.chart", new byte[] { 1 })));
            SafeExtractor.Extract(zip, destination, 100);

            Assert.That(Directory.GetFiles(destination).Select(Path.GetFileName), Is.EqualTo(new[] { "notes.chart" }));
            Assert.That(Directory.Exists(destination + ".partial"), Is.False);
        }

        [Test]
        public void Naming_SanitizesAndDisambiguatesByHash()
        {
            // Review Focus 3
            var first = SongShareNaming.EntryName("AC/DC", "Live?", Hash(1));
            var second = SongShareNaming.EntryName("AC/DC", "Live?", Hash(2));

            Assert.That(first, Does.Not.Contain("/").And.Not.Contain("?").And.Not.Contain(":"));
            Assert.That(first, Does.StartWith("AC_DC - Live_ ["));
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(Path.GetFileName(Path.Combine(_root, first)), Is.EqualTo(first));
        }

        [Test]
        public void Naming_TruncatesLongNames()
        {
            var name = SongShareNaming.EntryName(new string('a', 200), new string('b', 200), Hash(1));
            Assert.That(name.Length, Is.LessThanOrEqualTo(120 + " [12345678]".Length));
            Assert.That(name, Does.EndWith("]"));
        }

        private void AssertRejected(byte[] zip, long maxTotalBytes = 1_000_000)
        {
            var destination = Path.Combine(_root, "library", "Song");
            using var stream = new MemoryStream(zip);

            Assert.Throws<UnsafeArchiveException>(() => SafeExtractor.Extract(stream, destination, maxTotalBytes));
            Assert.That(Directory.Exists(destination), Is.False);
            Assert.That(Directory.Exists(destination + ".partial"), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "library", "evil.ini")), Is.False);
        }
    }
}
