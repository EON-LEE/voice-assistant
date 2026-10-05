import { openSettings, closeSheet, setConnectionMode, startMeeting, stopMeeting, setPause, confirmConsent } from '../overlay-helpers.js';
import { test, expect } from '@playwright/test';

const answer = 'Let me confirm the remaining dependencies before committing to a date.';

async function mockCapture(page, behavior = 'audio') {
  await page.addInitScript(({ behavior }) => {
    window.__captureCalls = 0;
    window.__microphoneCalls = 0;
    window.__capturedTracks = [];
    Object.defineProperty(navigator.mediaDevices, 'getUserMedia', {
      configurable: true,
      value: async () => {
        window.__microphoneCalls++;
        throw new Error('Microphone capture is forbidden in this test.');
      }
    });
    Object.defineProperty(navigator.mediaDevices, 'getDisplayMedia', {
      configurable: true,
      value: async () => {
        window.__captureCalls++;
        if (behavior === 'denied') {
          throw new DOMException('The user denied screen sharing.', 'NotAllowedError');
        }
        const canvas = document.createElement('canvas');
        canvas.width = 32;
        canvas.height = 32;
        const stream = canvas.captureStream(10);
        if (behavior === 'audio') {
          const context = new AudioContext();
          const oscillator = context.createOscillator();
          const destination = context.createMediaStreamDestination();
          oscillator.connect(destination);
          oscillator.start();
          await context.resume();
          oscillator.stop(context.currentTime + 0.4);
          stream.addTrack(destination.stream.getAudioTracks()[0]);
          window.__testAudioContext = context;
        }
        window.__capturedTracks.push(...stream.getTracks());
        return stream;
      }
    });
  }, { behavior });
}

async function mockBackend(page) {
  const traffic = { frames: 0, config: 0, tickets: 0, sockets: 0 };
  await page.route('**/api/client-config', async route => {
    traffic.config++;
    await route.fulfill({ json: {
      mode: 'Fake',
      clientId: '',
      authority: '',
      scope: '',
      webSocketPath: '/api/meeting'
    } });
  });
  await page.route('**/api/session/ticket', async route => {
    traffic.tickets++;
    await route.fulfill({ json: {
      ticket: 'one-use-test-ticket',
      expiresAt: new Date(Date.now() + 30_000).toISOString()
    } });
  });
  await page.routeWebSocket('**/api/meeting*', socket => {
    traffic.sockets++;
    let transcribed = false;
    let response = 0;
    socket.onMessage(message => {
      if (typeof message !== 'string') {
        traffic.frames++;
        if (!transcribed) {
          transcribed = true;
          socket.send(JSON.stringify({
            type: 'transcript.final', turnId: 'test-turn', revision: 1,
            text: 'Can we commit to delivering this by Friday?'
          }));
        }
        return;
      }
      const command = JSON.parse(message);
      if (command.type === 'session.start') {
        expect(command.protocolVersion).toBe(1);
        expect(command.audio).toEqual({ encoding: 'pcm_s16le', sampleRate: 16000, channels: 1 });
        socket.send(JSON.stringify({ type: 'session.ready' }));
      }
      if (command.type === 'response.request') {
        const responseId = `reply-${++response}`;
        socket.send(JSON.stringify({ type: 'response.started', turnId: 'test-turn', responseId }));
        for (const text of [answer.slice(0, 20), answer.slice(20)]) {
          socket.send(JSON.stringify({ type: 'response.delta', turnId: 'test-turn', responseId, text }));
        }
        socket.send(JSON.stringify({
          type: 'response.completed', turnId: 'test-turn', responseId,
          text: answer, sources: [], grounding: 'disabled'
        }));
      }
    });
  });
  return traffic;
}

async function prepareLive(page) {
  await page.goto('/');
  await setConnectionMode(page, 'live');
  await expect(page.locator('#auth-status')).toContainText(/fake|development|ready|local/i);
  await confirmConsent(page);
}

test('offline demo never requests media, identity, or backend access', async ({ page }) => {
  await mockCapture(page);
  const traffic = await mockBackend(page);
  await page.goto('/');
  await expect(page.getByTestId('mode')).toHaveValue('demo');
  await startMeeting(page);
  await expect.poll(() => page.locator('#demo-audio').evaluate(audio =>
    audio.currentTime > 0 && !audio.paused && !audio.muted && audio.volume > 0
  )).toBe(true);
  await expect(page.getByTestId('transcript')).toContainText('Project Lumen', { timeout: 10_000 });
  await expect(page.locator('#reply-status')).toHaveText('Complete', { timeout: 10_000 });
  await expect(page.getByTestId('reply')).toContainText("before Friday's update");
  await expect(page.getByTestId('pin')).toBeEnabled();
  await page.getByTestId('pin').click();
  const pinned = await page.getByTestId('pinned-reply').textContent();
  await page.getByTestId('suggest').click();
  await expect(page.getByTestId('pinned-reply')).toHaveText(pinned ?? '');
  await stopMeeting(page);
  await expect(page.locator('#demo-audio')).not.toHaveAttribute('src', /.+/);
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
  expect(traffic).toEqual({ frames: 0, config: 0, tickets: 0, sockets: 0 });
});

test('denied audio sharing is visible and never falls back to microphone', async ({ page }) => {
  await mockCapture(page, 'denied');
  await mockBackend(page);
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('error')).toBeVisible();
  await expect(page.getByTestId('error')).toContainText(/denied|permission|cancel|notallowed/i);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
  await expect(page.locator('#consent')).not.toBeChecked();
  await confirmConsent(page);
  await expect(page.getByTestId('start')).toBeEnabled();
});

test('stopping the audible demo prevents late replies and allows a clean replay', async ({ page }) => {
  await mockCapture(page);
  const traffic = await mockBackend(page);
  await page.goto('/');
  await startMeeting(page);
  await expect.poll(() => page.locator('#demo-audio').evaluate(audio => audio.currentTime > 0)).toBe(true);
  await setPause(page, true);
  await expect.poll(() => page.locator('#demo-audio').evaluate(audio => audio.paused)).toBe(true);
  await setPause(page, false);
  await expect.poll(() => page.locator('#demo-audio').evaluate(audio => !audio.paused)).toBe(true);
  await stopMeeting(page);
  await expect(page.getByTestId('status')).toContainText('Stopped');
  await expect(page.locator('#demo-audio')).not.toHaveAttribute('src', /.+/);
  await expect(page.locator('#reply-status')).not.toHaveText('Complete');
  await startMeeting(page);
  await expect(page.locator('#reply-status')).toHaveText('Complete', { timeout: 10_000 });
  await stopMeeting(page);
  expect(traffic).toEqual({ frames: 0, config: 0, tickets: 0, sockets: 0 });
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
});

test('blocked demo playback reports an error without faking a completed answer', async ({ page }) => {
  await page.addInitScript(() => {
    HTMLMediaElement.prototype.play = async () => { throw new DOMException('Blocked', 'NotAllowedError'); };
  });
  await page.goto('/');
  await startMeeting(page);
  await expect(page.getByTestId('error')).toContainText('Demo audio could not play');
  await expect(page.getByTestId('start')).toBeEnabled();
  await expect(page.locator('#reply-status')).not.toHaveText('Complete');
});

test('live mode does not capture before explicit consent and start', async ({ page }) => {
  await mockCapture(page);
  await mockBackend(page);
  await page.goto('/');
  await setConnectionMode(page, 'live');
  await expect(page.locator('#auth-status')).toContainText(/fake|development|ready|local/i);
  await expect(page.locator('#consent')).not.toBeChecked();
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
  await confirmConsent(page);
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
});

test('sharing without audio stops the captured video and explains the problem', async ({ page }) => {
  await mockCapture(page, 'video');
  await mockBackend(page);
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('error')).toBeVisible();
  await expect(page.getByTestId('error')).toContainText(/audio/i);
  expect(await page.evaluate(() => window.__capturedTracks.every(track => track.readyState === 'ended'))).toBe(true);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
});

test('shared synthetic media passes through the real worklet and releases every track on stop', async ({ page }) => {
  await mockCapture(page);
  const traffic = await mockBackend(page);
  await prepareLive(page);
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
  await startMeeting(page);
  await expect(page.getByTestId('transcript')).toContainText('Friday');
  expect(traffic.frames).toBeGreaterThan(0);
  await page.getByTestId('suggest').click();
  await expect(page.getByTestId('reply')).toHaveText(answer);
  await page.getByTestId('pin').click();
  await expect(page.getByTestId('pinned-reply')).toContainText(answer);
  await setPause(page, true);
  await expect(page.getByTestId('pause')).toBeChecked();
  await stopMeeting(page);
  await expect.poll(() => page.evaluate(() =>
    window.__capturedTracks.length === 2 &&
    window.__capturedTracks.every(track => track.readyState === 'ended')
  )).toBe(true);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
});

test('ending screen sharing cleans up the session and remaining audio track', async ({ page }) => {
  await mockCapture(page);
  await mockBackend(page);
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('transcript')).toContainText('Friday');
  await page.evaluate(() => {
    const track = window.__capturedTracks.find(item => item.kind === 'video');
    track.stop();
    track.dispatchEvent(new Event('ended'));
  });
  await expect.poll(() => page.evaluate(() =>
    window.__capturedTracks.every(track => track.readyState === 'ended')
  )).toBe(true);
  await expect(page.locator('#consent')).not.toBeChecked();
  await confirmConsent(page);
  await expect(page.getByTestId('start')).toBeEnabled();
});

test('real audio worklet survives a short main-thread stall without discarding the session', async ({ page }) => {
  await mockCapture(page);
  const traffic = await mockBackend(page);
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('transcript')).toContainText('Friday');
  const before = traffic.frames;
  await page.evaluate(() => {
    const until = performance.now() + 350;
    while (performance.now() < until) { /* Intentionally simulate a busy meeting UI thread. */ }
  });
  await expect.poll(() => traffic.frames).toBeGreaterThan(before + 12);
  await expect(page.getByTestId('stop')).toBeEnabled();
  await expect(page.getByTestId('error')).toBeHidden();
  await stopMeeting(page);
});

test('backend disconnect stops capture and never claims a successful reply', async ({ page }) => {
  await mockCapture(page);
  await mockBackend(page);
  await page.routeWebSocket('**/api/meeting*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && JSON.parse(message).type === 'session.start') {
        socket.close({ code: 1011, reason: 'Synthetic backend unavailable' });
      }
    });
  });
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('error')).toBeVisible();
  await expect.poll(() => page.evaluate(() =>
    window.__capturedTracks.every(track => track.readyState === 'ended')
  )).toBe(true);
  await expect(page.locator('#consent')).not.toBeChecked();
  await confirmConsent(page);
  await expect(page.getByTestId('start')).toBeEnabled();
});

test('mobile-width layout does not require horizontal scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  expect(await page.evaluate(() =>
    document.documentElement.scrollWidth <= window.innerWidth
  )).toBe(true);
  await expect(page.locator('#coach-toggle')).toBeVisible();
});

test('no relevant references is distinct from a grounded or failed search', async ({ page }) => {
  await mockCapture(page);
  await mockBackend(page);
  await page.routeWebSocket('**/api/meeting*', socket => {
    socket.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message);
      if (command.type === 'session.start') {
        socket.send(JSON.stringify({ type: 'session.ready' }));
        socket.send(JSON.stringify({ type: 'transcript.final', turnId: 'moon', revision: 1, text: 'Humans made history on the moon.' }));
        socket.send(JSON.stringify({ type: 'response.started', responseId: 'moon-reply', turnId: 'moon' }));
        socket.send(JSON.stringify({
          type: 'response.completed', responseId: 'moon-reply', turnId: 'moon',
          text: 'That was an important moment.', sources: [], grounding: 'no_matches'
        }));
      }
    });
  });
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.locator('#sources')).toContainText('No relevant references');
  await expect(page.locator('#sources')).toContainText('transcript only');
  await expect(page.locator('#sources li')).toHaveCount(1);
  await expect(page.getByTestId('error')).toBeHidden();
  await stopMeeting(page);
});

test('explicit synthetic mode connects to the actual local API', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1', 'Requires the explicit local Fake API.');
  await mockCapture(page);
  await page.goto('/');
  await setConnectionMode(page, 'synthetic');
  await startMeeting(page);
  await expect(page.getByTestId('transcript')).not.toBeEmpty();
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await page.getByTestId('suggest').click();
  await expect(page.getByTestId('reply')).toContainText(/confirm/i);
  await stopMeeting(page);
  expect(await page.evaluate(() => window.__captureCalls)).toBe(0);
});

test('shared media worklet reaches the actual local API and returns a reply', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1', 'Requires the explicit local Fake API.');
  await mockCapture(page);
  await prepareLive(page);
  await startMeeting(page);
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await page.getByTestId('suggest').click();
  await expect(page.getByTestId('reply')).toContainText(/confirm/i);
  await stopMeeting(page);
  await expect.poll(() => page.evaluate(() =>
    window.__capturedTracks.length === 2 &&
    window.__capturedTracks.every(track => track.readyState === 'ended')
  )).toBe(true);
  expect(await page.evaluate(() => window.__captureCalls)).toBe(1);
  expect(await page.evaluate(() => window.__microphoneCalls)).toBe(0);
});
