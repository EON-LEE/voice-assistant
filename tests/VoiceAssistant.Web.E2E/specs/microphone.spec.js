import { openSettings, closeSheet, setConnectionMode, startMeeting, stopMeeting, setPause, confirmConsent } from '../overlay-helpers.js';
import { test, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';

test.use({ launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream',
  `--use-file-for-fake-audio-capture=${fileURLToPath(new URL('../fixtures/original-project.wav', import.meta.url))}`] } });

async function observe(page) {
  await page.addInitScript(() => {
    window.__mic = { tracks: [], calls: [], display: 0, bytes: 0, nonzero: 0 };
    const get = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia = async constraints => {
      window.__mic.calls.push(constraints);
      const stream = await get(constraints);
      window.__mic.tracks.push(...stream.getTracks());
      return stream;
    };
    navigator.mediaDevices.getDisplayMedia = async () => { window.__mic.display++; throw new Error('No real display permissions in this test.'); };
    const send = WebSocket.prototype.send;
    WebSocket.prototype.send = function(data) {
      if (data instanceof ArrayBuffer) {
        window.__mic.bytes += data.byteLength;
        if (new Uint8Array(data).some(n => n)) window.__mic.nonzero++;
      }
      return send.call(this, data);
    };
  });
}
async function choose(page) {
  await page.goto('/');
  await setConnectionMode(page, 'live');
  await page.locator('#audio-source').selectOption('microphone');
  await expect(page.locator('#consent-label')).toContainText("room's audio");
  await confirmConsent(page);
  await startMeeting(page);
}
async function mockServer(page) {
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  let socket, nonzero = 0;
  await page.routeWebSocket('**/api/meeting*', connection => {
    socket = connection; let sent = false;
    connection.onMessage(message => {
      if (typeof message === 'string') {
        const command = JSON.parse(message);
        if (command.type === 'session.start') connection.send(JSON.stringify({ type: 'session.ready' }));
        if (command.type === 'response.request') {
          connection.send(JSON.stringify({ type: 'response.started', turnId: 'mic', responseId: 'r' }));
          connection.send(JSON.stringify({ type: 'response.completed', turnId: 'mic', responseId: 'r', text: 'Original mocked microphone reply.', sources: [] }));
        }
      } else {
        if (new Uint8Array(message).some(value => value)) nonzero++;
        if (!sent) {
          sent = true; connection.send(JSON.stringify({ type: 'transcript.final', turnId: 'mic', revision: 1, text: 'Synthetic room test.' }));
        }
      }
    });
  });
  return { nonzero: () => nonzero, limit: () => socket.send(JSON.stringify({ type: 'error', code: 'session_time_limit', message: 'Session reached its time limit.', retryable: false })) };
}
test('synthetic browser microphone uses real getUserMedia/worklet, mute recovery, P pause, pin and release (mocked WS)', async ({ page }) => {
  await observe(page); const server = await mockServer(page); await choose(page);
  await expect(page.getByTestId('suggest')).toBeEnabled();
  expect(await page.evaluate(() => window.__mic.calls[0])).toEqual({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false,
  });
  await expect.poll(server.nonzero).toBeGreaterThan(0);
  expect(await page.evaluate(() => window.__mic.display)).toBe(0);
  await expect(page.locator('#microphone-device option')).not.toHaveCount(1);
  await page.getByTestId('suggest').click(); await expect(page.getByTestId('reply')).toContainText('mocked microphone');
  await page.getByTestId('pin').click();
  await page.evaluate(() => window.__mic.tracks[0].dispatchEvent(new Event('mute')));
  await expect(page.locator('#microphone-warning')).toContainText('muted by the system/browser');
  await expect(page.getByTestId('stop')).toBeEnabled();
  await page.evaluate(() => window.__mic.tracks[0].dispatchEvent(new Event('unmute')));
  await expect(page.locator('#microphone-warning')).toBeHidden();
  await page.locator('#coach-question').click(); await page.keyboard.press('p');
  await expect(page.getByTestId('pause')).toBeChecked();
  await page.keyboard.press('p'); await expect(page.getByTestId('pause')).not.toBeChecked();
  server.limit();
  await expect(page.getByTestId('status')).toContainText('Session time limit reached');
  await expect(page.getByTestId('stop')).toBeDisabled();
  expect(await page.evaluate(() => window.__mic.tracks.every(track => track.readyState === 'ended'))).toBe(true);
  await expect(page.getByTestId('pinned-reply')).toContainText('mocked microphone');
});
test('microphone permission failure is specific and never falls back to display capture', async ({ page }) => {
  await observe(page); await mockServer(page);
  await page.addInitScript(() => {
    navigator.mediaDevices.getUserMedia = async () => { throw new DOMException('Denied', 'NotAllowedError'); };
  });
  await choose(page);
  await expect(page.getByTestId('error')).toContainText('Microphone permission denied');
  await expect(page.getByTestId('stop')).toBeDisabled();
  expect(await page.evaluate(() => window.__mic.display)).toBe(0);
});
test('real synthetic microphone reaches localhost Fake API and stop releases all tracks', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1', 'Requires real local Fake backend, not Azure.');
  await observe(page); await choose(page);
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await expect.poll(() => page.evaluate(() => window.__mic.nonzero)).toBeGreaterThan(0);
  await page.getByTestId('suggest').click(); await expect(page.getByTestId('reply')).toContainText(/confirm/i);
  await stopMeeting(page);
  await expect(page.getByTestId('stop')).toBeDisabled();
  expect(await page.evaluate(() => window.__mic.tracks.every(track => track.readyState === 'ended'))).toBe(true);
  expect(await page.evaluate(() => window.__mic.calls.length)).toBe(1);
  expect(await page.evaluate(() => window.__mic.display)).toBe(0);
});
