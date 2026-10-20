import { test, expect } from '@playwright/test';

test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] } });

/**
 * live.html with a mocked share picker (counts every getDisplayMedia call, i.e. every picker) and a mocked meeting server
 * that records each WebSocket connection. Nothing leaves the machine.
 */
async function liveFixture(page, { source = 'display', audio = true, app = false, debug = false, korean = false } = {}) {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  const translations = [];
  await page.route('**/api/assist/enrich', route => {
    const { kind, text } = route.request().postDataJSON();
    translations.push({ kind, text });
    return route.fulfill({ json: { korean: `뜻: ${text}`, pronunciation: null } });
  });
  await page.addInitScript(({ source, audio, app, debug, korean }) => {
    localStorage.setItem('live-coach-settings', JSON.stringify({ audioSource: source, showKorean: korean }));
    if (debug) localStorage.setItem('live-coach-debug', '1');
    if (app) window.chrome = { webview: { postMessage() {} } };
    window.__display = { calls: 0, tracks: [] };
    navigator.mediaDevices.getDisplayMedia = async () => {
      window.__display.calls++;
      const tracks = [document.createElement('canvas').captureStream(1).getVideoTracks()[0]];
      if (audio) {
        const context = new AudioContext(), tone = context.createOscillator(), output = context.createMediaStreamDestination();
        tone.connect(output); tone.start();
        tracks.push(output.stream.getAudioTracks()[0]);
      }
      window.__display.tracks.push(...tracks);
      return new MediaStream(tracks);
    };
  }, { source, audio, app, debug, korean });
  const connections = [];
  await page.routeWebSocket('**/api/meeting*', ws => {
    const connection = { ws, commands: [], frames: 0 };
    connections.push(connection);
    ws.onMessage(message => {
      if (typeof message !== 'string') { connection.frames++; return; }
      const command = JSON.parse(message);
      connection.commands.push(command);
      if (command.type === 'session.start') ws.send(JSON.stringify({ type: 'session.ready', protocolVersion: 1, sessionUpdate: true }));
    });
  });
  await page.goto('/live.html');
  await expect(page.locator('html')).toHaveAttribute('data-live-ready', '1');
  return {
    connections, translations,
    send: event => connections.at(-1).ws.send(JSON.stringify(event)),
    pickerCalls: () => page.evaluate(() => window.__display.calls),
    start: async () => {
      await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
      await page.locator('[data-action="start"]').click();
    },
  };
}
function reply(server, id, question, text, extra = {}) {
  server.send({ type: 'transcript.final', turnId: `t${id}`, revision: 1, text: question });
  server.send({ type: 'response.started', turnId: `t${id}`, responseId: `r${id}` });
  server.send({ type: 'response.completed', turnId: `t${id}`, responseId: `r${id}`, text, suggestions: [text], sources: [],
    grounding: 'disabled', ...extra });
}

test('display mode: three dropped connections reconnect the server only, with no new share picker', async ({ page }) => {
  const server = await liveFixture(page);
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  await expect(page.locator('#live-level')).toBeVisible();
  reply(server, 1, 'What comes first?', 'We should check the plan.');
  await expect(page.locator('#answer-text')).toHaveText('We should check the plan.');
  for (let drop = 1; drop <= 3; drop++) {
    await server.connections.at(-1).ws.close({ code: 1011, reason: 'Synthetic network drop' });
    await expect.poll(() => server.connections.length).toBe(drop + 1);
    await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  }
  expect(await server.pickerCalls()).toBe(1);
  expect(await page.evaluate(() => window.__display.tracks.every(track => track.readyState === 'live'))).toBe(true);
  await expect(page.locator('#answer-text')).toHaveText('We should check the plan.');
  await expect(page.locator('#session-error')).toBeHidden();
  await expect.poll(() => server.connections.at(-1).frames).toBeGreaterThan(0);
});

test('"Stop sharing" ends capture with an inline 다시 공유 button; only that click opens a new picker', async ({ page }) => {
  const server = await liveFixture(page);
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  reply(server, 1, 'Who owns it?', 'Alex owns it.');
  await expect(page.locator('#answer-text')).toHaveText('Alex owns it.');
  await page.evaluate(() => window.__display.tracks.find(track => track.kind === 'audio').dispatchEvent(new Event('ended')));
  await expect(page.locator('#live-action')).toBeVisible();
  await expect(page.locator('#live-action')).toHaveText('다시 공유');
  await expect(page.locator('#session-error')).toContainText('화면 공유가 중지되었습니다');
  await expect(page.locator('#session-error')).not.toContainText('세션 종료');
  await expect(page.locator('#capture-status')).toContainText('꺼짐');
  await expect.poll(() => server.connections.at(-1).commands.at(-1)?.type).toBe('session.stop');
  expect(await server.pickerCalls()).toBe(1);
  expect(server.connections).toHaveLength(1);
  await page.locator('#live-action').click();
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  expect(await server.pickerCalls()).toBe(2);
  expect(server.connections).toHaveLength(2);
  await expect(page.locator('#live-action')).toBeHidden();
  await expect(page.locator('#answer-text')).toHaveText('Alex owns it.', { message: 're-sharing continues the same conversation' });
});

for (const app of [true, false]) {
  test(`a share without audio explains the ${app ? 'LiveCoach.exe' : 'browser'} steps and offers 다시 공유`, async ({ page }) => {
    const server = await liveFixture(page, { audio: false, app });
    await expect(page.locator('#live-action')).toBeHidden();
    await server.start();
    await expect(page.locator('#live-action')).toBeVisible();
    await expect(page.locator('#live-action')).toHaveText('다시 공유');
    const message = page.locator('#session-error');
    await expect(message).toContainText('공유한 화면에 소리가 없습니다');
    await expect(message).toContainText('창(Window) 공유에는 소리가 포함되지 않습니다');
    if (app) {
      await expect(message).toContainText("'전체 화면'을 선택하고 '시스템 오디오 공유'를 체크하세요");
      await expect(message).not.toContainText('탭 오디오');
    } else await expect(message).toContainText("Teams 웹 탭을 고르고 '탭 오디오도 공유'");
    await expect(message).not.toContainText('세션 종료');
    expect(server.connections).toHaveLength(0);
    expect(await page.evaluate(() => window.__display.tracks.every(track => track.readyState === 'ended'))).toBe(true);
  });
}

test('session_time_limit rolls over to a new session with the same share and no stop message', async ({ page }) => {
  const server = await liveFixture(page);
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  reply(server, 1, 'Next step?', 'Review the plan.');
  await expect(page.locator('#answer-text')).toHaveText('Review the plan.');
  server.send({ type: 'error', code: 'session_time_limit', message: 'Session reached its time limit. Start a new session to continue.', retryable: false });
  // The server closes after the notice; by then the page has already moved to the next session.
  await server.connections[0].ws.close({ code: 1000, reason: 'Session ended' }).catch(() => {});
  await expect.poll(() => server.connections.length).toBe(2);
  // "듣는 중" also appears in the rollover status ("새 세션으로 이어서 듣는 중"); only data-capture="on" means ready again.
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  await expect(page.locator('#capture-status')).toHaveText('듣는 중 · 영어 · 이전 대화 맥락 연결');
  await expect(page.locator('#session-error')).toBeHidden();
  expect(await server.pickerCalls()).toBe(1);
  await expect.poll(() => server.connections[1].commands.find(command => command.type === 'session.start')).toBeTruthy();
  await expect(page.locator('#answer-text')).toHaveText('Review the plan.');
});

test('a search failure keeps the session and labels that reply 자료 확인 불가', async ({ page }) => {
  const server = await liveFixture(page, { source: 'microphone' });
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  server.send({ type: 'transcript.final', turnId: 't1', revision: 1, text: 'What does the contract say?' });
  server.send({ type: 'response.started', turnId: 't1', responseId: 'r1' });
  server.send({ type: 'error', code: 'grounding_unavailable', message: 'Grounding is unavailable.', retryable: true });
  server.send({ type: 'response.completed', turnId: 't1', responseId: 'r1', text: 'I will confirm that after the call.',
    sources: [], grounding: 'unavailable' });
  await expect(page.locator('#answer-text')).toHaveText('I will confirm that after the call.');
  await expect(page.locator('#grounding-badge')).toContainText('자료 확인 불가');
  await expect(page.locator('#session-error')).toBeHidden();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  expect(server.connections).toHaveLength(1);
});

test('the finished primary line shows before the reply completes, and completion fills in the rest', async ({ page }) => {
  const server = await liveFixture(page, { source: 'microphone', debug: true });
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  server.send({ type: 'transcript.final', turnId: 't1', revision: 1, text: 'When can we ship?' });
  server.send({ type: 'response.started', turnId: 't1', responseId: 'r1' });
  server.send({ type: 'response.delta', turnId: 't1', responseId: 'r1', text: 'We can ship ' });
  await expect(page.locator('#reading-status')).toContainText('답변 준비 중');
  await expect(page.locator('#answer-text')).not.toContainText('We can ship');
  server.send({ type: 'response.delta', turnId: 't1', responseId: 'r1', text: 'on Friday.', primaryDone: true });
  await expect(page.locator('#answer-text')).toHaveText('We can ship on Friday.');
  await expect(page.locator('#reading-status')).toContainText('대안과 근거를 확인하는 중');
  await expect(page.locator('#alternative-card')).toBeHidden();
  await expect(page.locator('#hold-answer')).toBeEnabled();
  server.send({ type: 'response.completed', turnId: 't1', responseId: 'r1', text: 'We can ship on Friday.',
    suggestions: ['We can ship on Friday.', 'Friday looks realistic for the release.'], sources: [], grounding: 'disabled' });
  await page.locator('#live-details summary').click();
  await expect(page.locator('#alternative-text')).toHaveText('Friday looks realistic for the release.');
  await expect(page.locator('#grounding-badge')).toContainText('문서 검색 미사용');
  const latency = await page.evaluate(() => window.__liveCoachDebug.latency);
  expect(latency).toHaveLength(1);
  expect(latency[0]).toMatchObject({ turnId: 't1', responseId: 'r1', early: true });
  expect(latency[0].finalToShownMs).toBeGreaterThanOrEqual(0);
  expect(latency[0].finalToCompletedMs).toBeGreaterThanOrEqual(latency[0].finalToShownMs);
});

test('an empty primaryDone delta also shows the line, and a held answer stays while the new one waits', async ({ page }) => {
  const server = await liveFixture(page, { source: 'microphone' });
  await server.start();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  reply(server, 1, 'First?', 'First answer.');
  await expect(page.locator('#answer-text')).toHaveText('First answer.');
  await page.locator('#hold-answer').click();
  server.send({ type: 'transcript.final', turnId: 't2', revision: 1, text: 'Second?' });
  server.send({ type: 'response.started', turnId: 't2', responseId: 'r2' });
  server.send({ type: 'response.delta', turnId: 't2', responseId: 'r2', text: 'Second answer.' });
  server.send({ type: 'response.delta', turnId: 't2', responseId: 'r2', text: '', primaryDone: true });
  await expect(page.locator('#reading-status')).toContainText('새 답변 도착');
  await expect(page.locator('#answer-text')).toHaveText('First answer.');
  await page.locator('#hold-answer').click();
  await expect(page.locator('#answer-text')).toHaveText('Second answer.');
});

test('an interrupted primary line keeps a pushed Korean meaning, or says it has none and can be translated', async ({ page }) => {
  const server = await liveFixture(page, { source: 'microphone', korean: true });
  await server.start();
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  server.send({ type: 'transcript.final', turnId: 't1', revision: 1, text: 'When can we ship?' });
  server.send({ type: 'response.started', turnId: 't1', responseId: 'r1' });
  server.send({ type: 'response.delta', turnId: 't1', responseId: 'r1', text: 'We can ship on Friday.', primaryDone: true });
  server.send({ type: 'response.korean', turnId: 't1', responseId: 'r1', index: 0, korean: '금요일에 출시할 수 있습니다.' });
  await expect(page.locator('#answer-korean')).toHaveText('금요일에 출시할 수 있습니다.', { message: 'a pushed line shows before completion' });
  server.send({ type: 'response.cancelled', turnId: 't1', responseId: 'r1' });
  await expect(page.locator('#reading-status')).toContainText('중단');
  await expect(page.locator('#answer-korean')).toHaveText('금요일에 출시할 수 있습니다.');
  server.send({ type: 'transcript.final', turnId: 't2', revision: 1, text: 'Who checks it?' });
  server.send({ type: 'response.started', turnId: 't2', responseId: 'r2' });
  server.send({ type: 'response.delta', turnId: 't2', responseId: 'r2', text: 'I will check it.', primaryDone: true });
  await expect(page.locator('#answer-text')).toHaveText('I will check it.');
  await expect(page.locator('#answer-korean')).toHaveText('한국어 번역 준비 중…');
  server.send({ type: 'response.cancelled', turnId: 't2', responseId: 'r2' });
  await expect(page.locator('#answer-korean')).toContainText('답변이 중단되어 한국어 뜻이 없습니다');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await expect(page.locator('[data-action="translate"]')).toBeEnabled();
  await page.locator('[data-action="translate"]').click();
  await expect(page.locator('#answer-korean')).toHaveText('뜻: I will check it.');
  expect(server.translations.filter(item => item.kind === 'reply')).toEqual([{ kind: 'reply', text: 'I will check it.' }]);
});
