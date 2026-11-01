// A fresh profile gets the one-time anti-piracy dialog over the main menu. Its button has no keyboard selection,
// so click "I understand" like a player would (1280x720 viewport). Held for a while: with software rendering a
// press and release can land in the same frame and the button misses the click.
export async function dismissAntiPiracyDialog(page) {
  await page.waitForTimeout(1000);
  await page.mouse.move(735, 537);
  await page.mouse.down();
  await page.waitForTimeout(150);
  await page.mouse.up();
  await page.waitForTimeout(500);
}
