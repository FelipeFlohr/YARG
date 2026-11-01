// Static server for the WebGL build. Brotli files are sent with Content-Encoding: br
// and the Content-Type of the file without the .br extension.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const port = Number(process.env.YARG_WEB_PORT || 8089);
const root = path.resolve(process.env.YARG_WEB_BUILD || path.join(os.homedir(), 'yarg-build-web'));
const FIXTURES_PREFIX = '/__fixtures/';
const fixturesRoot = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript',
  '.wasm': 'application/wasm',
  '.data': 'application/octet-stream',
  '.json': 'application/json',
  '.css': 'text/css',
  '.jpg': 'image/jpeg',
  '.png': 'image/png',
  '.ogg': 'audio/ogg',
  '.opus': 'audio/ogg',
  '.txt': 'text/plain; charset=utf-8',
  '.zip': 'application/zip',
};

const server = http.createServer((req, res) => {
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
  } catch {
    res.writeHead(400).end();
    return;
  }
  if (pathname.endsWith('/')) pathname += 'index.html';

  // Test fixtures (songs for song.spec.mjs) are served next to the build
  let base = root;
  if (pathname.startsWith(FIXTURES_PREFIX)) {
    base = fixturesRoot;
    pathname = pathname.slice(FIXTURES_PREFIX.length - 1);
  }

  const file = path.join(base, pathname);
  if (file !== base && !file.startsWith(base + path.sep)) {
    res.writeHead(403).end();
    return;
  }

  fs.stat(file, (err, stat) => {
    if (err || !stat.isFile()) {
      res.writeHead(404).end('Not found');
      return;
    }
    const headers = { 'Cache-Control': 'no-store', 'Content-Length': stat.size };
    let name = file;
    if (name.endsWith('.br')) {
      headers['Content-Encoding'] = 'br';
      name = name.slice(0, -3);
    }
    headers['Content-Type'] = types[path.extname(name).toLowerCase()] || 'application/octet-stream';
    res.writeHead(200, headers);
    if (req.method === 'HEAD') {
      res.end();
      return;
    }
    fs.createReadStream(file).pipe(res);
  });
});

server.listen(port, '127.0.0.1', () => console.log(`Serving ${root} on http://127.0.0.1:${port}`));
