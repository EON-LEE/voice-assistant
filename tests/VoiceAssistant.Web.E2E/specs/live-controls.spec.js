import { test, expect } from '@playwright/test';

test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] } });

/**
 * live.html with a mocked meeting server and share picker; nothing leaves the machine. The picker records whether it
 * was opened inside a user gesture, which browsers require for it.
 */
async function liveFixture(page, settings = { audioSource: 'microphone' }) {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.addInitScript(settings => {
    if (localStorage.getItem('live-coach-settings') === null) localStorage.setItem('live-coach-settings', JSON.stringify(settings));
    window.__picker = [];
    navigator.mediaDevices.getDisplayMedia = async () => {
      window.__picker.push(navigator.userActivation.isActive);
      const context = new AudioContext(), tone = context.createOscillator(), output = context.createMediaStreamDestination();
      tone.connect(output); tone.start();
      return new MediaStream([document.createElement('canvas').captureStream(1).getVideoTracks()[0], output.stream.getAudioTracks()[0]]);
    };
  }, settings);
  const connections = [];
  await page.routeWebSocket('**/api/meeting*', ws => {
    const connection = { ws, commands: [] };
    connections.push(connection);
    ws.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message);
      connection.commands.push(command.type);
      if (command.type === 'session.start') ws.send(JSON.stringify({ type: 'session.ready', protocolVersion: 1, sessionUpdate: true }));
    });
  });
  await page.goto('/live.html');
  await expect(page.locator('html')).toHaveAttribute('data-live-ready', '1');
  return {
    connections,
    commands: () => connections.flatMap(connection => connection.commands),
    send: event => connections.at(-1).ws.send(JSON.stringify(event)),
  };
}
function reply(server, id, text) {
  server.send({ type: 'transcript.final', turnId: `t${id}`, revision: 1, text: `Question ${id}?` });
  server.send({ type: 'response.started', turnId: `t${id}`, responseId: `r${id}` });
  server.send({ type: 'response.completed', turnId: `t${id}`, responseId: `r${id}`, text, suggestions: [text, 'Could we check that together?'],
    sources: [], grounding: 'disabled' });
}
const live = page => expect(page.locator('#live-panel')).toHaveAttribute('data-capture', 'on');

test('the header start/stop and pause buttons run the session like the menu and follow the same state', async ({ page }) => {
  const server = await liveFixture(page);
  const toggle = page.locator('#live-toggle'), pause = page.locator('#live-pause'), status = page.locator('#capture-status');
  await expect(status).toHaveText('마이크 꺼짐 · 시작을 누르세요');
  await expect(page.locator('#reading-status')).toHaveText('시작을 누르면 듣기 시작 · 설정은 우클릭 메뉴');
  await expect(toggle).toHaveText('시작');
  await expect(toggle).toHaveAttribute('title', /^라이브 시작/);
  await expect(pause).toBeDisabled();

  await toggle.click();
  await live(page);
  await expect(toggle).toHaveText('종료');
  await expect(toggle).toHaveAttribute('title', '회의 종료 / 마이크 해제');
  await expect(pause).toBeEnabled();
  await expect(pause).toHaveText('일시정지');
  expect(server.commands()).toEqual(['session.start']);
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await expect(page.locator('[data-action="start"]')).toBeDisabled();
  await expect(page.locator('[data-action="stop"]')).toBeEnabled();
  await page.keyboard.press('Escape');

  await pause.click();
  await expect(pause).toHaveText('다시 듣기');
  await expect(status).toHaveText('마이크 일시정지 · 오디오 전송 중지');
  await expect.poll(server.commands).toContain('response.cancel');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await expect(page.locator('[data-action="pause"]')).toHaveText('다시 듣기');
  await page.keyboard.press('Escape');
  await pause.click();
  await expect(pause).toHaveText('일시정지');
  await expect(status).toHaveText('듣는 중 · 마이크 · 영어');

  await toggle.click();
  await expect.poll(server.commands).toContain('session.stop');
  await expect(status).toHaveText('마이크 꺼짐 · 시작을 누르세요');
  await expect(toggle).toHaveText('시작');
  await expect(pause).toBeDisabled();

  // The menu still starts and stops a session, and the header follows it.
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator('[data-action="start"]').click();
  await live(page);
  await expect(toggle).toHaveText('종료');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator('[data-action="stop"]').click();
  await expect(toggle).toHaveText('시작');
  expect(server.connections).toHaveLength(2);
});

test('the header start opens the share picker only inside a click or key press', async ({ page }) => {
  await liveFixture(page, { audioSource: 'display' });
  const toggle = page.locator('#live-toggle');
  expect(await page.evaluate(() => window.__picker)).toEqual([]);
  await toggle.click();
  await live(page);
  await toggle.click();
  await expect(toggle).toHaveText('시작');
  await toggle.press('Enter');
  await live(page);
  expect(await page.evaluate(() => window.__picker)).toEqual([true, true]);
});

test('A−/A+ resize the English answers only, are remembered, and long answers still wrap', async ({ page }) => {
  await page.setViewportSize({ width: 550, height: 860 });
  const server = await liveFixture(page);
  await page.locator('#live-toggle').click();
  await live(page);
  const english = 'I can confirm the next milestone after reviewing the dependencies with the team. '.repeat(6);
  reply(server, 1, english + 'LongUnbrokenSyntheticIdentifier'.repeat(8));
  await expect(page.locator('#answer-text')).toContainText('I can confirm');
  await page.locator('#live-details').evaluate(details => { details.open = true; });
  const sizes = () => page.evaluate(() => {
    const size = selector => parseFloat(getComputedStyle(document.querySelector(selector)).fontSize);
    return { answer: size('#answer-text'), alternative: size('#alternative-text'), meaning: size('#answer-korean'),
      scale: document.querySelector('#live-panel').style.getPropertyValue('--answer-scale') };
  });
  // The default sets no scale, so the answers keep exactly the sizes the stylesheet had before the setting existed.
  const original = await page.evaluate(() => {
    const probe = document.createElement('p');
    probe.style.fontSize = 'clamp(22px, 4.8vw, 28px)';
    document.body.append(probe);
    const size = getComputedStyle(probe).fontSize;
    probe.remove();
    return size;
  });
  expect(await page.locator('#answer-text').evaluate(el => getComputedStyle(el).fontSize)).toBe(original);
  expect(await page.locator('#alternative-text').evaluate(el => getComputedStyle(el).fontSize)).toBe('19px');
  const base = await sizes();
  expect(base.scale).toBe('');
  await expect(page.locator('#answer-smaller')).toBeEnabled();

  await page.locator('#answer-larger').click();
  let now = await sizes();
  expect(now.scale).toBe('1.15');
  expect(now.answer).toBeCloseTo(base.answer * 1.15, 1);
  expect(now.alternative).toBeCloseTo(19 * 1.15, 1);
  expect(now.meaning).toBe(base.meaning);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings')).answerSize)).toBe(115);

  // By keyboard up to the largest size: the A+ button turns off and focus moves to A−.
  await page.locator('#answer-larger').focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Enter');
  await expect(page.locator('#answer-larger')).toBeDisabled();
  await expect(page.locator('#answer-smaller')).toBeFocused();
  now = await sizes();
  expect(now.scale).toBe('1.5');
  expect(now.answer).toBeCloseTo(base.answer * 1.5, 1);

  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-live-ready', '1');
  expect((await sizes()).scale).toBe('1.5');
  await expect(page.locator('#answer-larger')).toBeDisabled();
  await page.locator('#live-toggle').click();
  await live(page);
  reply(server, 2, english + 'LongUnbrokenSyntheticIdentifier'.repeat(8));
  await expect(page.locator('#answer-text')).toContainText('I can confirm');

  // The largest size in the narrowest window still wraps: nothing is cut off sideways, the answers scroll down instead.
  await page.setViewportSize({ width: 360, height: 400 });
  const layout = await page.evaluate(() => {
    const answer = document.querySelector('#answer-text'), card = document.querySelector('.live-card.primary').getBoundingClientRect();
    const box = answer.getBoundingClientRect(), line = parseFloat(getComputedStyle(answer).lineHeight);
    return {
      overflow: [answer, document.querySelector('.live-answers'), document.querySelector('.live-card.primary')]
        .filter(el => el.scrollWidth > el.clientWidth + 1).length,
      inside: box.left >= card.left - 1 && box.right <= card.right + 1,
      lines: Math.round(box.height / line),
      scrolls: document.querySelector('.live-answers').scrollHeight > document.querySelector('.live-answers').clientHeight,
    };
  });
  expect(layout).toMatchObject({ overflow: 0, inside: true, scrolls: true });
  expect(layout.lines).toBeGreaterThan(10);

  for (let step = 0; step < 4; step++) await page.locator('#answer-smaller').click();
  await expect(page.locator('#answer-smaller')).toBeDisabled();
  expect((await sizes()).scale).toBe('0.85');
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('live-coach-settings')).answerSize)).toBe(85);
});
