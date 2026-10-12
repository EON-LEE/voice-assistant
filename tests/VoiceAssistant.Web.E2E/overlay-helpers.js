export async function closeSheet(page) {
  const close = page.locator('.overlay-sheet:not([hidden]) [data-close-sheet]');
  if (await close.count()) await close.click();
}
export async function openSettings(page) {
  if (await page.locator('#settings-sheet').isVisible()) return;
  await closeSheet(page);
  await page.locator('#settings-toggle').click();
}
export async function setConnectionMode(page, value) {
  await openSettings(page); await page.getByTestId('mode').selectOption(value);
}
export async function startMeeting(page) {
  await openSettings(page); await page.getByTestId('start').click();
}
export async function stopMeeting(page) {
  await closeSheet(page); await page.locator('#coach-toggle').click();
}
export async function setPause(page, value) {
  await openSettings(page);
  await page.getByTestId('pause').setChecked(value);
  await closeSheet(page);
}
export async function confirmConsent(page) {
  await openSettings(page); await page.getByTestId('consent').check();
}
