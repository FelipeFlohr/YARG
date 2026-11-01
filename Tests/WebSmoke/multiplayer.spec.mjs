// Online match between two headless pages through a local relay (Server/YARG.Relay), driven by the in-game
// autotest (MultiplayerAutoTest webhost/webjoin). Checks a wrong password is refused, that the client receives the
// song the host loaded, and takes screenshots of both pages in gameplay and on the results screen.
// With YARG_WEB_BASE_URL (a deployed build) no local relay is started: the pages use /ws on their own origin.
import { test, expect } from '@playwright/test';
import { spawn, spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { remoteBaseUrl, routeFixtures } from './fixtures/remote.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const relayProject = path.resolve(here, '..', '..', 'Server', 'YARG.Relay', 'YARG.Relay.csproj');
const relayPort = Number(process.env.YARG_RELAY_PORT || 8090);
// null: the game's default, /ws on the page's origin (ws: or wss: like the page)
const relayUrl = remoteBaseUrl ? null : `ws://127.0.0.1:${relayPort}/ws`;

const ROOM = 'SmokeRoom';
const PASSWORD = 'certa123';
const WRONG_PASSWORD = 'errada';

const MATCH_TIMEOUT = 480_000;

let relay;

async function waitForHealth(timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${relayPort}/health`);
      if (response.ok) return;
    } catch {
      // Not up yet
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`relay did not answer on port ${relayPort}`);
}

test.beforeAll(async () => {
  if (remoteBaseUrl) return;
  test.setTimeout(240_000);
  relay = spawn('dotnet', ['run', '--project', relayProject, '--no-launch-profile', '--',
    '--urls', `http://127.0.0.1:${relayPort}`], { stdio: ['ignore', 'pipe', 'pipe'] });
  relay.stdout.on('data', (d) => process.stdout.write(`[relay] ${d}`));
  relay.stderr.on('data', (d) => process.stderr.write(`[relay] ${d}`));
  await waitForHealth(200_000);
});

test.afterAll(async () => {
  if (!relay) return;
  // dotnet run starts the relay as a child process, which a plain kill leaves running on Windows
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(relay.pid), '/T', '/F']);
  } else {
    relay.kill();
  }
});

// The song fixture from the song-loading smoke test (subproject 3), served by serve.mjs under /__fixtures/
// (or by routeFixtures against a deployed build).
async function fixtureSongUrl() {
  if (process.env.YARG_WEB_SONG_URL) return process.env.YARG_WEB_SONG_URL;
  const fixtures = await import('./fixtures/make-song.mjs');
  if (typeof fixtures.buildSongFixtures === 'function') await fixtures.buildSongFixtures();
  return '/__fixtures/song-build/web-fixture.sng';
}

// Collects a page's console and waits for lines; any "[AutoTest] FAIL" or exception fails every wait.
class Console {
  constructor(page, name) {
    this.name = name;
    this.lines = [];
    this.waiters = [];
    this.failure = null;
    page.on('console', (msg) => this.add(msg.text(), msg.type()));
    page.on('pageerror', (err) => this.add(String(err.message || err), 'error'));
  }

  add(text, type) {
    this.lines.push(text);
    if (text.includes('[AutoTest] FAIL') || (type === 'error' && text.includes('Exception'))) {
      this.failure ??= new Error(`${this.name}: ${text}`);
    }
    for (const waiter of [...this.waiters]) waiter.check();
  }

  waitFor(needle, timeout = 120_000) {
    return new Promise((resolve, reject) => {
      const waiter = {
        check: () => {
          if (this.failure) {
            done();
            reject(this.failure);
            return;
          }
          const line = this.lines.find((l) => l.includes(needle));
          if (line) {
            done();
            resolve(line);
          }
        },
      };
      const timer = setTimeout(() => {
        done();
        reject(new Error(`${this.name}: timed out waiting for "${needle}"\n` + this.lines.slice(-30).join('\n')));
      }, timeout);
      const done = () => {
        clearTimeout(timer);
        this.waiters = this.waiters.filter((w) => w !== waiter);
      };
      this.waiters.push(waiter);
      waiter.check();
    });
  }
}

function query(params) {
  return '/?' + Object.entries(params).map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join('&');
}

async function openPage(browser, baseURL, name, params) {
  const context = await browser.newContext({ baseURL, viewport: { width: 1280, height: 720 } });
  await routeFixtures(context);
  const page = await context.newPage();
  const log = new Console(page, name);
  await page.goto(query(relayUrl ? { ...params, relay: relayUrl } : params));
  await page.locator('#yarg-start').click();
  return { context, page, console: log };
}

async function screenshot(page, testInfo, name) {
  const file = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path: file });
  await testInfo.attach(name, { path: file, contentType: 'image/png' });
}

test('two pages play a password room match through the relay', async ({ browser }, testInfo) => {
  test.setTimeout(MATCH_TIMEOUT + 120_000);
  const baseURL = testInfo.project.use.baseURL;
  const songUrl = await fixtureSongUrl();

  const host = await openPage(browser, baseURL, 'host', {
    'mp-autotest': 'webhost', 'mp-room': ROOM, 'mp-password': PASSWORD, 'mp-players': 2,
    'mp-profile': 'Host', 'mp-bot': 1,
  });
  const client = await openPage(browser, baseURL, 'client', {
    'mp-autotest': 'webjoin', 'mp-room': ROOM, 'mp-password': PASSWORD, 'mp-wrong-password': WRONG_PASSWORD,
    'mp-profile': 'Cliente', 'mp-bot': 1,
  });

  try {
    await host.console.waitFor(`[AutoTest] Created room ${ROOM}`, 180_000);

    // The wrong password is refused before the right one gets in
    await client.console.waitFor('[AutoTest] Wrong password refused', 180_000);
    await client.console.waitFor(`[AutoTest] Joined room ${ROOM}`);
    const refusedAt = client.console.lines.findIndex((l) => l.includes('Wrong password refused'));
    const joinedAt = client.console.lines.findIndex((l) => l.includes(`Joined room ${ROOM}`));
    expect(refusedAt).toBeLessThan(joinedAt);

    // The host loads the song through the song loader's test hook once both players are in
    await host.console.waitFor('[AutoTest] Waiting for a song loaded into the page');
    await host.page.waitForFunction(() => window.yargTest && typeof window.yargTest.loadSongFromUrl === 'function');
    await host.page.evaluate((url) => window.yargTest.loadSongFromUrl(url), songUrl);
    await host.console.waitFor('[AutoTest] Loaded song:');

    // The client downloads it through the relay and installs it into its own MEMFS
    await client.console.waitFor('Installed multiplayer song to /tmp/yarg-song/mp-');
    await client.console.waitFor('[AutoTest] Done: the song');

    await Promise.all([
      host.console.waitFor('[AutoTest] Done: the song to start', 180_000),
      client.console.waitFor('[AutoTest] Done: the song to start', 180_000),
    ]);
    // A few seconds in, so notes are on the highway
    await host.page.waitForTimeout(5_000);
    await screenshot(host.page, testInfo, 'host-gameplay');
    await screenshot(client.page, testInfo, 'client-gameplay');

    await Promise.all([
      host.console.waitFor('[AutoTest] Done: the score screen', MATCH_TIMEOUT),
      client.console.waitFor('[AutoTest] Done: the score screen', MATCH_TIMEOUT),
    ]);
    await host.page.waitForTimeout(1_000);
    await screenshot(host.page, testInfo, 'host-results');
    await screenshot(client.page, testInfo, 'client-results');

    const band = await host.console.waitFor('[AutoTest] Band: ');
    expect(band).toMatch(/players=2/);

    await Promise.all([
      host.console.waitFor('[AutoTest] PASS'),
      client.console.waitFor('[AutoTest] PASS'),
    ]);
  } finally {
    await testInfo.attach('host-console', { body: host.console.lines.join('\n'), contentType: 'text/plain' });
    await testInfo.attach('client-console', { body: client.console.lines.join('\n'), contentType: 'text/plain' });
    await host.context.close();
    await client.context.close();
  }
});
