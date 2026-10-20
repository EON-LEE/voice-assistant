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

async function fixture(page, native, expanded = true) {
  await page.route('**/api/client-config', route => route.fulfill({
    json: { mode: 'Fake', webSocketPath: '/api/meeting' },
  }));
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

async function meetingFixture(page) {
  const commands = [];
  await page.route('**/api/client-config', route => route.fulfill({
    json: { mode: 'Fake', webSocketPath: '/api/meeting' },
  }));
  await page.route('**/api/assist/enrich', route => route.fulfill({
    json: { korean: `뜻: ${route.request().postDataJSON().text}`, pronunciation: null },
  }));
  let socket;
  await page.routeWebSocket('**/api/meeting*', ws => {
    socket = ws;
    ws.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message); commands.push(command);
      if (command.type === 'session.start') ws.send(JSON.stringify({ type: 'session.ready' }));
    });
  });
  await page.goto('/live.html');
  await expect(page.locator('#capture-status')).toContainText('꺼짐');
  await page.locator('#live-panel').dispatchEvent('contextmenu', { clientX: 10, clientY: 10 });
  await page.locator('[data-action="start"]').click();
  await expect(page.locator('#capture-status')).toContainText('듣는 중');
  return { commands, send: event => socket.send(JSON.stringify(event)) };
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
