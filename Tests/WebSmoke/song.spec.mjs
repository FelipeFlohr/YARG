// Loading a song from disk on the web build (subproject 3): load, song screen, gameplay, score screen,
// "load another" through the real file picker, and the error cases. Screenshots go to test-results/.
import { test, expect } from '@playwright/test';
import path from 'node:path';
import { buildSongFixtures, buildDir } from './fixtures/make-song.mjs';
import { dismissAntiPiracyDialog } from './fixtures/dialogs.mjs';
import { routeFixtures } from './fixtures/remote.mjs';

const FIXTURES = '/__fixtures/song-build/';

test.describe.configure({ mode: 'serial' });
test.setTimeout(300_000);

test.beforeAll(() => {
  buildSongFixtures();
});
test.beforeEach(({ context }) => routeFixtures(context));

// Every console line, with a way to wait for the n-th line that contains some text
function collectLogs(page) {
  const lines = [];
  page.on('console', (msg) => lines.push(msg.text()));
  const count = (text) => lines.filter((line) => line.includes(text)).length;
  return {
    lines,
    count,
    // Waits until there are more than `after` lines containing `text` (take `after` from count() before acting)
    async waitFor(text, { timeout = 60_000, after = 0 } = {}) {
      const occurrence = after + 1;
      const start = Date.now();
      while (count(text) < occurrence) {
        if (Date.now() - start > timeout) {
          throw new Error(`Timed out waiting for "${text}" (#${occurrence}). Last lines:\n${lines.slice(-30).join('\n')}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
    },
  };
}

// Like bootToMenu in boot.spec.mjs, but with ?web-test so the test commands are honored
async function bootToMenu(page, log) {
  const errors = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error' && msg.text().includes('Exception')) errors.push(msg.text());
  });
  page.on('pageerror', (err) => {
    if (String(err.message || err).includes('Exception')) errors.push(String(err.message || err));
  });

  await page.goto('/?web-test');
  await page.locator('#yarg-start').click();

  const failed = page.locator('#yarg-error').waitFor({ state: 'visible', timeout: 170_000 })
    .then(async () => { throw new Error('Error screen: ' + await page.locator('#yarg-error-message').textContent()); });
  failed.catch(() => {});
  await Promise.race([log.waitFor('Main menu ready', { timeout: 170_000 }), failed]);
  // Lets the menu finish its first frames before input
  await page.waitForTimeout(1000);
  await dismissAntiPiracyDialog(page);
  return errors;
}

async function shot(page, testInfo, name) {
  const file = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path: file });
  await testInfo.attach(name, { path: file, contentType: 'image/png' });
}

const songFiles = (page) => page.evaluate(() => window.yargTest.listSongFiles());
const command = (page, name) => page.evaluate((c) => window.yargTest.songCommand(c), name);

test('loads a song, plays it to the score screen and loads another', async ({ page }, testInfo) => {
  const log = collectLogs(page);
  const errors = await bootToMenu(page, log);

  // Dropped .zip (same C# path as a real drop): opens the song screen from the main menu
  await page.evaluate((url) => window.yargTest.loadSongFromUrl(url), FIXTURES + 'web-fixture.zip');
  await log.waitFor('[WebSong] Loaded "Web Fixture" by "YARG Tests"');
  await log.waitFor('[WebSong] Screen: Song');
  await page.waitForTimeout(2000); // cover decode and layout
  await shot(page, testInfo, '1-song-screen');
  expect(log.count('Failed to decode an image'), 'cover decode').toBe(0);
  expect(log.count('[WebSong] Error'), 'no load errors').toBe(0);

  // The zip itself is deleted after extraction; only the extracted song stays
  let files = await songFiles(page);
  expect(files.some((f) => f.endsWith('.zip')), files.join('\n')).toBe(false);
  expect(files.some((f) => f.endsWith('/notes.chart')), files.join('\n')).toBe(true);

  // Play opens the normal difficulty menu (Enter = Start once the bot has a keyboard profile); Red (2) comes back
  await command(page, 'bot');
  await log.waitFor('[WebSongTest] Done: bot');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(1500);
  await shot(page, testInfo, '2-difficulty-select');
  let before = log.count('[WebSong] Screen: Song');
  await page.keyboard.press('Digit2');
  await log.waitFor('[WebSong] Screen: Song', { after: before });

  // Gameplay with the bot, until the score screen
  await command(page, 'play');
  await log.waitFor('[WebSongTest] Scene: Gameplay');
  await page.waitForTimeout(6000);
  await shot(page, testInfo, '3-gameplay');
  await log.waitFor('[WebSongTest] Scene: Score', { timeout: 120_000 });
  await page.waitForTimeout(2000);
  await shot(page, testInfo, '4-score-screen');

  // Continue (Enter) goes back to the song screen with "play again" and "load another"
  before = log.count('[WebSongTest] Scene: Menu');
  await page.keyboard.press('Enter');
  await log.waitFor('[WebSongTest] Scene: Menu', { after: before });
  await log.waitFor('[WebSong] Screen: Result');
  await page.waitForTimeout(1500);
  await shot(page, testInfo, '5-result');

  // "Load another" (3 = Yellow) opens the real file picker; the previous song leaves the filesystem
  const loadedBefore = log.count('[WebSong] Loaded "Web Fixture"');
  before = log.count('[WebSong] Screen: Song');
  const chooser = page.waitForEvent('filechooser', { timeout: 15_000 });
  await page.keyboard.press('Digit3');
  await (await chooser).setFiles(path.join(buildDir, 'web-fixture.sng'));
  await log.waitFor('[WebSong] Loaded "Web Fixture"', { after: loadedBefore });
  await log.waitFor('[WebSong] Screen: Song', { after: before });
  await page.waitForTimeout(2000);
  await shot(page, testInfo, '6-another-song');

  files = await songFiles(page);
  expect(files, 'only the new .sng is left').toHaveLength(1);
  expect(files[0]).toMatch(/\/tmp\/yarg-song\/[^/]+\/web-fixture\.sng$/);

  expect(errors, 'console.error with Exception').toEqual([]);
});

test('rejects bad songs and keeps the loaded one', async ({ page }, testInfo) => {
  const log = collectLogs(page);
  const errors = await bootToMenu(page, log);

  // A folder (as from <input webkitdirectory> or a dropped directory)
  await page.evaluate(({ base, list }) => window.yargTest.loadFolderFromUrls(base, list),
    { base: FIXTURES + 'web-fixture/', list: ['song.ini', 'notes.chart', 'song.ogg', 'album.png'] });
  await log.waitFor('[WebSong] Loaded "Web Fixture"');
  await log.waitFor('[WebSong] Screen: Song');
  const goodFiles = await songFiles(page);
  expect(goodFiles).toHaveLength(4);

  // Unsupported format: refused before copying anything
  await page.evaluate((url) => window.yargTest.loadSongFromUrl(url), FIXTURES + 'not-a-song.txt');
  await log.waitFor('[WebSong] Error');
  await page.waitForTimeout(1000);
  await shot(page, testInfo, '7-error-unsupported');
  expect(log.lines.filter((l) => l.includes('[WebSong] Error')).join('\n')).toMatch(/Unsupported format|Formato não suportado/);

  // A zip without audio: refused and cleaned up
  await page.evaluate((url) => window.yargTest.loadSongFromUrl(url), FIXTURES + 'no-audio.zip');
  await log.waitFor('[WebSong] Error', { after: 1 });
  await page.waitForTimeout(1000);
  await shot(page, testInfo, '8-error-no-audio');
  expect(log.lines.filter((l) => l.includes('[WebSong] Error')).pop()).toMatch(/no audio|não tem áudio/);

  // The good song is still the current one and nothing else is left behind
  expect(await songFiles(page)).toEqual(goodFiles);
  expect(errors, 'console.error with Exception').toEqual([]);
});
