import { test, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSettings, closeSheet, startMeeting, stopMeeting, setConnectionMode } from '../overlay-helpers.js';
test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] } });
function silentClip() {
  const wav = Buffer.alloc(44 + 3200);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(16000, 24); wav.writeUInt32LE(32000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34);
  wav.write('data', 36); wav.writeUInt32LE(3200, 40); return wav;
}

async function mocks(page) {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.route('**/api/assist/enrich', route => {
    const { text, kind } = route.request().postDataJSON(); const words = text.split(' '), chunks = [];
    for (let i = 0; i < words.length; i += 3) chunks.push({ en: words.slice(i, i + 3).join(' '), ko: '가나다' });
    return route.fulfill({ json: { korean: '다음 단계를 함께 확인해요.', pronunciation: kind === 'question' ? null : chunks } });
  });
  let socket;
  await page.routeWebSocket('**/api/meeting*', value => {
    socket = value;
    value.onMessage(message => {
      if (typeof message === 'string' && JSON.parse(message).type === 'session.start') value.send(JSON.stringify({ type: 'session.ready' }));
    });
  });
  return { send: event => socket.send(JSON.stringify(event)), reply: () => {
    for (const event of [{ type: 'transcript.final', turnId: 't', revision: 1, text: 'What should we do next?' },
      { type: 'response.started', turnId: 't', responseId: 'r' },
      { type: 'response.delta', turnId: 't', responseId: 'r', text: 'Let me check the next step.' },
      { type: 'response.completed', turnId: 't', responseId: 'r', text: 'Let me check the next step.', sources: [], grounding: 'disabled' }])
      socket.send(JSON.stringify(event));
  } };
}
async function screenshot(page, name) {
  if (!process.env.VOICE_ASSISTANT_OVERLAY_SCREENSHOTS) return;
  await mkdir(process.env.VOICE_ASSISTANT_OVERLAY_SCREENSHOTS, { recursive: true });
  await page.screenshot({ path: join(process.env.VOICE_ASSISTANT_OVERLAY_SCREENSHOTS, name), fullPage: false, animations: 'disabled' });
}
test('viewport card starts with closed settings and traps focus then restores it on Escape', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('#settings-sheet')).toBeHidden();
  await expect(page.locator('#overlay-card')).toBeVisible();
  expect(await page.locator('h1').count()).toBe(0);
  await page.locator('#settings-toggle').click();
  await expect(page.locator('#settings-sheet')).toBeVisible();
  await expect(page.locator('#settings-close')).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  expect(await page.evaluate(() => document.activeElement?.closest('#settings-sheet') !== null)).toBe(true);
  await page.keyboard.press('Escape');
  await expect(page.locator('#settings-sheet')).toBeHidden();
  await expect(page.locator('#settings-toggle')).toBeFocused();
  await expect(page.locator('#transcript-disclosure')).not.toHaveAttribute('open');
  await page.locator('#coach-toggle').click();
  await expect(page.locator('#coach-status')).toContainText('DEMO');
  await page.locator('#coach-toggle').click();
});
test('opacity is the only persistent preference, compact is optional, denied storage is safe', async ({ page }) => {
  await page.goto('/'); await page.locator('#opacity-toggle').click();
  await expect(page.locator('#overlay-opacity')).toHaveValue('80');
  await page.locator('#overlay-opacity').fill('35'); await page.locator('#overlay-opacity').dispatchEvent('input');
  await expect(page.locator('#overlay-opacity-value')).toHaveText('35%');
  expect(await page.evaluate(() => ({ value: document.querySelector('#overlay-card').style.getPropertyValue('--overlay-alpha'),
    keys: Object.keys(localStorage) }))).toEqual({ value: '0.35', keys: ['voice-assistant.overlay-opacity'] });
  await page.locator('#overlay-compact').check(); await page.keyboard.press('Escape');
  expect((await page.locator('#overlay-card').boundingBox()).width).toBeLessThanOrEqual(420);
  await page.reload(); await page.locator('#opacity-toggle').click();
  await expect(page.locator('#overlay-opacity')).toHaveValue('35');
  await expect(page.locator('#overlay-compact')).not.toBeChecked();
  await page.addInitScript(() => { Storage.prototype.setItem = () => { throw new Error('denied'); }; });
  await page.reload(); await page.locator('#opacity-toggle').click();
  await page.locator('#overlay-opacity').fill('65'); await page.locator('#overlay-opacity').dispatchEvent('input');
  await page.keyboard.press('Escape');
  await expect(page.locator('#overlay-notice')).toContainText('storage is unavailable');
});
test('Float unsupported is honest and reduced transparency / forced colors stay opaque', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(window, 'documentPictureInPicture', { configurable: true, value: undefined }));
  await page.goto('/');
  await expect(page.locator('#float-toggle')).toBeDisabled();
  await expect(page.locator('#float-toggle')).toHaveAttribute('title', /Chrome or Edge/);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
  expect(await page.locator('#overlay-card').evaluate(node => getComputedStyle(node).backgroundColor)).toBe('rgb(12, 16, 24)');
  expect(await page.locator('#overlay-card').evaluate(node => getComputedStyle(node).backdropFilter)).toBe('none');
  await page.emulateMedia({ forcedColors: 'active' });
  expect(await page.locator('#overlay-card').evaluate(node => getComputedStyle(node).backdropFilter)).toBe('none');
});
test('PiP moves—not clones—live DOM, uses visible-window render fallback and returns state on close (mock)', async ({ page, context }) => {
  const ws = await mocks(page);
  await page.addInitScript(() => {
    window.__pipCalls = 0;
    Object.defineProperty(window, 'documentPictureInPicture', { configurable: true, value: {
      requestWindow: async options => {
        window.__pipCalls++; window.__pipOptions = options;
        const win = window.open('about:blank', '_blank', 'width=460,height=680');
        window.__pip = win; win.__rafCalls = 0;
        // Simulate a throttled rAF in this owner; its own setTimeout fallback must still paint.
        win.requestAnimationFrame = () => { win.__rafCalls++; return 1; }; win.cancelAnimationFrame = () => {};
        return win;
      },
    } });
  });
  await page.goto('/'); await setConnectionMode(page, 'synthetic'); await startMeeting(page);
  ws.reply(); await expect(page.locator('#reply')).toHaveText('Let me check the next step.');
  await page.locator('#pin').click();
  await page.evaluate(() => { window.__originalCard = document.querySelector('#overlay-card'); });
  const popupEvent = context.waitForEvent('page'); await page.locator('#float-toggle').click();
  const floating = await popupEvent;
  await expect(floating.locator('#overlay-card')).toBeVisible();
  expect(await page.locator('#overlay-card').count()).toBe(0);
  expect(await page.evaluate(() => window.__pip.document.querySelector('#overlay-card') === window.__originalCard)).toBe(true);
  expect(await page.evaluate(() => window.__pipOptions)).toEqual({ width: 460, height: 680 });
  expect(await floating.locator('head link[rel=stylesheet],head style').count()).toBeGreaterThan(0);
  await expect(floating.locator('#pinned')).toHaveText('Let me check the next step.');
  await page.evaluate(() => Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' }));
  ws.send({ type: 'response.started', turnId: 't', responseId: 'r2' });
  ws.send({ type: 'response.delta', turnId: 't', responseId: 'r2', text: 'Visible window update' });
  await expect(floating.locator('#reply')).toHaveText('Visible window update');
  expect(await floating.evaluate(() => window.__rafCalls)).toBeGreaterThan(0);
  await floating.locator('#settings-toggle').click();
  await expect(floating.locator('#settings-close')).toBeFocused(); await floating.keyboard.press('Escape');
  await floating.locator('#pin').click(); await expect(floating.locator('#pinned')).toHaveText('Visible window update');
  await expect.poll(async () => (await floating.locator('#elapsed').textContent()) !== '00:00').toBe(true);
  await floating.close();
  await expect(page.locator('#overlay-card')).toBeVisible();
  await expect(page.locator('#pinned')).toHaveText('Visible window update');
  await expect(page.locator('#float-toggle')).toBeFocused();
  await page.locator('#coach-toggle').click();
  await expect(page.locator('#coach-status')).toContainText('Stopped');
});

test('new Start without live consent prompts settings and unconfirmed profile stays visibly editable', async ({ page }) => {
  await mocks(page); await page.goto('/'); await setConnectionMode(page, 'live'); await closeSheet(page);
  await page.locator('#coach-toggle').click();
  await expect(page.locator('#settings-sheet')).toBeVisible();
  await expect(page.locator('#consent')).not.toBeChecked();
  await expect(page.locator('#overlay-notice')).toContainText('confirm permission');
  await setConnectionMode(page, 'synthetic');
  await page.locator('#meeting-options').evaluate(details => { details.open = true; });
  await page.locator('#profile-name').fill('Fictional Mina'); await page.getByTestId('start').click();
  await expect(page.locator('#settings-validation-error')).toContainText('Confirm');
  await expect(page.locator('#profile-project')).toBeVisible();
  await page.locator('#profile-confirmed').check(); await page.getByTestId('start').click();
  await expect(page.locator('#settings-sheet')).toBeHidden();
  await page.locator('#coach-toggle').click();
});
test('meeting card is viewport-contained at desktop, floating and mobile sizes with readable settings', async ({ page }) => {
  const ws = await mocks(page); await page.goto('/'); await setConnectionMode(page, 'synthetic'); await startMeeting(page);
  ws.reply(); await expect(page.locator('#reply-ko')).toBeVisible();
  for (const [width, height, name] of [[1100, 800, 'desktop'], [460, 680, 'floating'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && document.documentElement.scrollHeight <= innerHeight)).toBe(true);
    const card = await page.locator('#overlay-card').boundingBox();
    expect(card.height).toBeLessThanOrEqual(height); expect(card.width).toBeLessThanOrEqual(width);
    await screenshot(page, `overlay-meeting-${name}.png`);
    await openSettings(page);
    await expect(page.locator('#pause')).toBeVisible();
    await screenshot(page, `overlay-settings-${name}.png`);
    await page.keyboard.press('Escape');
  }
  await stopMeeting(page);
});
test('practice setup sheet gives consent then closes for compact round and can reopen without stopping', async ({ page }) => {
  await mocks(page);
  await page.route('**/api/practice/turn', route => route.fulfill({ json: {
    text: 'What will you focus on today?', done: false, turn: 1, grounding: 'disabled', sources: [],
  } }));
  await page.route('**/api/assist/speak', route => route.fulfill({ body: silentClip(), contentType: 'audio/wav' }));
  await page.goto('/'); await page.locator('#practice-chip').click();
  await expect(page.locator('#practice-sheet')).toBeVisible();
  await page.locator('#practice-consent').check(); await page.locator('#practice-start').click();
  await expect(page.locator('#practice-question')).toHaveText('What will you focus on today?');
  await expect(page.locator('#practice-sheet')).toBeHidden();
  for (const [width, height, name] of [[1100, 800, 'desktop'], [460, 680, 'floating'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && document.documentElement.scrollHeight <= innerHeight)).toBe(true);
    await screenshot(page, `overlay-practice-${name}.png`);
  }
  await page.locator('#practice-setup-toggle').click();
  await expect(page.locator('#practice-count')).toBeDisabled();
  await page.keyboard.press('Escape'); await page.locator('#practice-stop').click();
  await expect(page.locator('#practice-conversation')).toBeHidden();
});

test('practice handlers and microphone session survive PiP adoption and restoration (mock)', async ({ page, context }) => {
  const ws = await mocks(page);
  await page.addInitScript(() => Object.defineProperty(window, 'documentPictureInPicture', { configurable: true, value: {
    requestWindow: async () => window.open('about:blank', '_blank', 'width=460,height=680'),
  } }));
  await page.route('**/api/practice/turn', route => route.fulfill({ json: {
    text: 'What will you focus on today?', done: false, turn: 1, grounding: 'disabled', sources: [],
  } }));
  await page.route('**/api/assist/speak', route => route.fulfill({ body: silentClip(), contentType: 'audio/wav' }));
  await page.route('**/api/practice/feedback', route => route.fulfill({ json: {
    correctedEnglish: 'I will check the plan.', easierEnglish: 'I will check.', feedbackKo: '핵심을 잘 말했어요.',
    points: [], clarity: 4,
  } }));
  await page.goto('/'); await page.locator('#practice-chip').click();
  await page.locator('#practice-consent').check(); await page.locator('#practice-start').click();
  await expect(page.locator('#practice-question')).toHaveText('What will you focus on today?');
  const next = context.waitForEvent('page'); await page.locator('#float-toggle').click(); const floating = await next;
  await floating.locator('#practice-answer').click();
  ws.send({ type: 'transcript.final', turnId: 'answer-pip', revision: 1, text: 'I will check the plan.' });
  await expect(floating.locator('#practice-answer-text')).toContainText('I will check the plan.');
  await floating.locator('#practice-done').click();
  await expect(floating.locator('#practice-feedback')).toContainText('핵심을 잘 말했어요.');
  await floating.close();
  await expect(page.locator('#practice-feedback')).toContainText('핵심을 잘 말했어요.');
  await page.locator('#practice-stop').click();
  await expect(page.locator('#practice-feedback')).toBeHidden();
});
