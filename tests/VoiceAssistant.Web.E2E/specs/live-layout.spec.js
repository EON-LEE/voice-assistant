import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] } });

const hostRoot = new URL('../../../src/VoiceAssistant.WebOverlay/', import.meta.url);
const hostFile = name => readFileSync(new URL(name, hostRoot), 'utf8');
const shell = hostFile('ShellScript.cs').split('"""')[1];
// The host script only activates on its configured origin, so build it for the origin the app is served from.
const shellFor = origin => shell.replace('__ORIGIN__', JSON.stringify(origin)).replace('__OPACITY__', '0.8');
const english = 'I can confirm the next milestone after reviewing the dependencies with the team. '.repeat(12);
const korean = '팀과 의존성을 검토한 뒤 다음 일정을 확인하겠습니다. '.repeat(18);
const token = 'LongUnbrokenSyntheticIdentifier'.repeat(10);

// Captions and Korean meanings are off unless chosen. The layout and Korean tests look at the fullest screen, so they switch both on
// before the page loads; a test that wants the real defaults passes `null`.
const everything = { showCaptions: true, showKorean: true };
async function seedSettings(page, settings) {
  if (!settings) return;
  await page.addInitScript(value => {
    if (localStorage.getItem('live-coach-settings') === null) localStorage.setItem('live-coach-settings', JSON.stringify(value));
  }, settings);
}

async function fixture(page, native, expanded = true, settings = everything) {
  await page.route('**/api/client-config', route => route.fulfill({
    json: { mode: 'Fake', webSocketPath: '/api/meeting' },
  }));
  await seedSettings(page, settings);
  if (native) await page.addInitScript(script => {
    window.__shellMessages = [];
    window.chrome = { webview: { postMessage: value => window.__shellMessages.push(value) } };
    (0, eval)(script);
  }, shellFor(new URL(test.info().project.use.baseURL).origin));
  await page.goto('/live.html');
  await expect(page.locator('#capture-status')).toContainText('꺼짐');
  await page.evaluate(({ english, korean, token, expanded }) => {
    document.querySelector('#live-details').open = expanded;
    document.querySelector('#question-text').textContent = 'What is the next milestone?';
    document.querySelector('#conversation').innerHTML = '';
    for (let i = 0; i < 12; i++) {
      const line = document.createElement('div');
      line.className = 'live-line current';
      for (const [className, text] of [['en', english + token], ['ko', korean + token]]) {
        const p = document.createElement('p'); p.className = className; p.textContent = text; line.append(p);
      }
      document.querySelector('#conversation').append(line);
    }
    document.querySelector('#answer-text').textContent = english + token;
    document.querySelector('#answer-korean').textContent = korean + token;
    document.querySelector('#alternative-card').hidden = false;
    document.querySelector('#alternative-text').textContent = english;
    document.querySelector('#alternative-korean').textContent = korean;
    document.querySelector('#sources-text').textContent = token;
  }, { english, korean, token, expanded });
}

async function meetingFixture(page, { replyKorean = false, sessionUpdate = false, settings = everything } = {}) {
  const commands = [];
  const translations = [];
  await page.route('**/api/client-config', route => route.fulfill({
    json: { mode: 'Fake', webSocketPath: '/api/meeting' },
  }));
  await page.route('**/api/assist/enrich', route => {
    const { kind, text } = route.request().postDataJSON();
    translations.push({ kind, text });
    return route.fulfill({ json: { korean: `뜻: ${text}`, pronunciation: null } });
  });
  await seedSettings(page, settings);
  let socket, effective = { responseMode: 'grounded', replyKorean: false };
  await page.routeWebSocket('**/api/meeting*', ws => {
    socket = ws;
    ws.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message); commands.push(command);
      if (command.type === 'session.start') {
        effective = { responseMode: command.options.responseMode, replyKorean: command.options.replyKorean === true };
        ws.send(JSON.stringify({ type: 'session.ready', ...(replyKorean ? { protocolVersion: 1, replyKorean: true } : {}),
          ...(sessionUpdate ? { sessionUpdate: true } : {}) }));
      }
      if (command.type === 'session.update' && sessionUpdate) {
        effective = { ...effective, ...command.options };
        ws.send(JSON.stringify({ type: 'session.updated', ...effective }));
      }
    });
  });
  await page.goto('/live.html');
  await expect(page.locator('#capture-status')).toContainText('꺼짐');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator('[data-action="start"]').click();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  return { commands, translations, send: event => socket.send(JSON.stringify(event)) };
}

function reply(ws, id, question, text, grounding = 'disabled') {
  ws.send({ type: 'transcript.final', turnId: `t${id}`, revision: 1, text: question });
  ws.send({ type: 'response.started', turnId: `t${id}`, responseId: `r${id}` });
  ws.send({ type: 'response.completed', turnId: `t${id}`, responseId: `r${id}`, text,
    suggestions: [text, 'Could we check that together?'], sources: [], grounding });
}

test('reading view prioritizes one English answer and keeps optional content closed', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 550, height: 860 });
  await fixture(page, false, false);
  await expect(page.locator('#live-details')).not.toHaveAttribute('open');
  await expect(page.locator('#conversation')).toBeHidden();
  await expect(page.locator('#alternative-card')).toBeHidden();
  await expect(page.locator('#answer-text')).toBeVisible();
  expect(await page.locator('.live-card:visible').count()).toBe(1);
  expect(await page.locator('#answer-text').evaluate(el => parseFloat(getComputedStyle(el).fontSize)))
    .toBeGreaterThan(await page.locator('#answer-korean').evaluate(el => parseFloat(getComputedStyle(el).fontSize)));
  await page.locator('#live-details summary').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#conversation')).toBeVisible();
  await expect(page.locator('#alternative-card')).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(page.locator('#live-details')).not.toHaveAttribute('open');
  await page.evaluate(() => {
    document.querySelector('#answer-text').textContent = 'I can confirm the next milestone after we review the dependencies.';
    document.querySelector('#answer-korean').textContent = '의존성을 검토한 뒤 다음 일정을 확인하겠습니다.';
  });
  await page.screenshot({ path: testInfo.outputPath('focused-reading.png') });
});

const replyRequests = ws => ws.translations.filter(item => item.kind === 'reply');
const pushKorean = (ws, id, index, extra) => ws.send({ type: 'response.korean', turnId: `t${id}`, responseId: `r${id}`, index, ...extra });

test('the server pushes the Korean meaning of each reply line, so no per-line translation request is made', async ({ page }) => {
  const ws = await meetingFixture(page, { replyKorean: true });
  expect(ws.commands.find(command => command.type === 'session.start').options.replyKorean).toBe(true);
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-korean')).toHaveText('한국어 번역 중…');
  pushKorean(ws, 1, 0, { korean: '먼저 계획을 확인해야 합니다.' });
  await expect(page.locator('#answer-korean')).toHaveText('먼저 계획을 확인해야 합니다.');
  await expect(page.locator('#alternative-korean')).toHaveText('한국어 번역 중…');
  pushKorean(ws, 1, 1, { korean: '함께 확인해 볼 수 있을까요?' });
  await expect(page.locator('#alternative-korean')).toHaveText('함께 확인해 볼 수 있을까요?');
  expect(replyRequests(ws)).toEqual([]);
});

test('Korean pushed before the reply completes is shown as soon as the reply completes', async ({ page }) => {
  const ws = await meetingFixture(page, { replyKorean: true });
  ws.send({ type: 'transcript.final', turnId: 't1', revision: 1, text: 'What comes first?' });
  ws.send({ type: 'response.started', turnId: 't1', responseId: 'r1' });
  ws.send({ type: 'response.delta', turnId: 't1', responseId: 'r1', text: 'We should check' });
  pushKorean(ws, 1, 0, { korean: '먼저 계획을 확인해야 합니다.' });
  ws.send({ type: 'response.completed', turnId: 't1', responseId: 'r1', text: 'We should check the plan.',
    suggestions: ['We should check the plan.', 'Could we check that together?'], sources: [], grounding: 'disabled' });
  await expect(page.locator('#answer-korean')).toHaveText('먼저 계획을 확인해야 합니다.');
  pushKorean(ws, 1, 1, { korean: '함께 확인해 볼 수 있을까요?' });
  await expect(page.locator('#alternative-korean')).toHaveText('함께 확인해 볼 수 있을까요?');
  expect(replyRequests(ws)).toEqual([]);
});

test('a reply line the server could not translate is translated by the client, and only that line', async ({ page }) => {
  const ws = await meetingFixture(page, { replyKorean: true });
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  pushKorean(ws, 1, 0, { korean: '먼저 계획을 확인해야 합니다.' });
  pushKorean(ws, 1, 1, { failed: true });
  await expect(page.locator('#answer-korean')).toHaveText('먼저 계획을 확인해야 합니다.');
  await expect(page.locator('#alternative-korean')).toHaveText('뜻: Could we check that together?');
  expect(replyRequests(ws)).toEqual([{ kind: 'reply', text: 'Could we check that together?' }]);
});

test('Korean that the server never pushes is translated by the client after a short wait', async ({ page }) => {
  const ws = await meetingFixture(page, { replyKorean: true });
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-korean')).toHaveText('한국어 번역 중…');
  expect(replyRequests(ws)).toEqual([]);
  await expect(page.locator('#answer-korean')).toHaveText('뜻: We should check the plan.', { timeout: 10_000 });
  await expect(page.locator('#alternative-korean')).toHaveText('뜻: Could we check that together?');
  expect(replyRequests(ws).map(item => item.text).sort()).toEqual(['Could we check that together?', 'We should check the plan.']);
});

test('a server that does not announce the push gets no waiting: the client translates the reply at once', async ({ page }) => {
  const ws = await meetingFixture(page);
  expect(ws.commands.find(command => command.type === 'session.start').options.replyKorean).toBe(true);
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-korean')).toHaveText('뜻: We should check the plan.', { timeout: 2000 });
  await expect(page.locator('#alternative-korean')).toHaveText('뜻: Could we check that together?', { timeout: 2000 });
});

test('drafts do not replace complete answers and hold keeps its exact question without pausing capture', async ({ page }) => {
  const ws = await meetingFixture(page);
  ws.send({ type: 'transcript.final', turnId: 't1', revision: 1, text: 'What is the next step?' });
  ws.send({ type: 'response.started', turnId: 't1', responseId: 'r1' });
  ws.send({ type: 'response.delta', turnId: 't1', responseId: 'r1', text: 'Incomplete draft' });
  await expect(page.locator('#reading-status')).toContainText('답변 준비 중');
  await expect(page.locator('#answer-text')).not.toContainText('Incomplete draft');
  await expect(page.locator('#hold-answer')).toBeDisabled();
  ws.send({ type: 'response.completed', turnId: 't1', responseId: 'r1',
    text: 'Review the dependencies first.', sources: [], grounding: 'no_matches' });
  await expect(page.locator('#answer-text')).toHaveText('Review the dependencies first.');
  await expect(page.locator('#answer-korean')).toHaveText('뜻: Review the dependencies first.');
  await expect(page.locator('#grounding-badge')).toContainText('문서 근거 없음');
  await expect(page.locator('#grounding-badge')).toBeVisible();
  await page.locator('#hold-answer').click();
  await expect(page.locator('#hold-answer')).toHaveAttribute('aria-pressed', 'true');
  await page.evaluate(() => { window.__heldAnswerNode = document.querySelector('#answer-text').firstChild; });
  for (const id of [2, 3]) {
    reply(ws, id, `Question ${id}?`, `Answer ${id}.`);
    await expect(page.locator('#reading-status')).toContainText('새 답변 도착');
    await expect(page.locator('#answer-text')).toHaveText('Review the dependencies first.');
    await expect(page.locator('#question-text')).toHaveText('What is the next step?');
    await expect(page.locator('#answer-korean')).toHaveText('뜻: Review the dependencies first.');
  }
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  expect(await page.evaluate(() => document.querySelector('#answer-text').firstChild === window.__heldAnswerNode)).toBe(true);
  expect(ws.commands.map(value => value.type)).not.toContain('response.cancel');
  await page.locator('#hold-answer').click();
  await expect(page.locator('#answer-text')).toHaveText('Answer 3.');
  await expect(page.locator('#question-text')).toHaveText('Question 3?');
  await expect(page.locator('#answer-korean')).toHaveText('뜻: Answer 3.');
  await expect(page.locator('#hold-answer')).toHaveAttribute('aria-pressed', 'false');
  ws.send({ type: 'transcript.final', turnId: 't4', revision: 1, text: 'Question 4?' });
  ws.send({ type: 'response.started', turnId: 't4', responseId: 'r4' });
  ws.send({ type: 'response.delta', turnId: 't4', responseId: 'r4', text: 'New incomplete draft' });
  await expect(page.locator('#primary-label')).toContainText('이전 질문');
  await expect(page.locator('#answer-text')).toHaveText('Answer 3.');
  await expect(page.locator('#question-text')).toHaveText('Question 3?');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator('[data-action="stop"]').click();
  expect(ws.commands.map(value => value.type)).toContain('session.stop');
});

test('reading hold and response updates survive moving into and out of a floating window', async ({ page, context }) => {
  await page.addInitScript(() => Object.defineProperty(window, 'documentPictureInPicture', { configurable: true, value: {
    requestWindow: async () => window.open('about:blank', '_blank', 'width=550,height=860'),
  } }));
  const ws = await meetingFixture(page);
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-text')).toHaveText('We should check the plan.');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  const popup = context.waitForEvent('page');
  await page.locator('[data-action="float"]').click();
  const floating = await popup;
  await expect(floating.locator('#answer-text')).toHaveText('We should check the plan.');
  await floating.locator('#hold-answer').click();
  reply(ws, 2, 'And then?', 'Then we can confirm the next step.');
  await expect(floating.locator('#reading-status')).toContainText('새 답변 도착');
  await expect(floating.locator('#question-text')).toHaveText('What comes first?');
  await floating.locator('#hold-answer').click();
  await expect(floating.locator('#answer-text')).toHaveText('Then we can confirm the next step.');
  await floating.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await floating.keyboard.press('Escape');
  await expect(floating.locator('#live-menu')).toBeHidden();
  await floating.close();
  await expect(page.locator('#answer-text')).toHaveText('Then we can confirm the next step.');
  await expect(page.locator('#question-text')).toHaveText('And then?');
});

// ---------- Quick settings: document search, captions and Korean meaning ----------

const updates = ws => ws.commands.filter(command => command.type === 'session.update');
const startOptions = ws => ws.commands.find(command => command.type === 'session.start').options;
const pressed = (page, id) => page.locator(`#${id}`).getAttribute('aria-pressed');
async function menuAction(page, action) {
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator(`[data-action="${action}"]`).click();
}

test('by default only the English answer shows: no captions, no Korean meaning, and no Korean is requested', async ({ page }) => {
  const ws = await meetingFixture(page, { settings: null });
  expect(startOptions(ws).replyKorean).toBe(false);
  expect(await pressed(page, 'doc-on')).toBe('true');
  expect(await pressed(page, 'doc-off')).toBe('false');
  expect(await pressed(page, 'toggle-captions')).toBe('false');
  expect(await pressed(page, 'toggle-korean')).toBe('false');
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-text')).toHaveText('We should check the plan.');
  await expect(page.locator('#answer-meaning')).toBeHidden();
  await expect(page.locator('#caption-block')).toBeHidden();
  await expect(page.locator('#conversation-section')).toBeHidden();
  await expect(page.locator('#details-summary')).toHaveText('대안 · 상세 근거');
  expect(await page.locator('.live-card:visible').count()).toBe(1);
  await page.waitForTimeout(250);
  expect(ws.translations).toEqual([]);
});

test('each quick setting reaches a connected session that supports changes, and captions alone send nothing', async ({ page }) => {
  const ws = await meetingFixture(page, { settings: null, sessionUpdate: true });
  expect(startOptions(ws)).toMatchObject({ responseMode: 'grounded', replyKorean: false });
  const sent = [];
  for (const [id, options] of [['doc-off', { responseMode: 'conversation' }], ['doc-on', { responseMode: 'grounded' }],
    ['toggle-korean', { replyKorean: true }], ['toggle-korean', { replyKorean: false }]]) {
    await page.locator(`#${id}`).click();
    sent.push({ type: 'session.update', options });
    await expect.poll(() => updates(ws)).toEqual(sent);
  }
  await page.locator('#toggle-captions').click();
  await page.waitForTimeout(250);
  expect(updates(ws)).toEqual(sent);
  await expect(page.locator('#quick-note')).toBeHidden();
  await expect(page.locator('#session-error')).toBeHidden();
});

test('a server that cannot change settings during a session says so, and the next connection uses the choice', async ({ page }) => {
  const ws = await meetingFixture(page, { settings: null });
  await page.locator('#doc-off').click();
  await expect(page.locator('#quick-note')).toContainText('이 서버는 연결 중 문서검색 변경을 지원하지 않아요');
  expect(await pressed(page, 'doc-off')).toBe('true');
  await page.locator('#doc-on').click();
  await expect(page.locator('#quick-note')).toBeHidden();
  await page.locator('#doc-off').click();
  await expect(page.locator('#quick-note')).toBeVisible();
  await menuAction(page, 'stop');
  await expect(page.locator('#capture-status')).toContainText('꺼짐');
  await expect(page.locator('#quick-note')).toBeHidden();
  await menuAction(page, 'start');
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  expect(ws.commands.filter(command => command.type === 'session.start').map(command => command.options.responseMode))
    .toEqual(['grounded', 'conversation']);
  expect(updates(ws)).toEqual([]);
  await expect(page.locator('#quick-note')).toBeHidden();
});

test('Korean meaning stays off without any request, turns on for what is on screen, and off again stops it', async ({ page }) => {
  const ws = await meetingFixture(page, { settings: { showCaptions: true } });
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-text')).toHaveText('We should check the plan.');
  await expect(page.locator('#answer-meaning')).toBeHidden();
  await page.waitForTimeout(250);
  expect(ws.translations).toEqual([]);
  await page.locator('#toggle-korean').click();
  await expect(page.locator('#answer-meaning')).toBeVisible();
  await expect(page.locator('#answer-korean')).toHaveText('뜻: We should check the plan.');
  await expect(page.locator('#alternative-korean')).toHaveText('뜻: Could we check that together?');
  await expect.poll(() => ws.translations.filter(item => item.kind === 'question')).toEqual([{ kind: 'question', text: 'What comes first?' }]);
  reply(ws, 2, 'And then?', 'Then we can confirm.');
  await expect(page.locator('#answer-korean')).toHaveText('뜻: Then we can confirm.');
  await page.locator('#toggle-korean').click();
  await expect(page.locator('#answer-meaning')).toBeHidden();
  const requested = ws.translations.length;
  reply(ws, 3, 'Anything else?', 'No, that is all.');
  await expect(page.locator('#answer-text')).toHaveText('No, that is all.');
  await page.waitForTimeout(250);
  expect(ws.translations).toHaveLength(requested);
  await page.locator('#toggle-korean').click();
  await expect(page.locator('#answer-korean')).toHaveText('뜻: No, that is all.');
});

test('captions show what was heard, and translate it only while Korean meaning is on too', async ({ page }) => {
  const ws = await meetingFixture(page, { settings: { showKorean: true } });
  reply(ws, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-korean')).toHaveText('뜻: We should check the plan.');
  await expect(page.locator('#caption-block')).toBeHidden();
  await expect(page.locator('#conversation-section')).toBeHidden();
  expect(ws.translations.filter(item => item.kind === 'question')).toEqual([]);
  await page.locator('#toggle-captions').click();
  await expect(page.locator('#caption-block')).toBeVisible();
  await expect(page.locator('#question-text')).toHaveText('What comes first?');
  await expect(page.locator('#details-summary')).toHaveText('대화 · 대안 · 상세 근거');
  await page.locator('#live-details summary').click();
  await expect(page.locator('#conversation-section')).toBeVisible();
  await expect(page.locator('#conversation .live-line .en')).toHaveText('What comes first?');
  await expect(page.locator('#conversation .live-line .ko')).toHaveText('뜻: What comes first?');
  await page.locator('#toggle-captions').click();
  await expect(page.locator('#caption-block')).toBeHidden();
  await expect(page.locator('#conversation-section')).toBeHidden();
  await expect(page.locator('#details-summary')).toHaveText('대안 · 상세 근거');
});

test('the quick settings are remembered, and settings saved by an older version load with both switches off', async ({ page }) => {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.goto('/live.html');
  await page.locator('#doc-off').click();
  await page.locator('#toggle-captions').click();
  await page.locator('#toggle-korean').click();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings'))))
    .toMatchObject({ responseMode: 'conversation', documentMode: 'grounded', showCaptions: true, showKorean: true });
  await page.reload();
  expect(await pressed(page, 'doc-off')).toBe('true');
  expect(await pressed(page, 'toggle-captions')).toBe('true');
  expect(await pressed(page, 'toggle-korean')).toBe('true');
  await expect(page.locator('#caption-block')).toBeVisible();
  await expect(page.locator('#answer-meaning')).toBeVisible();
  await page.evaluate(() => localStorage.setItem('live-coach-settings',
    JSON.stringify({ deviceId: '', responseMode: 'balanced', topic: 'Azure sync', audioSource: 'display' })));
  await page.reload();
  expect(await pressed(page, 'doc-on')).toBe('true');
  expect(await pressed(page, 'toggle-captions')).toBe('false');
  expect(await pressed(page, 'toggle-korean')).toBe('false');
  await expect(page.locator('#caption-block')).toBeHidden();
  await expect(page.locator('#answer-meaning')).toBeHidden();
  await expect(page.locator('#topic')).toHaveValue('Azure sync');
  await expect(page.locator('#response-mode')).toHaveValue('balanced');
  await expect(page.locator('#audio-source')).toHaveValue('display');
});

test('the document search choice in the settings dialog and the quick buttons are one setting', async ({ page }) => {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.goto('/live.html');
  await menuAction(page, 'settings');
  await expect(page.locator('#response-mode')).toHaveValue('grounded');
  await page.locator('#response-mode').selectOption('conversation');
  expect(await pressed(page, 'doc-off')).toBe('true');
  await page.locator('#response-mode').selectOption('balanced');
  expect(await pressed(page, 'doc-on')).toBe('true');
  await page.getByRole('button', { name: '닫기', exact: true }).click();
  await page.locator('#doc-off').click();
  await menuAction(page, 'settings');
  await expect(page.locator('#response-mode')).toHaveValue('conversation');
  await page.getByRole('button', { name: '닫기', exact: true }).click();
  await page.locator('#doc-on').click();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings')).responseMode)).toBe('balanced');
});

async function assertLayout(page) {
  const geometry = await page.evaluate(() => {
    const panel = document.querySelector('#live-panel').getBoundingClientRect();
    const answers = document.querySelector('.live-answers');
    return {
      pageOverflow: document.documentElement.scrollWidth > innerWidth + 1 ||
        document.documentElement.scrollHeight > innerHeight + 1,
      panel: { width: panel.width, height: panel.height, bottom: panel.bottom },
      answersHeight: answers.clientHeight,
      overflow: [...document.querySelectorAll('.live-history, .live-answers, .live-footer, .live-status')]
        .filter(el => el.scrollWidth > el.clientWidth + 1).map(el => el.className),
    };
  });
  expect(geometry.pageOverflow).toBe(false);
  expect(geometry.overflow).toEqual([]);
  expect(geometry.answersHeight).toBeGreaterThan(60);
  await page.locator('#answer-korean').scrollIntoViewIfNeeded();
  await expect(page.locator('#answer-korean')).toBeInViewport();
  await page.locator('#alternative-korean').scrollIntoViewIfNeeded();
  await expect(page.locator('#alternative-korean')).toBeInViewport();
  for (const id of ['doc-on', 'doc-off', 'toggle-captions', 'toggle-korean']) {
    await page.locator(`#${id}`).scrollIntoViewIfNeeded();
    await expect(page.locator(`#${id}`)).toBeInViewport();
  }
}

for (const native of [false, true]) {
  for (const [width, height] of [[550, 860], [360, 400], [1200, 900]]) {
    for (const scale of [1, 1.25, 1.5, 2]) {
      test(`${native ? 'native shell' : 'web'} ${width}x${height}, ${scale * 100}% text`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height });
        await fixture(page, native);
        // Text-only scaling: keep the viewport fixed and enlarge actual computed text and line heights.
        await page.evaluate(scale => {
          const sizes = [...document.querySelectorAll('body, body *')].map(el => {
            const style = getComputedStyle(el);
            return [el, parseFloat(style.fontSize), style.lineHeight];
          });
          for (const [el, size, line] of sizes) {
            el.style.fontSize = `${size * scale}px`;
            if (line !== 'normal') el.style.lineHeight = `${parseFloat(line) * scale}px`;
          }
        }, scale);
        await assertLayout(page);
        if (scale === 1) await page.screenshot({ path: testInfo.outputPath('long-content.png') });
        await page.evaluate(() => {
          const error = document.querySelector('#session-error');
          error.hidden = false; error.textContent = 'Synthetic connection error. Retry from the menu. '.repeat(8);
          document.querySelector('#answer-text').append(' Streaming partial response…');
          document.querySelector('#answer-korean').textContent = '한국어 번역 중…';
        });
        await assertLayout(page);
        if (scale === 1) await page.screenshot({ path: testInfo.outputPath('streaming-error.png') });
        await page.locator('#live-panel').focus();
        await page.locator('#live-panel').press('Shift+F10');
        const menu = page.locator('#live-menu');
        await expect(menu).toBeVisible();
        const bounds = await menu.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1);
        expect(bounds.y + bounds.height).toBeLessThanOrEqual(height + 1);
        expect(await menu.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        if (native) {
          await menu.locator('[data-shell="opacity"]').click();
          expect(await page.evaluate(() => window.__shellMessages)).toEqual(['opacity']);
          await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 4, clientY: 4 });
        }
        await page.locator('[data-action="settings"]').click();
        const dialog = page.locator('#settings');
        await expect(dialog).toBeVisible();
        expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.locator('#audio-source').selectOption('display');
        await page.getByRole('button', { name: '닫기', exact: true }).scrollIntoViewIfNeeded();
        await expect(page.getByRole('button', { name: '닫기', exact: true })).toBeInViewport();
        if (scale === 1) await page.screenshot({ path: testInfo.outputPath('settings.png') });
        await page.getByRole('button', { name: '닫기', exact: true }).click();
        await expect(dialog).toBeHidden();
        expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings')).audioSource)).toBe('display');
      });
    }
  }
}

test('native resize and restoration contract stays transparent and resizable', () => {
  const xaml = hostFile('MainWindow.xaml');
  expect(xaml).toContain('AllowsTransparency="True"');
  expect(xaml).toContain('Topmost="True"');
  expect(xaml).toContain('ResizeMode="CanResizeWithGrip"');
  expect(xaml).toContain('MinWidth="360" MinHeight="400"');
  expect(xaml).toContain('ResizeBorderThickness="6"');
  expect(xaml).toContain('CaptionHeight="0"');
  expect(hostFile('MainWindow.xaml.cs')).not.toContain('and <= 1200');
  expect(shell).toContain('post("drag")');
  expect(shell).toContain('add("창 최소화", "minimize")');
  expect(shell).toContain('add("프로그램 종료", "exit")');
});

test('open native menu remains reachable while the window shrinks and expands', async ({ page }) => {
  await page.setViewportSize({ width: 1200, height: 900 });
  await fixture(page, true);
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 1190, clientY: 890 });
  for (const [width, height] of [[550, 860], [360, 400], [1200, 900]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(async () => {
      const box = await page.locator('#live-menu').boundingBox();
      return box.x + box.width <= width + 1 && box.y + box.height <= height + 1;
    }).toBe(true);
  }
  await page.locator('[data-shell="minimize"]').click();
  expect(await page.evaluate(() => window.__shellMessages)).toEqual(['minimize']);
});
