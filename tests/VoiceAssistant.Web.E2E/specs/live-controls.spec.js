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
const focusedItem = page => page.evaluate(() => document.activeElement?.dataset.action ?? document.activeElement?.id);
async function openMenuFrom(page, selector) {
  await page.locator(selector).focus();
  await page.keyboard.press('Shift+F10');
  await expect(page.locator('#live-menu')).toBeVisible();
}

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

test('the menu opens from the keyboard, moves with the arrow keys, and Escape returns focus to the panel', async ({ page }) => {
  await liveFixture(page);
  const panel = page.locator('#live-panel'), menu = page.locator('#live-menu');
  await panel.focus();
  await panel.press('Shift+F10');
  await expect(menu).toBeVisible();
  const items = await page.evaluate(() => [...document.querySelectorAll('#live-menu button')]
    .filter(button => !button.disabled && button.getClientRects().length).map(button => button.dataset.action));
  expect(items.slice(0, 2)).toEqual(['settings', 'start']);
  await expect(page.locator('[data-action="settings"]')).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[data-action="start"]')).toBeFocused();
  await page.keyboard.press('End');
  expect(await focusedItem(page)).toBe(items.at(-1));
  await page.keyboard.press('Home');
  await expect(page.locator('[data-action="settings"]')).toBeFocused();
  await page.keyboard.press('ArrowUp');
  expect(await focusedItem(page)).toBe(items.at(-1));
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[data-action="settings"]')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(panel).toBeFocused();

  // The menu key opens it as well, and Enter runs the focused item.
  await panel.press('ContextMenu');
  await expect(menu).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(menu).toBeHidden();
  await live(page);
  await expect(panel).toBeFocused();
});

test('closing the menu or the settings returns focus to the button they were opened from', async ({ page }) => {
  const server = await liveFixture(page);
  const toggle = page.locator('#live-toggle'), menu = page.locator('#live-menu'), dialog = page.locator('#settings');
  await openMenuFrom(page, '#live-toggle');
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(toggle).toBeFocused();

  // Space runs an item too; the settings dialog then gives focus back to where the menu was opened.
  await openMenuFrom(page, '#answer-larger');
  await page.keyboard.press('Space');
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(page.locator('#answer-larger')).toBeFocused();
  await openMenuFrom(page, '#answer-larger');
  await page.keyboard.press('Enter');
  await expect(dialog).toBeVisible();
  await page.getByRole('button', { name: '닫기', exact: true }).click();
  await expect(page.locator('#answer-larger')).toBeFocused();

  // A click outside on nothing in particular returns focus to the opener; a click on another control keeps it there.
  await openMenuFrom(page, '#live-toggle');
  const box = await page.locator('#live-panel').boundingBox(), shown = await menu.boundingBox();
  const point = { x: box.x + 24, y: box.y + box.height - 24 };
  expect(point.y > shown.y + shown.height || point.x < shown.x).toBe(true);
  await page.mouse.click(point.x, point.y);
  await expect(menu).toBeHidden();
  await expect(toggle).toBeFocused();
  await openMenuFrom(page, '#live-toggle');
  await page.locator('#live-details summary').click();
  await expect(menu).toBeHidden();
  await expect(page.locator('#live-details summary')).toBeFocused();
  await expect(page.locator('#live-details')).toHaveAttribute('open', '');

  // A choice that turns off its opener (stop turns off pause) leaves focus on the panel.
  await toggle.click();
  await live(page);
  await openMenuFrom(page, '#live-pause');
  while (await focusedItem(page) !== 'stop') await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect.poll(server.commands).toContain('session.stop');
  await expect(page.locator('#live-pause')).toBeDisabled();
  await expect(page.locator('#live-panel')).toBeFocused();

  // So does closing the settings after their opener turned off meanwhile (the session ended while they were open).
  await toggle.click();
  await live(page);
  await openMenuFrom(page, '#live-pause');
  await page.keyboard.press('Enter');
  await expect(dialog).toBeVisible();
  server.send({ type: 'error', code: 'session_superseded', message: 'Another session started.', retryable: false });
  await expect(page.locator('#live-action')).toHaveText('다시 시작');
  await expect(page.locator('#live-pause')).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(page.locator('#live-panel')).toBeFocused();
});

test('streaming replies and reconnects never move keyboard focus', async ({ page }) => {
  const server = await liveFixture(page, { audioSource: 'microphone', showCaptions: true });
  await page.locator('#live-toggle').click();
  await live(page);
  // Focus is somewhere else, as it would be in Teams or PowerPoint; any focus() call from the page is counted.
  await page.evaluate(() => {
    const input = Object.assign(document.createElement('input'), { id: 'elsewhere' });
    document.body.append(input);
    input.focus();
    window.__focusCalls = 0;
    const focus = HTMLElement.prototype.focus;
    HTMLElement.prototype.focus = function (...args) { window.__focusCalls++; return focus.apply(this, args); };
  });
  for (let id = 1; id <= 3; id++) {
    server.send({ type: 'transcript.partial', turnId: `t${id}`, revision: 1, text: `Question ${id}` });
    server.send({ type: 'transcript.final', turnId: `t${id}`, revision: 2, text: `Question ${id}?` });
    server.send({ type: 'response.started', turnId: `t${id}`, responseId: `r${id}` });
    for (const part of ['We should ', 'check the ', 'plan first.'])
      server.send({ type: 'response.delta', turnId: `t${id}`, responseId: `r${id}`, text: part, primaryDone: part === 'plan first.' });
    server.send({ type: 'response.completed', turnId: `t${id}`, responseId: `r${id}`, text: `We should check plan ${id} first.`,
      suggestions: [`We should check plan ${id} first.`], sources: [], grounding: 'disabled' });
    await expect(page.locator('#answer-text')).toHaveText(`We should check plan ${id} first.`);
  }
  await server.connections.at(-1).ws.close({ code: 1011, reason: 'Synthetic network drop' });
  await expect.poll(() => server.connections.length).toBe(2);
  await live(page);
  await expect(page.locator('#live-pause')).toBeEnabled();
  await expect(page.locator('#elsewhere')).toBeFocused();
  expect(await page.evaluate(() => window.__focusCalls)).toBe(0);
});
