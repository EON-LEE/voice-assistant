import { openSettings, closeSheet, setConnectionMode, startMeeting, stopMeeting, setPause, confirmConsent } from '../overlay-helpers.js';
import { test, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] } });
function silentWav() {
  const bytes = Buffer.alloc(44 + 3200);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(16000, 24); bytes.writeUInt32LE(32000, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34);
  bytes.write('data', 36); bytes.writeUInt32LE(3200, 40); return bytes;
}
async function coachApi(page) {
  const calls = [];
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.route('**/api/assist/enrich', route => {
    const body = route.request().postDataJSON(); calls.push({ path: 'enrich', ...body });
    const words = body.text.trim().split(/\s+/), pronunciation = [];
    for (let i = 0; i < words.length; i += 3) pronunciation.push({ en: words.slice(i, i + 3).join(' '), ko: '가나다' });
    return route.fulfill({ json: { korean: body.kind === 'question' ? '다음 계획은 무엇인가요?' : '계획을 확인하고 알려 드리겠습니다.', pronunciation: body.kind === 'question' ? null : pronunciation } });
  });
  await page.route('**/api/assist/speak', route => {
    calls.push({ path: 'speak', ...route.request().postDataJSON() });
    return route.fulfill({ body: silentWav(), contentType: 'audio/wav' });
  });
  return calls;
}
async function socket(page) {
  let connected, frames = 0; const commands = [];
  await page.routeWebSocket('**/api/meeting*', ws => {
    connected = ws;
    ws.onMessage(message => {
      if (typeof message !== 'string') { frames++; return; }
      const body = JSON.parse(message); commands.push(body);
      if (body.type === 'session.start') ws.send(JSON.stringify({ type: 'session.ready' }));
    });
  });
  return { commands, frames: () => frames, send: event => connected.send(JSON.stringify(event)) };
}
async function meeting(page) {
  await page.goto('/'); await setConnectionMode(page, 'synthetic');
  await startMeeting(page); await expect(page.getByTestId('pause')).toBeEnabled();
}
async function shot(page, name) {
  if (!process.env.VOICE_ASSISTANT_COACH_SCREENSHOTS) return;
  await mkdir(process.env.VOICE_ASSISTANT_COACH_SCREENSHOTS, { recursive: true });
  await page.screenshot({ path: join(process.env.VOICE_ASSISTANT_COACH_SCREENSHOTS, name), fullPage: true });
}

test('meeting English streams before assist completes, aligned Hangul and click-only shadowing', async ({ page }) => {
  const calls = await coachApi(page), ws = await socket(page);
  await meeting(page);
  ws.send({ type: 'transcript.final', turnId: 't', revision: 1, text: 'What is the next step?' });
  ws.send({ type: 'response.started', turnId: 't', responseId: 'r' });
  ws.send({ type: 'response.delta', turnId: 't', responseId: 'r', text: 'Let me ' });
  await expect(page.getByTestId('reply')).toHaveText('Let me ');
  expect(calls.filter(call => call.kind === 'reply')).toHaveLength(0);
  ws.send({ type: 'response.delta', turnId: 't', responseId: 'r', text: 'check the plan.' });
  ws.send({ type: 'response.completed', turnId: 't', responseId: 'r', text: 'Let me check the plan.', sources: [], grounding: 'disabled' });
  await expect(page.getByTestId('reply')).toHaveText('Let me check the plan.');
  await expect(page.locator('#question-ko')).toHaveText('다음 계획은 무엇인가요?');
  await expect(page.locator('#reply-ko')).toHaveText('계획을 확인하고 알려 드리겠습니다.');
  expect(await page.locator('#reply-pronunciation .pronunciation-en').allTextContents()).toEqual(['Let me check', 'the plan.']);
  await expect(page.locator('#pronunciation-note')).toBeVisible();
  expect(calls.filter(call => call.path === 'speak')).toHaveLength(0);
  await page.locator('#speech-rate').selectOption('slow'); await page.locator('#reply-listen').click();
  await expect.poll(() => calls.filter(call => call.path === 'speak').length).toBe(1);
  expect(calls.find(call => call.path === 'speak')).toMatchObject({ text: 'Let me check the plan.', voice: 'coach', rate: 'slow' });
  await expect(page.getByTestId('pause')).toBeChecked();
  await expect(page.getByTestId('reply')).toHaveText('Let me check the plan.');
  await page.getByTestId('pin').click();
  await page.setViewportSize({ width: 1100, height: 900 }); await shot(page, 'coach-meeting-desktop.png');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await shot(page, 'coach-meeting-mobile.png');
  await stopMeeting(page);
  await expect(page.locator('#question-ko')).toBeHidden();
});

test('stale enrichment cannot replace a newer turn and failures leave English visible', async ({ page }) => {
  await coachApi(page); const ws = await socket(page);
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  await page.route('**/api/assist/enrich', async route => {
    const body = route.request().postDataJSON();
    if (body.text === 'Old question?') {
      await pending; await route.fulfill({ json: { korean: '이전 질문입니다.', pronunciation: null } }).catch(() => {});
    } else await route.fulfill({ status: 502, json: { error: 'provider_unavailable' } });
  });
  await meeting(page);
  ws.send({ type: 'transcript.final', turnId: 'old', revision: 1, text: 'Old question?' });
  await page.waitForRequest('**/api/assist/enrich');
  ws.send({ type: 'transcript.final', turnId: 'new', revision: 1, text: 'New question?' }); release();
  ws.send({ type: 'response.started', turnId: 'new', responseId: 'r2' });
  ws.send({ type: 'response.completed', turnId: 'new', responseId: 'r2', text: 'I will check.', sources: [] });
  await expect(page.getByTestId('reply')).toHaveText('I will check.');
  await expect(page.locator('#coach-question')).toHaveText('New question?');
  await expect(page.locator('#question-ko')).toBeHidden();
  await expect(page.locator('#reply-ko')).toBeHidden();
  await expect(page.getByTestId('error')).toBeHidden();
  await stopMeeting(page);
});

async function practiceApi(page) {
  const calls = await coachApi(page);
  await page.route('**/api/practice/turn', route => {
    const body = route.request().postDataJSON(); calls.push({ path: 'turn', ...body });
    const count = body.history.filter(entry => entry.role === 'partner').length;
    return route.fulfill({ json: { text: count >= body.maxTurns ? 'Thanks for practicing.' : 'What is your next step?',
      done: count >= body.maxTurns, turn: count + 1, grounding: 'disabled', sources: [] } });
  });
  await page.route('**/api/practice/suggest', route => {
    calls.push({ path: 'suggest', ...route.request().postDataJSON() });
    return route.fulfill({ json: { text: 'Let me check the plan.', grounding: 'disabled', sources: [] } });
  });
  await page.route('**/api/practice/feedback', route => {
    calls.push({ path: 'feedback', ...route.request().postDataJSON() });
    return route.fulfill({ json: { correctedEnglish: 'We will check the plan.', easierEnglish: 'We will check it.',
      feedbackKo: '핵심을 잘 말했어요. 더 짧게 말해 보세요.', points: [{ tag: 'clarity', ko: '한 문장으로 말해 보세요.' }], clarity: 4 } });
  });
  await page.route('**/api/practice/summary', route => {
    calls.push({ path: 'summary', ...route.request().postDataJSON() });
    return route.fulfill({ json: { headlineKo: '한 질문을 끝까지 연습했어요.', strengthsKo: ['핵심을 잘 전달했어요.'], improveKo: ['짧게 말해 보세요.'],
      phrases: [{ en: 'Let me check the plan.', ko: '계획을 확인하겠습니다.' }] } });
  });
  return calls;
}
async function startPractice(page) {
  await page.goto('/'); await page.locator('#practice-chip').click();
  await expect(page.locator('#practice-auth-status')).toContainText('LOCAL FAKE');
  await page.locator('#practice-count').fill('1');
  await page.locator('#practice-consent').check();
  await page.locator('#practice-start').click();
  await expect(page.locator('#practice-question')).toHaveText('What is your next step?');
}
test('full practice round uses recognition-only mic, requested hint, feedback/listen and summary', async ({ page }) => {
  const calls = await practiceApi(page), ws = await socket(page);
  await page.addInitScript(() => {
    window.__practiceTracks = [];
    const get = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia = async options => { const stream = await get(options); window.__practiceTracks.push(...stream.getTracks()); return stream; };
  });
  await startPractice(page);
  expect(ws.commands[0].options.transcribeOnly).toBe(true);
  await expect.poll(() => calls.filter(call => call.path === 'speak' && call.voice === 'partner').length).toBe(1);
  expect(calls.filter(call => call.path === 'suggest')).toHaveLength(0);
  await page.locator('#practice-hint').click();
  await expect(page.locator('#practice-hint-text')).toHaveText('Let me check the plan.');
  await expect(page.locator('#practice-pronunciation .pronunciation-chunk')).toHaveCount(2);
  expect(ws.frames()).toBe(0);
  await page.setViewportSize({ width: 1100, height: 900 }); await shot(page, 'coach-practice-desktop.png');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await shot(page, 'coach-practice-mobile.png');
  await page.locator('#practice-answer').click();
  ws.send({ type: 'transcript.partial', turnId: 'answer1', revision: 1, text: 'We will' });
  await expect(page.locator('#practice-done')).toBeDisabled();
  ws.send({ type: 'transcript.final', turnId: 'answer1', revision: 2, text: 'We will' });
  ws.send({ type: 'transcript.final', turnId: 'answer2', revision: 1, text: 'check the plan.' });
  await expect(page.locator('#practice-answer-text')).toContainText('We will check the plan.');
  await page.locator('#practice-done').click();
  await expect(page.locator('#practice-feedback')).toContainText('핵심을 잘 말했어요.');
  expect(calls.find(call => call.path === 'feedback').answer).toBe('We will check the plan.');
  await page.locator('[data-listen="easier"]').click();
  await expect.poll(() => calls.filter(call => call.path === 'speak' && call.text === 'We will check it.').length).toBe(1);
  await page.locator('#practice-next').click();
  await expect(page.locator('#practice-summary')).toContainText('한 질문을 끝까지 연습했어요.');
  expect(calls.filter(call => call.path === 'turn').at(-1).history).toEqual([
    { role: 'partner', text: 'What is your next step?' }, { role: 'user', text: 'We will check the plan.' },
  ]);
  expect(calls.find(call => call.path === 'summary').turns).toHaveLength(1);
  await expect.poll(() => page.evaluate(() => window.__practiceTracks.every(track => track.readyState === 'ended'))).toBe(true);
  await page.locator('[data-listen="phrase-0"]').click();
  await page.locator('#practice-stop').click();
  await expect(page.locator('#practice-summary')).toBeHidden();
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
  expect(ws.commands.some(command => command.type === 'response.request')).toBe(false);
});

test('practice429 retry and skip are explicit; Stop ignores late requests and releases mic', async ({ page }) => {
  await practiceApi(page); const ws = await socket(page); let attempts = 0, answer;
  await page.route('**/api/practice/feedback', route => {
    answer = route.request().postDataJSON().answer;
    if (++attempts === 1) return route.fulfill({ status: 429, headers: { 'Retry-After': '0' }, json: { error: 'busy' } });
    return route.fulfill({ json: { correctedEnglish: 'I can check.', easierEnglish: 'Let me check.', feedbackKo: '다음에는 답해 보세요.', points: [], clarity: 2 } });
  });
  await startPractice(page); await page.locator('#practice-skip').click();
  await expect(page.locator('#practice-error')).toContainText('busy');
  await page.locator('#practice-retry').click();
  await expect(page.locator('#practice-feedback')).toContainText('다음에는 답해 보세요.');
  expect(answer).toBe('');
  await page.locator('#practice-stop').click();
  expect(ws.commands.some(command => command.type === 'session.stop')).toBe(true);
  await expect(page.locator('#practice-start')).toBeDisabled();
  await expect(page.locator('#practice-consent')).not.toBeChecked();
});

test('invalid pronunciation stays omitted while speech failure exposes retry without autoplay', async ({ page }) => {
  const calls = await coachApi(page), ws = await socket(page);
  await page.route('**/api/assist/enrich', route => route.fulfill({ json: { korean: '확인해요.', pronunciation: [{ en: 'Wrong words', ko: '잘못' }] } }));
  await page.route('**/api/assist/speak', route => {
    calls.push({ path: 'speak' }); return route.fulfill({ status: 502, json: { error: 'provider_unavailable' } });
  });
  await meeting(page);
  ws.send({ type: 'transcript.final', turnId: 't', revision: 1, text: 'What now?' });
  ws.send({ type: 'response.started', turnId: 't', responseId: 'r' });
  ws.send({ type: 'response.completed', turnId: 't', responseId: 'r', text: 'Let me check.', sources: [] });
  await expect(page.getByTestId('reply')).toHaveText('Let me check.');
  await expect(page.locator('#reply-pronunciation')).toBeHidden();
  expect(calls.filter(call => call.path === 'speak')).toHaveLength(0);
  await page.locator('#reply-listen').click();
  await expect(page.locator('#audio-status')).toContainText('unavailable');
  await page.locator('#reply-listen').click();
  await expect.poll(() => calls.filter(call => call.path === 'speak').length).toBe(2);
  await stopMeeting(page);
});

test('real localhost Fake practice and enrich/speak complete a recognition-only round', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_PRACTICE_E2E !== '1', 'Requires integrated practice/assist Fake API; never Azure.');
  await page.addInitScript(() => {
    window.__clipPlays = 0;
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function() { if (this.src.startsWith('blob:')) window.__clipPlays++; return play.call(this); };
  });
  await page.goto('/'); await page.locator('#practice-chip').click();
  await expect(page.locator('#practice-auth-status')).toContainText('LOCAL FAKE');
  await page.locator('#practice-count').fill('1'); await page.locator('#practice-materials').uncheck();
  const voiceResponse = page.waitForResponse(response => response.url().endsWith('/api/assist/speak'));
  await page.locator('#practice-consent').check(); await page.locator('#practice-start').click();
  await expect(page.locator('#practice-question')).not.toBeEmpty();
  await expect(page.locator('#practice-question-ko')).toBeVisible();
  const voice = await voiceResponse;
  expect(voice.status()).toBe(200); expect(voice.headers()['content-type']).toContain('audio/wav');
  await expect.poll(() => page.evaluate(() => window.__clipPlays)).toBeGreaterThan(0);
  await page.locator('#practice-hint').click();
  await expect(page.locator('#practice-hint-text')).not.toBeEmpty();
  await expect(page.locator('#practice-pronunciation')).toBeVisible();
  await page.locator('#practice-answer').click();
  await expect(page.locator('#practice-done')).toBeEnabled({ timeout: 15000 });
  await page.locator('#practice-done').click();
  await expect(page.locator('#practice-feedback')).toContainText('Clarity:');
  await page.locator('#practice-next').click();
  await expect(page.locator('#practice-summary')).toBeVisible();
  await page.locator('#practice-stop').click();
});
