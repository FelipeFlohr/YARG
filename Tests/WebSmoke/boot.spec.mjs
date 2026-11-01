import { test, expect } from '@playwright/test';
import fs from 'node:fs';

const fixtureProfile = JSON.parse(fs.readFileSync(new URL('./fixtures/profile.json', import.meta.url), 'utf8'));

// Loads the page, clicks the start overlay and waits for the boot's last log line.
// Fails if an Exception shows up as console.error.
export async function bootToMenu(page) {
  const errors = [];
  let ready;
  const menuReady = new Promise((resolve) => { ready = resolve; });

  page.on('console', (msg) => {
    const text = msg.text();
    if (msg.type() === 'error' && text.includes('Exception')) errors.push(text);
    if (text.includes('Main menu ready')) ready();
  });
  page.on('pageerror', (err) => {
    if (String(err.message || err).includes('Exception')) errors.push(String(err.message || err));
  });

  await page.goto('/');
  await page.locator('#yarg-start').click();

  const failed = new Promise((_, reject) => {
    page.locator('#yarg-error').waitFor({ state: 'visible', timeout: 170_000 })
      .then(async () => reject(new Error('Error screen: ' + await page.locator('#yarg-error-message').textContent())))
      .catch(() => {});
  });
  await Promise.race([menuReady, failed]);

  expect(errors, 'console.error with Exception').toEqual([]);
}

test('boots to the main menu', async ({ page }) => {
  await bootToMenu(page);
});

test('profiles round-trip through localStorage', async ({ page }) => {
  await bootToMenu(page);
  const read = async (key) => page.evaluate((k) => localStorage.getItem(k), key);

  expect(await read('yarg.settings')).not.toBeNull();
  expect(await read('yarg.schemaVersion')).toBe('1');

  let profiles = JSON.parse((await read('yarg.profiles')) ?? '[]');
  if (!Array.isArray(profiles) || profiles.length === 0) {
    profiles = [fixtureProfile];
  }
  profiles[0].Name = 'Ação 🎸';
  await page.evaluate((p) => localStorage.setItem('yarg.profiles', p), JSON.stringify(profiles));

  await bootToMenu(page); // reloads
  profiles = JSON.parse(await read('yarg.profiles'));
  expect(profiles[0].Name).toBe('Ação 🎸');
});

test('corrupt profiles still boot', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('yarg.profiles', '{not json'));
  await bootToMenu(page);
});

test('storage failure still boots', async ({ page }) => {
  await page.addInitScript(() => {
    Storage.prototype.setItem = () => { throw new Error('QuotaExceededError'); };
  });
  await bootToMenu(page);
});
