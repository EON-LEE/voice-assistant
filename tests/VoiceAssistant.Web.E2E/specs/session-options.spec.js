import { test, expect } from '@playwright/test';

const fake = { mode: 'Fake', clientId: '', authority: '', scope: '', webSocketPath: '/api/meeting' };
async function setup(page) {
  const starts = [], commands = [];
  let socket;
  await page.route('**/api/client-config', route => route.fulfill({ json: fake }));
  await page.routeWebSocket('**/api/meeting*', connection => {
    socket = connection;
    connection.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message); commands.push(command);
      if (command.type === 'session.start') {
        starts.push(command);
        connection.send(JSON.stringify({ type: 'session.ready' }));
        connection.send(JSON.stringify({ type: 'transcript.final', turnId: 't', revision: 1, text: 'Original fictional test question.' }));
      }
    });
  });
  await page.goto('/');
  await page.getByTestId('mode').selectOption('synthetic');
  await page.locator('#meeting-options').evaluate(details => { details.open = true; });
  await expect(page.getByTestId('start')).toBeEnabled();
  return { starts, commands, send: event => socket.send(JSON.stringify(event)) };
}
test('blank web context sends balanced500 without presumed user facts', async ({ page }) => {
  const backend = await setup(page);
  await page.getByTestId('start').click();
  await expect.poll(() => backend.starts.length).toBe(1);
  expect(backend.starts[0].options).toEqual({
    responseMode: 'balanced', profile: { name: '', role: '', project: '' }, profileConfirmed: false,
    topic: '', phrases: [], endSilenceMs: 500,
  });
  await expect(page.locator('#profile-name')).toBeDisabled();
  await expect(page.locator('#response-mode')).toBeDisabled();
  await page.getByTestId('stop').click();
  await expect(page.locator('#profile-name')).toBeEnabled();
});
test('profile confirmation resets on edit and blocks sending until confirmed', async ({ page }) => {
  const backend = await setup(page);
  await page.locator('#profile-name').fill('Mina');
  await page.locator('#profile-confirmed').check();
  await page.locator('#profile-role').fill('Fictional test engineer');
  await expect(page.locator('#profile-confirmed')).not.toBeChecked();
  await page.getByTestId('start').click();
  await expect(page.getByTestId('error')).toContainText('Confirm');
  expect(backend.starts).toHaveLength(0);
  await page.locator('#profile-project').fill('Original Lumen example');
  await page.locator('#meeting-topic').fill('A fictional release plan');
  await page.locator('#meeting-phrases').fill('Lumen\nrelease gate');
  await page.locator('#profile-confirmed').check();
  await page.getByTestId('start').click();
  await expect.poll(() => backend.starts.length).toBe(1);
  expect(backend.starts[0].options.profile).toEqual({ name: 'Mina', role: 'Fictional test engineer', project: 'Original Lumen example' });
  expect(backend.starts[0].options.phrases).toEqual(['Lumen', 'release gate']);
  await page.getByTestId('stop').click();
  await page.reload();
  await page.getByTestId('mode').selectOption('synthetic');
  await page.locator('#meeting-options').evaluate(details => { details.open = true; });
  await expect(page.locator('#profile-name')).toHaveValue('');
  await expect(page.locator('#meeting-topic')).toHaveValue('');
  await expect(page.locator('#meeting-phrases')).toHaveValue('');
  await expect(page.locator('#profile-confirmed')).not.toBeChecked();
});
test('all answer modes and silence presets reach start.options unchanged', async ({ page }) => {
  const backend = await setup(page);
  for (const [responseMode, silence] of [['balanced', '450'], ['grounded', '700'], ['conversation', '1000'], ['balanced', '500']]) {
    await page.locator('#response-mode').selectOption(responseMode);
    await page.locator('#end-silence').selectOption(silence);
    await page.getByTestId('start').click();
    await expect(page.getByTestId('suggest')).toBeEnabled();
    expect(backend.starts.at(-1).options.responseMode).toBe(responseMode);
    expect(backend.starts.at(-1).options.endSilenceMs).toBe(Number(silence));
    await page.getByTestId('stop').click();
    await expect(page.locator('#profile-name')).toBeEnabled();
  }
});
test('oversized phrase list fails before socket while demo stays one click', async ({ page }) => {
  const backend = await setup(page);
  await page.locator('#meeting-phrases').fill(Array(41).fill('term').join('\n'));
  await page.getByTestId('start').click();
  await expect(page.getByTestId('error')).toContainText('40');
  expect(backend.starts).toHaveLength(0);
  await page.getByTestId('mode').selectOption('demo');
  await expect(page.locator('#meeting-options')).toBeHidden();
  await page.getByTestId('start').click();
  await expect(page.getByTestId('status')).toContainText('DEMO');
  await expect(page.getByTestId('error')).toBeHidden();
  expect(backend.starts).toHaveLength(0);
  await page.getByTestId('stop').click();
});
test('streaming floods batch content, preserve transcript nodes and flush controls/pins', async ({ page }) => {
  const backend = await setup(page);
  await page.getByTestId('start').click();
  await expect(page.getByTestId('suggest')).toBeEnabled();
  backend.send({ type: 'response.started', turnId: 't', responseId: 'r1' });
  await expect(page.locator('#reply-status')).toHaveText('Streaming…');
  await page.evaluate(() => {
    window.__transcriptNode = document.querySelector('#transcript p');
    window.__replyMutations = 0;
    window.__observer = new MutationObserver(records => { window.__replyMutations += records.length; });
    window.__observer.observe(document.querySelector('#reply'), { childList: true });
  });
  for (let i = 0; i < 200; i++) backend.send({ type: 'response.delta', turnId: 't', responseId: 'r1', text: 'x' });
  await expect(page.getByTestId('reply')).toHaveText('x'.repeat(200));
  expect(await page.evaluate(() => window.__replyMutations)).toBeLessThan(40);
  expect(await page.evaluate(() => window.__transcriptNode === document.querySelector('#transcript p'))).toBe(true);
  await page.getByTestId('pin').click();
  const pinned = await page.getByTestId('pinned-reply').textContent();
  backend.send({ type: 'response.completed', turnId: 't', responseId: 'r1', text: 'Authoritative final',
    sources: [], grounding: 'disabled', responseRoute: 'profile', retrievalPrefetched: false });
  await expect(page.getByTestId('reply')).toHaveText('Authoritative final');
  await expect(page.locator('#sources')).toContainText('confirmed profile');
  await expect(page.locator('#sources')).toContainText('not prefetched');
  await expect(page.getByTestId('pinned-reply')).toHaveText(pinned);
  await page.getByTestId('pin').click();
  await expect(page.locator('#pinned-sources')).toContainText('confirmed profile');
  backend.send({ type: 'response.started', turnId: 't', responseId: 'r2' });
  for (let i = 0; i < 100; i++) backend.send({ type: 'response.delta', turnId: 't', responseId: 'r2', text: 'late' });
  await page.getByTestId('pause').check();
  await expect(page.getByTestId('suggest')).toBeDisabled();
  await page.getByTestId('stop').click();
  await expect(page.getByTestId('stop')).toBeDisabled();
  await expect(page.getByTestId('reply')).not.toContainText('late');
  await expect(page.getByTestId('pinned-reply')).toHaveText('Authoritative final');
  expect(backend.commands.some(command => command.type === 'response.cancel')).toBe(true);
  expect(backend.commands.some(command => command.type === 'session.stop')).toBe(true);
});
