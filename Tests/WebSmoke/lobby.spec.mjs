// Online play through the menus, on the GPU: a host creates a password room on the relay, a guest gets the
// password wrong and then right, the host uploads a real song, both choose a part (guitar and bass) and the band
// plays it. Screenshots of every screen; the two pages must play the song at the same moment, and the host keeps
// playing when the guest leaves mid-song. Needs a test build, a relay at
// YARG_WEB_RELAY (e.g. ws://127.0.0.1:8091/ws; with YARG_WEB_BASE_URL the page's own /ws) and a song in
// YARG_WEB_SONGS. Runs in the "gpu" project.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { dismissAntiPiracyDialog } from './fixtures/dialogs.mjs';
import { remoteBaseUrl } from './fixtures/remote.mjs';

const relay = process.env.YARG_WEB_RELAY || (remoteBaseUrl ? '' : null);
const songsDir = process.env.YARG_WEB_SONGS;
const song = songsDir && fs.existsSync(songsDir)
  ? (process.env.YARG_WEB_LOBBY_SONG || fs.readdirSync(songsDir).filter((f) => /\.(sng|zip)$/i.test(f)).sort()[0])
  : null;
const PASSWORD = 'friday-rock';

// Where things are on a 1280x720 page (OnlineMenu's layout, scaled from 1920x1080)
const AT = {
  online: [150, 333], // main menu
  firstRoom: [450, 233],
  roomPassword: [1053, 326],
  create: [1053, 523],
  joinPassword: [1053, 283],
  chooseSong: [250, 523],
  part: [320, 612],
  start: [1149, 612],
  firstPart: [634, 249], // the part picker dialog's first item
};

test.setTimeout(600_000);

function collectLogs(page) {
  const lines = [];
  const errors = [];
  page.on('console', (msg) => {
    lines.push(msg.text());
    if (msg.type() === 'error' && msg.text().includes('Exception')) errors.push(msg.text());
  });
  page.on('pageerror', (err) => errors.push(String(err.message || err)));
  const count = (text) => lines.filter((line) => line.includes(text)).length;
  return {
    lines,
    errors,
    count,
    async waitFor(text, timeout = 90_000, times = 1) {
      const start = Date.now();
      while (count(text) < times) {
        if (Date.now() - start > timeout) {
          throw new Error(`Timed out waiting for "${text}". Last lines:\n${lines.slice(-30).join('\n')}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
    },
  };
}

// A press held for a few frames: a same-frame down/up can be missed by the game
async function click(page, [x, y]) {
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.waitForTimeout(120);
  await page.mouse.up();
  await page.waitForTimeout(600);
}

let shotIndex = 0;
async function shot(page, testInfo, name) {
  shotIndex++;
  const file = testInfo.outputPath(`${String(shotIndex).padStart(2, '0')}-${name}.png`);
  await page.screenshot({ path: file });
  await testInfo.attach(name, { path: file, contentType: 'image/png' });
}

async function boot(browser, baseURL, name, instrument) {
  const context = await browser.newContext({ baseURL, viewport: { width: 1280, height: 720 } });
  const page = await context.newPage();
  const log = collectLogs(page);
  await page.goto(relay ? `/?web-test&relay=${encodeURIComponent(relay)}` : '/?web-test');
  await page.locator('#yarg-start').click();
  await log.waitFor('Main menu ready', 170_000);
  await dismissAntiPiracyDialog(page);
  await page.evaluate((args) => window.yargTest.songCommand(args), `bot|${name}|${instrument}`);
  await log.waitFor('Done: bot');
  return { page, log, context };
}

// The part picker dialog's first item: the profile's instrument at its hardest difficulty
async function pickFirstPart(page, log, testInfo, who) {
  await click(page, AT.part);
  await page.waitForTimeout(800);
  await shot(page, testInfo, `${who}-part-picker`);
  const before = log.count('[Online] Part:');
  await click(page, AT.firstPart);
  await log.waitFor('[Online] Part:', 10_000, before + 1);
  await page.waitForTimeout(500);
}

const playingMixer = (page) => page.evaluate(() =>
  window.yargAudioDebug.mixers().find((m) => m.playing && m.stems.some((s) => s.decoded)) ?? null);

// The song's position at one wall-clock moment, so two pages can be compared
async function songPositionAt(page, mixerId) {
  return page.evaluate((id) => {
    const t = window.yargAudioDebug.songTime(id);
    return { songTime: t.songTime, wall: performance.timeOrigin + t.performanceNow };
  }, mixerId);
}

test('two players create, join and play a song online', async ({ browser }, testInfo) => {
  test.skip(relay === null, 'set YARG_WEB_RELAY to the relay\'s ws:// address');
  test.skip(!song, 'set YARG_WEB_SONGS to a folder with a .sng or .zip song');
  const baseURL = testInfo.project.use.baseURL;

  // Host: rooms screen, create a room
  const host = await boot(browser, baseURL, 'Host', 'FiveFretGuitar');
  await click(host.page, AT.online);
  await host.log.waitFor('[Online] Screen: Rooms');
  await host.page.waitForTimeout(1500);
  await shot(host.page, testInfo, 'host-rooms-empty');

  await click(host.page, AT.roomPassword);
  await host.page.keyboard.type(PASSWORD, { delay: 40 });
  await shot(host.page, testInfo, 'host-create-filled');
  await click(host.page, AT.create);
  await host.log.waitFor('[Online] Screen: Lobby', 20_000);
  await host.page.waitForTimeout(800);
  await shot(host.page, testInfo, 'host-lobby-alone');

  // Guest: sees the room, gets the password wrong, then right
  const guest = await boot(browser, baseURL, 'Guest', 'FiveFretBass');
  await click(guest.page, AT.online);
  await guest.log.waitFor('[Online] Screen: Rooms');
  await guest.page.waitForTimeout(2000);
  await shot(guest.page, testInfo, 'guest-rooms');

  await click(guest.page, AT.firstRoom);
  await shot(guest.page, testInfo, 'guest-join-form');
  await click(guest.page, AT.joinPassword);
  await guest.page.keyboard.type('not-it', { delay: 40 });
  await guest.page.keyboard.press('Enter');
  await guest.log.waitFor('Relay request failed', 20_000);
  await guest.page.waitForTimeout(500);
  await shot(guest.page, testInfo, 'guest-wrong-password');
  expect(guest.log.count('[Online] Screen: Lobby'), 'a wrong password keeps the guest out').toBe(0);

  await click(guest.page, AT.joinPassword);
  await guest.page.keyboard.type(PASSWORD, { delay: 40 });
  await guest.page.keyboard.press('Enter');
  await guest.log.waitFor('[Online] Screen: Lobby', 20_000);
  await guest.page.waitForTimeout(1500);
  await shot(guest.page, testInfo, 'guest-lobby');
  await shot(host.page, testInfo, 'host-lobby-two');

  // Host uploads the song and picks a part in the dialog that follows
  const chooser = host.page.waitForEvent('filechooser', { timeout: 15_000 });
  await click(host.page, AT.chooseSong);
  await (await chooser).setFiles(path.join(songsDir, song));
  await host.log.waitFor('[Online] Part picker', 60_000);
  await host.page.waitForTimeout(800);
  await shot(host.page, testInfo, 'host-part-picker');
  await click(host.page, AT.firstPart);
  await host.log.waitFor('[Online] Part:', 10_000);

  // Guest downloads it (screenshot while it does), then picks a part
  await guest.page.waitForTimeout(300);
  await shot(guest.page, testInfo, 'guest-downloading');
  await guest.log.waitFor('[Online] Song ready', 120_000);
  await guest.page.waitForTimeout(800);
  await shot(guest.page, testInfo, 'guest-song-ready');
  await pickFirstPart(guest.page, guest.log, testInfo, 'guest');
  await shot(guest.page, testInfo, 'guest-ready');
  expect(guest.log.count('[Online] Part: FiveFretBass'), 'the guest plays its own instrument').toBe(1);
  await host.page.waitForTimeout(800);
  await shot(host.page, testInfo, 'host-all-ready');

  // Start
  await click(host.page, AT.start);
  await expect.poll(() => playingMixer(host.page), { timeout: 90_000 }).not.toBeNull();
  await expect.poll(() => playingMixer(guest.page), { timeout: 90_000 }).not.toBeNull();
  await host.page.waitForTimeout(10_000);
  await shot(host.page, testInfo, 'host-gameplay');
  await shot(guest.page, testInfo, 'guest-gameplay');

  // Same song position on both pages at the same wall-clock moment
  const offsets = [];
  const hostMixer = await playingMixer(host.page);
  const guestMixer = await playingMixer(guest.page);
  for (let i = 0; i < 5; i++) {
    const a = await songPositionAt(host.page, hostMixer.id);
    const b = await songPositionAt(guest.page, guestMixer.id);
    offsets.push((b.songTime - (b.wall - a.wall) / 1000) - a.songTime);
    await host.page.waitForTimeout(1000);
  }
  const stats = { song, offsets };
  fs.writeFileSync(testInfo.outputPath('stats.json'), JSON.stringify(stats, null, 2));
  await testInfo.attach('stats', { body: JSON.stringify(stats, null, 2), contentType: 'application/json' });

  for (const offset of offsets) {
    expect(Math.abs(offset), `guest vs host song position (${offsets.map((o) => o.toFixed(3)).join(', ')} s)`)
      .toBeLessThan(0.05);
  }
  expect(host.log.errors, 'host exceptions').toEqual([]);
  expect(guest.log.errors, 'guest exceptions').toEqual([]);

  // The guest leaves mid-song: the host plays on
  await guest.context.close();
  await host.page.waitForTimeout(5000);
  await shot(host.page, testInfo, 'host-after-guest-left');
  const before = await host.page.evaluate((id) => window.yargAudioDebug.songTime(id).songTime, hostMixer.id);
  await host.page.waitForTimeout(2000);
  const after = await host.page.evaluate((id) => window.yargAudioDebug.songTime(id).songTime, hostMixer.id);
  expect(after - before, 'the host song keeps going').toBeGreaterThan(1.5);
  expect(host.log.errors, 'host exceptions after the guest left').toEqual([]);
  await host.context.close();
});
