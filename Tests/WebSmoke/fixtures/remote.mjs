// Running the specs against a deployed build (YARG_WEB_BASE_URL, e.g. the docker compose stack in deploy/)
// instead of serve.mjs. A real deployment does not serve the test fixtures, so the browser context answers
// /__fixtures/* itself from this folder, on the page's own origin like serve.mjs does.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const remoteBaseUrl = process.env.YARG_WEB_BASE_URL ? process.env.YARG_WEB_BASE_URL.replace(/\/+$/, '') : null;

const fixturesRoot = path.dirname(fileURLToPath(import.meta.url));
const types = { '.zip': 'application/zip', '.txt': 'text/plain; charset=utf-8', '.png': 'image/png',
  '.ogg': 'audio/ogg', '.ini': 'text/plain; charset=utf-8' };

// Serves /__fixtures/* from Tests/WebSmoke/fixtures in this context; does nothing when serve.mjs is in use
export async function routeFixtures(context) {
  if (!remoteBaseUrl) return;
  await context.route((url) => url.pathname.startsWith('/__fixtures/'), async (route) => {
    const pathname = decodeURIComponent(new URL(route.request().url()).pathname).slice('/__fixtures/'.length);
    const file = path.join(fixturesRoot, pathname);
    if (!file.startsWith(fixturesRoot + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      await route.fulfill({ status: 404, body: 'Not found' });
      return;
    }
    await route.fulfill({
      status: 200,
      body: fs.readFileSync(file),
      contentType: types[path.extname(file).toLowerCase()] || 'application/octet-stream',
    });
  });
}
