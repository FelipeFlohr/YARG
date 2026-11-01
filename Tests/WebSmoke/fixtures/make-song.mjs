// Builds the song fixtures for song.spec.mjs into fixtures/song-build/ (git-ignored):
//   web-fixture/          the song folder: fixtures/song/* + song.ogg + album.png
//   web-fixture.zip       that folder inside a "Web Fixture/" root folder
//   web-fixture.sng       the same song as an SNGPKG file
//   no-audio.zip          song.ini + notes.chart only
//   not-a-song.txt        an unsupported file
// song.ogg is the game's own calibration track (Assets/StreamingAssets/calibration_music.ogg, 15 s).
// Run directly with `node fixtures/make-song.mjs`, or import buildSongFixtures().
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = path.join(here, 'song');
const repoRoot = path.resolve(here, '..', '..', '..');
const audioSource = path.join(repoRoot, 'Assets', 'StreamingAssets', 'calibration_music.ogg');
export const buildDir = path.join(here, 'song-build');

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(...buffers) {
  let crc = 0xffffffff;
  for (const buffer of buffers) {
    for (const byte of buffer) crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

// A 256x256 RGB gradient with a dark "note" ring, so a decoded cover is easy to spot in screenshots
function makePng(size = 256) {
  const raw = Buffer.alloc((size * 3 + 1) * size);
  for (let y = 0; y < size; y++) {
    const row = y * (size * 3 + 1);
    raw[row] = 0;
    for (let x = 0; x < size; x++) {
      const dx = x - size / 2;
      const dy = y - size / 2;
      const ring = Math.abs(Math.sqrt(dx * dx + dy * dy) - size * 0.3) < size * 0.05;
      const i = row + 1 + x * 3;
      raw[i] = ring ? 20 : Math.round((x / size) * 255);
      raw[i + 1] = ring ? 20 : Math.round((y / size) * 180);
      raw[i + 2] = ring ? 20 : 200;
    }
  }
  const chunk = (type, data) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const typeBytes = Buffer.from(type, 'ascii');
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typeBytes, data));
    return Buffer.concat([length, typeBytes, data, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', zlib.deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// entries: [{ name, data }]; deflated, no extra fields
function makeZip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const { name, data } of entries) {
    const nameBytes = Buffer.from(name, 'utf8');
    const compressed = zlib.deflateRawSync(data);
    const crc = crc32(data);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(8, 8); // deflate
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    locals.push(local, nameBytes, compressed);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBytes);

    offset += local.length + nameBytes.length + compressed.length;
  }
  const centralSize = centrals.reduce((sum, b) => sum + b.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, ...centrals, end]);
}

// SNGPKG (https://github.com/mdsitton/SngFileFormat). Mask byte i = key[i % 16] ^ (i & 0xFF).
function makeSng(metadata, files) {
  const key = Buffer.from([3, 1, 4, 1, 5, 9, 2, 6, 5, 3, 5, 8, 9, 7, 9, 3]);
  const parts = [Buffer.from('SNGPKG', 'ascii')];
  const u32 = (v) => { const b = Buffer.alloc(4); b.writeUInt32LE(v); return b; };
  const u64 = (v) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(BigInt(v)); return b; };
  parts.push(u32(1), key);

  const pairs = [];
  for (const [k, v] of metadata) {
    const kb = Buffer.from(k, 'utf8');
    const vb = Buffer.from(v, 'utf8');
    pairs.push(u32(kb.length), kb, u32(vb.length), vb);
  }
  const pairBytes = Buffer.concat(pairs);
  parts.push(u64(8 + pairBytes.length), u64(metadata.length), pairBytes);

  const indexLength = 8 + files.reduce((sum, f) => sum + 1 + Buffer.byteLength(f.name) + 16, 0);
  let position = parts.reduce((sum, b) => sum + b.length, 0) + 8 + indexLength + 8;
  const index = [u64(indexLength), u64(files.length)];
  for (const file of files) {
    const nb = Buffer.from(file.name, 'utf8');
    index.push(Buffer.from([nb.length]), nb, u64(file.data.length), u64(position));
    position += file.data.length;
  }
  parts.push(...index, u64(files.reduce((sum, f) => sum + f.data.length, 0)));
  for (const file of files) {
    const masked = Buffer.alloc(file.data.length);
    for (let i = 0; i < masked.length; i++) masked[i] = file.data[i] ^ key[i % 16] ^ (i & 0xff);
    parts.push(masked);
  }
  return Buffer.concat(parts);
}

function readIni(text) {
  return text.split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('[') && line.includes('='))
    .map((line) => {
      const at = line.indexOf('=');
      return [line.slice(0, at).trim().toLowerCase(), line.slice(at + 1).trim()];
    });
}

export function buildSongFixtures() {
  fs.rmSync(buildDir, { recursive: true, force: true });
  const folder = path.join(buildDir, 'web-fixture');
  fs.mkdirSync(folder, { recursive: true });

  const ini = fs.readFileSync(path.join(source, 'song.ini'));
  const chart = fs.readFileSync(path.join(source, 'notes.chart'));
  const audio = fs.readFileSync(audioSource);
  const cover = makePng();
  const files = [
    { name: 'song.ini', data: ini },
    { name: 'notes.chart', data: chart },
    { name: 'song.ogg', data: audio },
    { name: 'album.png', data: cover },
  ];
  for (const file of files) fs.writeFileSync(path.join(folder, file.name), file.data);

  fs.writeFileSync(path.join(buildDir, 'web-fixture.zip'),
    makeZip(files.map((f) => ({ name: `Web Fixture/${f.name}`, data: f.data }))));
  fs.writeFileSync(path.join(buildDir, 'web-fixture.sng'),
    makeSng(readIni(ini.toString('utf8')), files.filter((f) => f.name !== 'song.ini')));
  fs.writeFileSync(path.join(buildDir, 'no-audio.zip'), makeZip(files.slice(0, 2)));
  fs.writeFileSync(path.join(buildDir, 'not-a-song.txt'), 'This is not a song.\n');
  return { folder, files: files.map((f) => f.name) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { folder } = buildSongFixtures();
  console.log(`Song fixtures written to ${path.dirname(folder)}`);
}
