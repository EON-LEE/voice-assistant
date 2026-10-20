import { test, expect } from '@playwright/test';

/**
 * live.html inside a simulated LiveCoach.exe: a fake `window.chrome.webview` bridge that answers host-audio requests
 * like the Windows host (one capture at a time, a status first, then 20 ms PCM frames as base64) and the
 * `window.__liveShell.hostAudio` announcement. The meeting server is mocked too; nothing leaves the machine.
 */
async function hostFixture(page, { settings, app = true, sources = ['teams', 'system'] } = {}) {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.addInitScript(({ settings, app, sources }) => {
    if (settings) localStorage.setItem('live-coach-settings', JSON.stringify(settings));
    window.__display = { calls: 0 };
    navigator.mediaDevices.getDisplayMedia = async () => {
      window.__display.calls++;
      throw new DOMException('No share picker in this test.', 'NotAllowedError');
    };
    if (!app) return;
    const listeners = new Set();
    const tone = new Int16Array(320);
    for (let i = 0; i < 320; i++) tone[i] = Math.round(9000 * Math.sin(2 * Math.PI * 440 * i / 16000));
    const base64 = samples => {
      let text = '';
      for (const byte of new Uint8Array(samples.buffer)) text += String.fromCharCode(byte);
      return btoa(text);
    };
    const pcm = { tone: base64(tone), silence: base64(new Int16Array(320)) };
    const deliver = message => { for (const listener of [...listeners]) listener({ data: JSON.stringify(message) }); };
    const host = window.__host = { posted: [], captures: [], current: undefined, sound: 'tone' };
    host.status = (state, extra = {}) => {
      const capture = host.current;
      if (capture) deliver({ type: 'host-audio.status', id: capture.id, source: capture.source, state,
        detail: extra.detail ?? `Synthetic ${state}`, ...(extra.code ? { code: extra.code } : {}) });
    };
    window.chrome = { webview: {
      postMessage(text) {
        const message = JSON.parse(text);
        host.posted.push(message);
        if (message.type === 'host-audio.start') {
          // Like LiveCoach.exe: a new start replaces the running capture.
          if (host.current) clearInterval(host.current.timer);
          const capture = { id: message.id, source: message.source, seq: 0 };
          host.captures.push(capture);
          host.current = capture;
          setTimeout(() => {
            if (host.current !== capture) return;
            host.status('capturing', { detail: `Synthetic ${capture.source} capture` });
            capture.timer = setInterval(() => deliver({ type: 'host-audio.frame', seq: capture.seq++, pcm: pcm[host.sound] }), 20);
          }, 10);
        }
        if (message.type === 'host-audio.stop' && host.current?.id === message.id) {
          clearInterval(host.current.timer);
          host.current = undefined;
        }
      },
      addEventListener(type, listener) { if (type === 'message') listeners.add(listener); },
      removeEventListener(type, listener) { listeners.delete(listener); },
    } };
    window.__liveShell = { setOpacity() {}, hostAudio: { version: 1, sources } };
  }, { settings, app, sources });
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
  const menu = async action => {
    await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
    await page.locator(`[data-action="${action}"]`).click();
  };
  return {
    connections, menu,
    start: () => menu('start'),
    posted: type => page.evaluate(type => window.__host.posted.filter(message => message.type === type), type),
    status: (state, extra) => page.evaluate(([state, extra]) => window.__host.status(state, extra), [state, extra]),
    pickerCalls: () => page.evaluate(() => window.__display.calls),
  };
}

test('LiveCoach offers Teams app and PC audio, prefers Teams over a stored share, and streams it with no picker', async ({ page }) => {
  const app = await hostFixture(page, { settings: { audioSource: 'display' } });
  await app.menu('settings');
  await expect(page.locator('#audio-source')).toHaveValue('teams');
  await expect(page.locator('#audio-source option[value="teams"]')).toHaveText('Teams 앱 소리 (LiveCoach · 공유 창 없음)');
  await expect(page.locator('#audio-source option[value="host-system"]')).toHaveText('PC 전체 소리 (LiveCoach · 기본 출력 장치)');
  for (const value of ['teams', 'host-system']) {
    await expect(page.locator(`#audio-source option[value="${value}"]`)).not.toHaveAttribute('hidden');
    await expect(page.locator(`#audio-source option[value="${value}"]`)).toHaveJSProperty('disabled', false);
  }
  await expect(page.locator('#teams-hint')).toBeVisible();
  await expect(page.locator('#teams-hint')).toContainText('Teams 데스크톱 앱의 소리만');
  await expect(page.locator('#display-hint')).toBeHidden();
  await page.locator('#audio-source').selectOption('host-system');
  await expect(page.locator('#host-system-hint')).toBeVisible();
  await page.locator('#audio-source').selectOption('teams');
  await page.locator('#settings button[value="close"]').click();

  await app.start();
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  await expect(page.locator('#capture-status')).toHaveText('듣는 중 · Teams 앱 소리 · 영어');
  expect(await app.posted('host-audio.start')).toEqual([expect.objectContaining({ type: 'host-audio.start', source: 'teams' })]);
  await expect.poll(() => app.connections[0].frames).toBeGreaterThan(10);
  await expect.poll(() => app.posted('host-audio.ack').then(acks => acks.length)).toBeGreaterThan(1);
  await expect(page.locator('#live-level')).toBeVisible();
  await expect.poll(() => page.locator('#live-level').evaluate(meter => meter.value)).toBeGreaterThan(0.1);
  await expect(page.locator('#session-error')).toBeHidden();

  await app.connections[0].ws.close({ code: 1011, reason: 'Synthetic network drop' });
  await expect.poll(() => app.connections.length).toBe(2);
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  await expect.poll(() => app.connections[1].frames).toBeGreaterThan(0);
  expect(await app.posted('host-audio.start'), 'a reconnect keeps the LiveCoach capture').toHaveLength(1);
  expect(await app.posted('host-audio.stop')).toHaveLength(0);
  expect(await app.pickerCalls()).toBe(0);

  await app.menu('stop');
  await expect(page.locator('#capture-status')).toHaveText('Teams 앱 소리 꺼짐 · 우클릭으로 설정 및 시작');
  expect(await app.posted('host-audio.stop')).toHaveLength(1);
});

test('Teams warnings: not running only waits; a silent Teams capture offers PC audio, and the switch keeps the connection', async ({ page }) => {
  const app = await hostFixture(page);
  await app.start();
  await expect(page.locator('#capture-status')).toHaveText('듣는 중 · Teams 앱 소리 · 영어');
  const message = page.locator('#session-error'), action = page.locator('#live-action');

  await app.status('teams-not-running', { detail: 'Waiting for Microsoft Teams (ms-teams.exe) to start.' });
  await expect(message).toHaveText('Teams가 실행 중이 아닙니다. Teams를 켜면 자동으로 연결됩니다.');
  await expect(action).toBeHidden();
  await expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');
  await app.status('capturing');
  await expect(message).toBeHidden();

  await app.status('error', { code: 'teams-silent', detail: 'Teams is playing sound, but the capture gets only silence.' });
  await expect(message).toContainText("Teams에서 소리가 감지되지만 캡처가 비어 있습니다. 'PC 전체 소리'로 바꾸세요.");
  await expect(action).toBeVisible();
  await expect(action).toHaveText('PC 전체 소리로 전환');
  const before = app.connections[0].frames;
  await action.click();

  await expect(page.locator('#capture-status')).toHaveText('듣는 중 · PC 전체 소리 · 영어');
  await expect(message).toBeHidden();
  await expect(action).toBeHidden();
  const starts = await app.posted('host-audio.start');
  expect(starts.map(start => start.source)).toEqual(['teams', 'system']);
  await expect.poll(() => app.posted('host-audio.stop').then(stops => stops.map(stop => stop.id))).toEqual([starts[0].id]);
  await expect.poll(() => app.connections[0].frames).toBeGreaterThan(before + 10);
  expect(app.connections, 'the switch keeps the server connection').toHaveLength(1);
  expect(app.connections[0].commands.map(command => command.type)).toEqual(['session.start']);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings')).audioSource)).toBe('host-system');

  await app.status('error', { detail: 'The audio device is in use (0x8889000A).' });
  await expect(message).toHaveText("PC 전체 소리를 가져오지 못해 자동으로 다시 시도하는 중입니다. 소리가 나오는 출력 장치를 확인하세요. (LiveCoach: The audio device is in use (0x8889000A).)");
  await expect(action, 'PC audio has nothing to switch to').toBeHidden();
});

test('In a browser the LiveCoach sources stay hidden and a stored one falls back to the share', async ({ page }) => {
  const app = await hostFixture(page, { app: false, settings: { audioSource: 'teams' } });
  await app.menu('settings');
  await expect(page.locator('#audio-source')).toHaveValue('display');
  for (const value of ['teams', 'host-system']) {
    await expect(page.locator(`#audio-source option[value="${value}"]`)).toHaveAttribute('hidden', '');
    await expect(page.locator(`#audio-source option[value="${value}"]`)).toHaveJSProperty('disabled', true);
  }
  await expect(page.locator('#display-hint')).toBeVisible();
  await expect(page.locator('#teams-hint')).toBeHidden();
});
