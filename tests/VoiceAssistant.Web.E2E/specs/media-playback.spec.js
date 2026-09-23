import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const wav = readFileSync(new URL('../fixtures/original-project.wav', import.meta.url));
const metadata = JSON.parse(readFileSync(new URL('../fixtures/original-project.json', import.meta.url), 'utf8'));
const fakeConfig = { mode: 'Fake', clientId: '', authority: '', scope: '', webSocketPath: '/api/meeting' };

test('concurrent auth initialization shares one request and rejected ticket clears expired sign-in', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_VITE_AUTH_TESTS !== '1', 'Injected auth module test requires explicit Vite source-server opt-in.');
  await page.goto('/');
  const result = await page.evaluate(async () => {
    const { BrowserAuth } = await import('/src/auth.ts');
    let calls = 0, rejectTicket = true;
    const config = { mode: 'Azure', clientId: '11111111-1111-1111-1111-111111111111',
      authority: 'https://login.microsoftonline.com/tenant', scope: 'api://test/Meeting.Access', webSocketPath: '/api/meeting' };
    const auth = new BrowserAuth({
      location: { protocol: 'https:', hostname: 'example.invalid', origin: 'https://example.invalid' },
      fetch: async path => {
        if (path === '/api/client-config') { calls++; await new Promise(r => setTimeout(r, 10)); return Response.json(config); }
        return rejectTicket ? new Response('', { status: 401 })
          : Response.json({ ticket: 'single-use-fixture', expiresAt: new Date(Date.now() + 30000).toISOString() });
      },
      createClient: () => ({ initialize: async () => {}, loginPopup: async () => ({ account: { homeAccountId: 'fixture' } }),
        acquireTokenSilent: async () => ({ accessToken: 'memory-only-test-token' }) }),
    });
    await Promise.all([auth.initialize(), auth.initialize(), auth.initialize()]);
    await auth.signIn();
    let error = '';
    try { await auth.endpoint(false); } catch (e) { error = e.message; }
    const expired = !auth.signedIn;
    rejectTicket = false;
    await auth.signIn();
    const endpoint = await auth.endpoint(false);
    return { calls, error, expired, endpoint: endpoint.href };
  });
  expect.soft(result.calls).toBe(1);
  expect(result.error).toContain('401');
  expect.soft(result.expired).toBe(true);
  expect(result.endpoint).toBe('wss://example.invalid/api/meeting?ticket=single-use-fixture');
});

test('failed auth configuration can retry without retaining a poisoned initialization', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_VITE_AUTH_TESTS !== '1', 'Injected auth module test requires explicit Vite source-server opt-in.');
  await page.goto('/');
  const result = await page.evaluate(async () => {
    const { BrowserAuth } = await import('/src/auth.ts');
    let calls = 0;
    const auth = new BrowserAuth({
      location: { protocol: 'http:', hostname: 'localhost', origin: 'http://localhost' },
      fetch: async () => ++calls === 1 ? new Response('', { status: 503 })
        : Response.json({ mode: 'Fake', webSocketPath: '/api/meeting' }),
      createClient: () => { throw new Error('Fake must not initialize MSAL'); },
    });
    let failed = false;
    try { await auth.initialize(); } catch { failed = true; }
    const config = await auth.initialize();
    return { failed, calls, mode: config.mode, endpoint: (await auth.endpoint(true)).href };
  });
  expect(result).toEqual({ failed: true, calls: 2, mode: 'Fake', endpoint: 'ws://localhost/api/meeting' });
});

async function mediaFixture(page) {
  await page.route('**/original-project.wav', route => route.fulfill({ body: wav, contentType: 'audio/wav' }));
  await page.addInitScript(() => {
    window.__mediaTest = { calls: 0, tracks: [], contexts: [], frames: 0, nonzero: 0, sizes: [], microphones: 0 };
    const NativeContext = window.AudioContext;
    window.AudioContext = class extends NativeContext {
      constructor(...args) { super(...args); window.__mediaTest.contexts.push(this); }
    };
    const send = WebSocket.prototype.send;
    WebSocket.prototype.send = function(data) {
      if (data instanceof ArrayBuffer) {
        window.__mediaTest.frames++;
        if (new Uint8Array(data).some(n => n !== 0)) window.__mediaTest.nonzero++;
        window.__mediaTest.sizes.push(data.byteLength);
      }
      return send.call(this, data);
    };
    Object.defineProperty(navigator.mediaDevices, 'getUserMedia', { value: async () => {
      window.__mediaTest.microphones++; throw new Error('No microphone allowed.');
    } });
    Object.defineProperty(navigator.mediaDevices, 'getDisplayMedia', { value: async () => {
      window.__mediaTest.calls++;
      const audio = document.createElement('audio');
      audio.src = '/original-project.wav';
      audio.loop = true;
      const context = new AudioContext();
      const gain = context.createGain(); gain.gain.value = 0;
      context.createMediaElementSource(audio).connect(gain).connect(context.destination);
      await context.resume();
      await audio.play();
      const stream = audio.captureStream();
      if (!stream.getAudioTracks().length) throw new Error('HTMLMediaElement yielded no audio track.');
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 16;
      const video = canvas.captureStream(1).getVideoTracks()[0];
      stream.addTrack(video);
      window.__mediaTest.tracks.push(...stream.getTracks());
      window.__mediaTest.audio = audio;
      // Release the test-owned decoder/context when the application releases its capture.
      const track = stream.getAudioTracks()[0], originalStop = track.stop.bind(track);
      track.stop = () => { originalStop(); audio.pause(); void context.close(); };
      return stream;
    } });
  });
}

async function prepare(page) {
  await page.goto('/');
  await page.getByTestId('mode').selectOption('live');
  await expect(page.locator('#auth-status')).toContainText('LOCAL FAKE');
  await page.getByTestId('consent').check();
}

test('original offline speech fixture has documented canonical PCM and silence', () => {
  expect(wav.toString('ascii', 0, 4)).toBe('RIFF');
  expect(wav.toString('ascii', 8, 16)).toBe('WAVEfmt ');
  expect(wav.readUInt16LE(20)).toBe(1);
  expect(wav.readUInt16LE(22)).toBe(1);
  expect(wav.readUInt32LE(24)).toBe(16000);
  expect(wav.readUInt16LE(34)).toBe(16);
  expect(wav.readUInt32LE(40)).toBe(wav.length - 44);
  expect(wav.length).toBeLessThan(32000 * 15);
  expect(wav.subarray(-32000).every(n => n === 0)).toBe(true);
  expect(createHash('sha256').update(wav).digest('hex')).toBe(metadata.sha256);
  expect(metadata.synthetic).toBe(true);
  expect(metadata.speechEndSample).toBeNull();
});

test('HTMLMediaElement original speech -> native worklet -> actual Fake API, twenty clean restarts', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1', 'Requires explicit local Fake API; not Azure semantics.');
  test.setTimeout(120_000);
  await mediaFixture(page);
  await prepare(page);
  for (let cycle = 0; cycle < 20; cycle++) {
    if (cycle) await page.getByTestId('consent').check();
    await page.getByTestId('start').click();
    await expect(page.getByTestId('suggest')).toBeEnabled();
    await expect(page.getByTestId('status')).toContainText('LOCAL FAKE');
    if (cycle === 0) {
      // Exercise the complete generated utterance, not merely its initial nonzero samples.
      await expect.poll(() => page.evaluate(() => window.__mediaTest.audio.currentTime), { timeout: 10000 })
        .toBeGreaterThan(metadata.synthesisEndSample / 16000);
    }
    await page.getByTestId('suggest').click();
    await expect(page.getByTestId('reply')).toContainText(/confirm/i);
    await page.getByTestId('stop').click();
    await expect(page.getByTestId('stop')).toBeDisabled();
    await expect.poll(() => page.evaluate(() =>
      window.__mediaTest.tracks.every(t => t.readyState === 'ended') &&
      window.__mediaTest.contexts.every(c => c.state === 'closed')
    )).toBe(true);
  }
  const observed = await page.evaluate(() => ({
    calls: window.__mediaTest.calls, nonzero: window.__mediaTest.nonzero,
    sizes: [...new Set(window.__mediaTest.sizes)], microphones: window.__mediaTest.microphones,
  }));
  expect(observed.calls).toBe(20);
  expect(observed.nonzero).toBeGreaterThanOrEqual(20);
  expect(observed.sizes).toEqual([640]);
  expect(observed.microphones).toBe(0);
});

test('rapid mode switching ignores late configuration failures and keeps demo explicit', async ({ page }) => {
  let release;
  const delayed = new Promise(resolve => { release = resolve; });
  await page.route('**/api/client-config', async route => {
    await delayed; await route.fulfill({ status: 503, json: { error: 'unavailable' } });
  });
  await page.goto('/');
  await page.getByTestId('mode').selectOption('live');
  await page.getByTestId('mode').selectOption('synthetic');
  await page.getByTestId('mode').selectOption('demo');
  release();
  await page.getByTestId('start').click();
  await expect(page.getByTestId('suggest')).toBeEnabled({ timeout: 10000 });
  await expect(page.getByTestId('status')).toContainText('DEMO');
  await expect(page.getByTestId('error')).toBeHidden();
  await page.getByTestId('stop').click();
});

test('transcripts replies and citation strings stay inert text', async ({ page }) => {
  const hostile = '<img src=x onerror="window.__injected=true"><script>window.__injected=true</script>';
  await page.route('**/api/client-config', route => route.fulfill({ json: fakeConfig }));
  await page.routeWebSocket('**/api/meeting*', socket => {
    socket.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message);
      if (command.type === 'session.start') {
        socket.send(JSON.stringify({ type: 'session.ready' }));
        socket.send(JSON.stringify({ type: 'transcript.final', turnId: 't', revision: 1, text: hostile }));
      }
      if (command.type === 'response.request') {
        socket.send(JSON.stringify({ type: 'response.started', turnId: 't', responseId: 'r' }));
        socket.send(JSON.stringify({ type: 'response.completed', turnId: 't', responseId: 'r', text: hostile,
          sources: [{ title: hostile, url: 'javascript:window.__injected=true' }], grounding: 'grounded' }));
      }
    });
  });
  await page.goto('/');
  await page.getByTestId('mode').selectOption('synthetic');
  await page.getByTestId('start').click();
  await expect(page.getByTestId('transcript')).toContainText(hostile);
  await page.getByTestId('suggest').click();
  await expect(page.getByTestId('reply')).toHaveText(hostile);
  await page.getByTestId('pin').click();
  expect(await page.locator('#transcript img, #reply script, #sources a, #pinned img').count()).toBe(0);
  expect(await page.evaluate(() => window.__injected)).toBeUndefined();
  await page.getByTestId('stop').click();
});

test('media mute fails visibly and a new explicit share recovers', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1', 'Requires local Fake API.');
  await mediaFixture(page); await prepare(page);
  await page.getByTestId('start').click();
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await page.evaluate(() => window.__mediaTest.tracks.find(t => t.kind === 'audio').dispatchEvent(new Event('mute')));
  await expect(page.getByTestId('error')).toContainText('unavailable');
  await expect(page.getByTestId('stop')).toBeDisabled();
  await page.getByTestId('consent').check();
  await page.getByTestId('start').click();
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await expect(page.getByTestId('error')).toBeHidden();
  await page.getByTestId('stop').click();
});

test('a newer partial turn blocks suggestion requests until that turn is final', async ({ page }) => {
  await page.route('**/api/client-config', route => route.fulfill({ json: fakeConfig }));
  let activeSocket;
  let requests = 0;
  await page.routeWebSocket('**/api/meeting*', socket => {
    activeSocket = socket;
    socket.onMessage(message => {
      if (typeof message !== 'string') return;
      const command = JSON.parse(message);
      if (command.type === 'session.start') {
        socket.send(JSON.stringify({ type: 'session.ready' }));
        socket.send(JSON.stringify({ type: 'transcript.final', turnId: 'a', revision: 1, text: 'First question.' }));
      }
      if (command.type === 'response.request') requests++;
    });
  });
  await page.goto('/');
  await page.getByTestId('mode').selectOption('synthetic');
  await page.getByTestId('start').click();
  await expect(page.getByTestId('suggest')).toBeEnabled();
  activeSocket.send(JSON.stringify({ type: 'transcript.partial', turnId: 'b', revision: 1, text: 'A newer question' }));
  await expect(page.getByTestId('transcript')).toContainText('A newer question');
  await expect.soft(page.getByTestId('suggest')).toBeDisabled();
  await page.getByTestId('suggest').dispatchEvent('click');
  await page.getByTestId('pause').check(); // Round trip through UI ensures the click handler has completed.
  expect.soft(requests).toBe(0);
  await page.getByTestId('pause').uncheck();
  activeSocket.send(JSON.stringify({ type: 'transcript.final', turnId: 'b', revision: 2, text: 'A newer question finished.' }));
  await expect(page.getByTestId('suggest')).toBeEnabled();
  await page.getByTestId('suggest').click();
  await expect.poll(() => requests).toBe(1);
  await page.getByTestId('stop').click();
});
