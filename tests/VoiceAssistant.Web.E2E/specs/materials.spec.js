import { openSettings, closeSheet, setConnectionMode, startMeeting, stopMeeting, setPause, confirmConsent } from '../overlay-helpers.js';
import { test, expect } from '@playwright/test';

const limits = { maxDocuments: 300, maxChunks: 5000, maxFileBytes: 5242880, maxCharactersPerDocument: 400000 };
async function mocked(page) {
  let documents = [], active = 0, maxActive = 0, attempts = 0;
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.route('**/api/knowledge**', async route => {
    const request = route.request(), method = request.method();
    if (method === 'GET') return route.fulfill({ json: { documents, limits, usage: { documents: documents.length, chunks: documents.length }, extensions: ['.md', '.txt'] } });
    if (method === 'DELETE') { documents = documents.filter(d => !request.url().endsWith(d.id)); return route.fulfill({ status: 204 }); }
    active++; maxActive = Math.max(maxActive, active); const attempt = ++attempts;
    await new Promise(resolve => setTimeout(resolve, 100));
    active--;
    if (attempt === 2) return route.fulfill({ status: 429, json: { error: 'busy', message: 'private diagnostic never shown' } });
    const text = request.postData() ?? '';
    const title = text.startsWith('{') ? JSON.parse(text).title : /filename="([^"]+)"/.exec(text)?.[1] ?? 'Fixture';
    const document = { id: attempt.toString(16).padStart(32, '0'), title, chunks: 1, characters: 10, updatedAt: '2026-10-01T13:00:00Z' };
    documents.push(document); return route.fulfill({ status: 201, json: document });
  });
  return { attempts: () => attempts, maxActive: () => maxActive };
}
async function panel(page) {
  await page.goto('/materials.html');
  await expect(page.locator('#materials-refresh')).toBeEnabled();
}
test('mocked materials: filtering, two uploads, safe retry, notes, quota, delete and no placeholder link', async ({ page }) => {
  const traffic = await mocked(page); await panel(page);
  await page.locator('#material-files').setInputFiles([
    { name: 'first.md', mimeType: 'text/plain', buffer: Buffer.from('Original first') },
    { name: 'second.txt', mimeType: 'text/plain', buffer: Buffer.from('Original second') },
    { name: 'third.txt', mimeType: 'text/plain', buffer: Buffer.from('Original third') },
    { name: 'binary.txt', mimeType: 'text/plain', buffer: Buffer.from([0, 1, 2]) },
    { name: 'ignore.exe', mimeType: 'application/octet-stream', buffer: Buffer.from([1, 2]) },
  ]);
  await expect(page.locator('#materials-panel')).toContainText('3 files ready');
  await expect(page.locator('#materials-panel')).toContainText('binary/non-UTF-8');
  await page.locator('#materials-upload').click();
  await expect(page.locator('#materials-progress')).toContainText('service is busy');
  await expect.poll(() => traffic.attempts()).toBe(3);
  expect(traffic.maxActive()).toBeLessThanOrEqual(2);
  await expect(page.locator('#materials-progress')).not.toContainText('private diagnostic');
  await page.locator('#materials-retry').click();
  await expect.poll(() => traffic.attempts()).toBe(4);
  await expect(page.locator('#materials-progress')).not.toContainText('busy');
  await page.locator('#material-title').fill('<img src=x onerror=alert(1)>');
  await page.locator('#material-notes').fill('Wholly original pasted fixture.');
  await page.locator('#materials-save-notes').click();
  await expect(page.locator('#materials-documents')).toContainText('<img src=x');
  expect(await page.locator('#materials-documents img').count()).toBe(0);
  await expect(page.locator('#materials-usage')).toContainText('4 / 300');
  page.once('dialog', dialog => dialog.accept());
  await page.locator('#materials-documents button').first().click();
  await expect(page.locator('#materials-usage')).toContainText('3 / 300');
});
test('meeting demo has only a separate materials link; 401 on that page prompts sign-in (mocked)', async ({ page }) => {
  await mocked(page); await page.goto('/');
  await expect(page.locator('#materials-host')).toHaveCount(0);
  await expect(page.locator('#manage-materials')).toHaveAttribute('href', '/materials.html');
  await expect(page.locator('#manage-materials')).toHaveAttribute('target', '_blank');
  await expect(page.locator('#manage-materials')).toHaveAttribute('rel', 'noopener');
  await panel(page);
  await expect(page.locator('#material-files')).toBeEnabled();
  await page.route('**/api/knowledge', route => route.fulfill({ status: 401, json: { error: 'unauthorized' } }));
  await page.locator('#materials-refresh').click();
  await expect(page.locator('#materials-status')).toContainText(/Sign in/i);
});
test('cancel active uploads and do not retry cancelled items (mocked)', async ({ page }) => {
  const traffic = await mocked(page); await panel(page);
  let requested = 0;
  await page.route('**/api/knowledge', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    requested++;
    await new Promise(resolve => setTimeout(resolve, 1000));
    await route.fulfill({ status: 503, json: { error: 'knowledge_unavailable' } }).catch(() => {});
  });
  await page.locator('#material-title').fill('Cancel fixture'); await page.locator('#material-notes').fill('Original');
  await page.locator('#materials-save-notes').click();
  await expect.poll(() => requested).toBe(1);
  await page.locator('#materials-cancel').click();
  await expect(page.locator('#materials-progress')).toContainText('Cancelled locally');
  await expect(page.locator('#materials-retry')).toBeDisabled();
  expect(traffic.attempts()).toBe(0);
});
test('personal reference placeholder URLs render as titles only (mocked WS)', async ({ page }) => {
  await mocked(page);
  await page.routeWebSocket('**/api/meeting*', socket => {
    socket.onMessage(message => {
      if (typeof message !== 'string' || JSON.parse(message).type !== 'session.start') return;
      for (const event of [
        { type: 'session.ready' }, { type: 'transcript.final', turnId: 't', revision: 1, text: 'Fixture?' },
        { type: 'response.started', responseId: 'r', turnId: 't' },
        { type: 'response.completed', responseId: 'r', turnId: 't', text: 'Fixture answer', sources: [
          { title: 'My original notes', url: 'https://my-materials.invalid/' + 'a'.repeat(32), updatedAt: null }], grounding: 'grounded' },
      ]) socket.send(JSON.stringify(event));
    });
  });
  await page.goto('/'); await setConnectionMode(page, 'synthetic'); await startMeeting(page);
  await expect(page.locator('#sources')).toContainText('My original notes (My meeting materials)');
  await expect(page.locator('#sources')).not.toContainText('my-materials.invalid');
  await page.getByTestId('pin').click();
  await expect(page.locator('#pinned-sources')).not.toContainText('my-materials.invalid');
  await stopMeeting(page);
});
test('real localhost Fake materials stores lists and deletes original notes', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_KNOWLEDGE_E2E !== '1', 'Requires integrated local Fake knowledge endpoints.');
  await panel(page);
  const title = `Original E2E notes ${Date.now()}`;
  await page.locator('#material-title').fill(title);
  await page.locator('#material-notes').fill('Fictional Project Lumen is a synthetic browser test. No private material.');
  await page.locator('#materials-save-notes').click();
  await expect(page.locator('#materials-progress')).toContainText('Stored successfully');
  await expect(page.locator('#materials-documents')).toContainText(title);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: `Delete ${title}`, exact: true }).click();
  await expect(page.locator('#materials-documents')).not.toContainText(title);
});

test('folder selection skips hidden/build paths and drag-drop selection remains keyboard-uploadable (mocked)', async ({ page }) => {
  const traffic = await mocked(page); await panel(page);
  await page.locator('#material-folder').evaluate(input => {
    const transfer = new DataTransfer();
    for (const path of ['project/src/original.ts', 'project/node_modules/package/index.js', 'project/.git/config.txt',
      'project/bin/generated.txt', 'project/.hidden/private.txt']) {
      const file = new File(['Original generated fixture'], path.split('/').at(-1), { type: 'text/plain' });
      Object.defineProperty(file, 'webkitRelativePath', { value: path });
      transfer.items.add(file);
    }
    input.files = transfer.files; input.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await expect(page.locator('#materials-panel')).toContainText('1 files ready');
  await expect(page.locator('#materials-panel')).toContainText('4 hidden/build folders');
  await page.locator('#materials-upload').focus(); await page.keyboard.press('Enter');
  await expect.poll(() => traffic.attempts()).toBe(1);
  await page.locator('#materials-drop').evaluate(target => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(['Original dropped text'], 'drop.md', { type: 'text/plain' }));
    target.dispatchEvent(new DragEvent('drop', { dataTransfer: transfer, bubbles: true, cancelable: true }));
  });
  await expect(page.locator('#materials-panel')).toContainText('1 files ready');
  await expect(page.locator('#materials-upload')).toBeEnabled();
});

test('materials open in a new noopener tab without interrupting active meeting (mocked)', async ({ page, context }) => {
  await mocked(page);
  await page.routeWebSocket('**/api/meeting*', socket => socket.onMessage(message => {
    if (typeof message === 'string' && JSON.parse(message).type === 'session.start') socket.send(JSON.stringify({ type: 'session.ready' }));
  }));
  await page.goto('/'); await setConnectionMode(page, 'synthetic'); await startMeeting(page);
  await expect(page.getByTestId('pause')).toBeEnabled();
  // Context routes cover the new page's first request, which occurs before a page-scoped route can be installed.
  await context.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await context.route('**/api/knowledge', route => route.fulfill({ json: {
    documents: [], limits, usage: { documents: 0, chunks: 0 }, extensions: ['.txt'],
  } }));
  const next = context.waitForEvent('page');
  await openSettings(page); await page.locator('#manage-materials').click();
  const materials = await next;
  await expect(materials).toHaveURL(/\/materials\.html$/);
  await expect(materials.locator('#material-files')).toBeEnabled();
  expect(await materials.evaluate(() => window.opener === null)).toBe(true);
  expect(new URL(page.url()).pathname).toBe('/');
  await expect(page.getByTestId('stop')).toBeEnabled();
  await stopMeeting(page);
  await materials.close();
});

test('materials bearer auth remains memory-only and restricted to contract endpoints (injected auth)', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_VITE_AUTH_TESTS !== '1', 'Requires explicit Vite-only injected auth test; no Entra calls.');
  await page.goto('/');
  const result = await page.evaluate(async () => {
    const { BrowserAuth } = await import('/src/auth.ts');
    let authorization = '', redirect, return401 = false, calls = 0;
    const auth = new BrowserAuth({
      location: { protocol: 'https:', hostname: 'example.invalid', origin: 'https://example.invalid' },
      fetch: async (path, init) => {
        if (path === '/api/client-config') return Response.json({ mode: 'Azure', webSocketPath: '/api/meeting',
          clientId: '11111111-1111-1111-1111-111111111111', authority: 'https://login.microsoftonline.com/tenant', scope: 'api://fixture/Meeting.Access' });
        calls++; authorization = new Headers(init.headers).get('Authorization'); redirect = init.redirect;
        return new Response(null, { status: return401 ? 401 : 204 });
      },
      createClient: config => {
        if (config.cache.cacheLocation !== 'memoryStorage') throw new Error('Unsafe auth cache');
        return { initialize: async () => {}, loginPopup: async () => ({ account: { homeAccountId: 'fixture' } }),
          acquireTokenSilent: async () => ({ accessToken: 'synthetic-memory-token' }) };
      },
    });
    await auth.initialize();
    let signedOutFailed = false;
    try { await auth.knowledgeRequest('/api/knowledge'); } catch { signedOutFailed = true; }
    await auth.signIn(); await auth.knowledgeRequest('/api/knowledge', { method: 'GET' });
    let refusedExternal = false;
    try { await auth.knowledgeRequest('https://attacker.invalid/api/knowledge'); } catch { refusedExternal = true; }
    return401 = true; await auth.knowledgeRequest('/api/knowledge');
    return { signedOutFailed, refusedExternal, signedIn: auth.signedIn, authorization, redirect, calls };
  });
  expect(result).toEqual({ signedOutFailed: true, refusedExternal: true, signedIn: false,
    authorization: 'Bearer synthetic-memory-token', redirect: 'error', calls: 2 });
});
