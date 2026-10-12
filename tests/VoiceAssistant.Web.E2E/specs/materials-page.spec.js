import { test, expect } from '@playwright/test';
const limits = { maxDocuments: 300, maxChunks: 5000, maxFileBytes: 5242880, maxCharactersPerDocument: 400000 };

async function service(page, initial = []) {
  let documents = initial, deletes = [], uploads = 0;
  await page.route('**/api/client-config', route => route.fulfill({ json: { mode: 'Fake', webSocketPath: '/api/meeting' } }));
  await page.route('**/api/knowledge**', async route => {
    const method = route.request().method();
    if (method === 'GET') return route.fulfill({ json: { documents, limits,
      usage: { documents: documents.length, chunks: documents.reduce((n, d) => n + d.chunks, 0) }, extensions: ['.txt', '.pptx', '.pdf', '.docx', '.md', '.ts'] } });
    if (method === 'DELETE') {
      const id = new URL(route.request().url()).pathname.split('/').at(-1);
      deletes.push(id); documents = documents.filter(document => document.id !== id);
      return route.fulfill({ status: 204 });
    }
    uploads++;
    return route.fulfill({ status: 503, json: { error: 'knowledge_unavailable' } });
  });
  return { deletes: () => deletes, uploads: () => uploads };
}
const docs = () => [
  { id: 'a'.repeat(32), title: 'Original trip glossary.md', chunks: 3, updatedAt: '2026-10-02T10:00:00Z' },
  { id: 'b'.repeat(32), title: 'Original Q&A.docx', chunks: 2, updatedAt: '2026-10-02T11:00:00Z' },
];

test('dedicated page has indexed tables, accessible bulk confirmation, help and back link', async ({ page }) => {
  const backend = await service(page, docs());
  await page.goto('/materials.html');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('My meeting materials');
  await expect(page.locator('#materials-auth-status')).toContainText('LOCAL FAKE');
  await expect(page.locator('#materials-documents tr')).toHaveCount(2);
  await expect(page.locator('#materials-usage')).toContainText('2 / 300');
  await expect(page.locator('#back-to-meeting')).toHaveAttribute('href', '/');
  await expect(page.locator('.materials-help')).toContainText('images/diagram text inside slides are NOT read');
  await expect(page.locator('.materials-help')).toContainText('Legacy .doc and .ppt are unsupported');
  await page.locator('#materials-select-all').check();
  await expect(page.locator('#materials-delete-selected')).toHaveText('Delete selected (2)');
  page.once('dialog', dialog => dialog.dismiss());
  await page.locator('#materials-delete-selected').click();
  expect(backend.deletes()).toEqual([]);
  await expect(page.locator('#materials-documents tr')).toHaveCount(2);
  page.once('dialog', dialog => { expect(dialog.message()).toContain('2 indexed documents'); return dialog.accept(); });
  await page.locator('#materials-delete-selected').click();
  await expect(page.locator('#materials-documents tr')).toHaveCount(0);
  expect(backend.deletes()).toEqual(docs().map(document => document.id));
  await expect(page.locator('#materials-select-all')).not.toBeChecked();
  await expect(page.locator('#materials-delete-selected')).toBeDisabled();
});
test('per-file table reports size and safe failure, retry only that file', async ({ page }) => {
  const backend = await service(page);
  await page.goto('/materials.html');
  await expect(page.locator('#material-files')).toBeEnabled();
  await page.locator('#material-files').setInputFiles([
    { name: 'one.md', mimeType: 'text/plain', buffer: Buffer.from('First original file') },
    { name: 'two.ts', mimeType: 'text/plain', buffer: Buffer.from('const synthetic = true;') },
  ]);
  await expect(page.locator('#materials-upload')).toBeEnabled();
  await page.locator('#materials-upload').click();
  await expect(page.locator('#materials-progress tr')).toHaveCount(2);
  await expect(page.locator('#materials-progress tr').first()).toContainText('19 B');
  await expect(page.locator('#materials-progress')).toContainText('temporarily unavailable');
  await expect(page.getByRole('button', { name: 'Retry one.md', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Retry one.md', exact: true }).click();
  await expect.poll(backend.uploads).toBe(3);
  await expect(page.locator('#materials-upload-summary')).toContainText('2 failed');
});
test('phone layout scrolls tables locally without widening the page', async ({ page }) => {
  await service(page, docs());
  await page.setViewportSize({ width: 390, height: 844 }); await page.goto('/materials.html');
  await expect(page.locator('#materials-documents tr')).toHaveCount(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.getByLabel('Choose files')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Indexed documents', exact: true })).toHaveAttribute('tabindex', '0');
});
test('unavailable configuration fails closed with explicit retry, never assumes sign-in', async ({ page }) => {
  await page.route('**/api/client-config', route => route.fulfill({ status: 503, json: { error: 'unavailable' } }));
  await page.goto('/materials.html');
  await expect(page.locator('#materials-auth-status')).toContainText('unavailable');
  await expect(page.locator('#material-files')).toBeDisabled();
  await expect(page.locator('#materials-signin')).toBeDisabled();
  await service(page); await page.locator('#materials-reconnect').click();
  await expect(page.locator('#material-files')).toBeEnabled();
});

test('materials controller signs in separately via popup with origin redirect and memory-only token (injected)', async ({ page, context }) => {
  test.skip(process.env.VOICE_ASSISTANT_VITE_AUTH_TESTS !== '1', 'Vite-only injected MSAL client; no live identity provider.');
  await service(page);
  await page.goto('/materials.html');
  await expect(page.locator('#material-files')).toBeEnabled();
  await page.evaluate(async () => {
    const { BrowserAuth } = await import('/src/auth.ts');
    const { mountMaterialsPage } = await import('/src/materials-page-controller.ts');
    for (const id of ['materials-signin', 'materials-reconnect']) {
      const old = document.getElementById(id); old.replaceWith(old.cloneNode(true));
    }
    document.getElementById('materials-host').replaceChildren();
    window.__authTest = { calls: 0, activeAtPopup: false, redirect: '', cache: '', bearer: '', hash: '' };
    const auth = new BrowserAuth({
      location: { protocol: 'https:', hostname: 'example.invalid', origin: location.origin },
      fetch: async (path, init) => {
        if (path === '/api/client-config') return Response.json({ mode: 'Azure', webSocketPath: '/api/meeting',
          clientId: '11111111-1111-1111-1111-111111111111', authority: 'https://login.microsoftonline.com/tenant', scope: 'api://fixture/Meeting.Access' });
        window.__authTest.calls++; window.__authTest.bearer = new Headers(init.headers).get('Authorization');
        return Response.json({ documents: [], limits: { maxDocuments: 300, maxChunks: 5000, maxFileBytes: 5242880, maxCharactersPerDocument: 400000 },
          usage: { documents: 0, chunks: 0 }, extensions: ['.txt'] });
      },
      createClient: config => {
        window.__authTest.redirect = config.auth.redirectUri; window.__authTest.cache = config.cache.cacheLocation;
        return {
          initialize: async () => {},
          loginPopup: () => {
            window.__authTest.activeAtPopup = navigator.userActivation.isActive;
            const popup = window.open(config.auth.redirectUri + '/#code=synthetic-code&state=fixture-state', 'fixture-auth-popup');
            return new Promise((resolve, reject) => {
              if (!popup) { reject(new Error('Popup blocked')); return; }
              const timeout = setTimeout(() => { clearInterval(interval); popup.close(); reject(new Error('Fixture timed out')); }, 5000);
              const interval = setInterval(() => {
                try {
                  if (!popup.document.querySelector('#start') || !popup.location.hash.includes('synthetic-code')) return;
                  window.__authTest.hash = popup.location.hash; clearInterval(interval); clearTimeout(timeout); popup.close();
                  resolve({ account: { homeAccountId: 'synthetic-account' } });
                } catch { /* Wait while the synthetic popup navigates. */ }
              }, 50);
            });
          },
          acquireTokenSilent: async () => ({ accessToken: 'synthetic-memory-only' }),
        };
      },
    });
    mountMaterialsPage(auth);
  });
  await expect(page.locator('#materials-signin')).toBeEnabled();
  await expect(page.locator('#material-files')).toBeDisabled();
  expect(await page.evaluate(() => window.__authTest.calls)).toBe(0);
  const popupEvent = context.waitForEvent('page');
  await page.locator('#materials-signin').click(); const popup = await popupEvent;
  await expect(page.locator('#materials-auth-status')).toContainText('Signed in in this tab');
  await expect(page.locator('#material-files')).toBeEnabled();
  const observed = await page.evaluate(() => ({ ...window.__authTest, local: localStorage.length, session: sessionStorage.length }));
  expect(observed.activeAtPopup).toBe(true);
  expect(observed.redirect).toBe(new URL(page.url()).origin);
  expect(observed.cache).toBe('memoryStorage');
  expect(observed.hash).toBe('#code=synthetic-code&state=fixture-state');
  expect(observed.bearer).toBe('Bearer synthetic-memory-only');
  expect(observed.calls).toBe(1);
  expect(observed.local).toBe(0); expect(observed.session).toBe(0);
  expect(popup.isClosed()).toBe(true);
});

test('real Fake backend indexes multiple original files and confirms bulk deletion on dedicated page', async ({ page }) => {
  test.skip(process.env.VOICE_ASSISTANT_KNOWLEDGE_E2E !== '1', 'Requires real local Fake knowledge endpoints, never Azure.');
  const suffix = Date.now();
  const names = [`Original-trip-${suffix}.md`, `Original-glossary-${suffix}.txt`, `Original-code-${suffix}.ts`];
  await page.goto('/materials.html');
  await expect(page.locator('#material-files')).toBeEnabled();
  await page.locator('#material-files').setInputFiles(names.map((name, index) => ({
    name, mimeType: 'text/plain', buffer: Buffer.from(`Wholly original fictional Lumen preparation fixture ${index}. No private meeting data.`),
  })));
  await expect(page.locator('#materials-panel')).toContainText('3 files ready');
  await page.locator('#materials-upload').click();
  await expect(page.locator('#materials-upload-summary')).toContainText('3 stored');
  for (const name of names) {
    await expect(page.locator('#materials-documents')).toContainText(name);
    await page.getByRole('checkbox', { name: `Select ${name}`, exact: true }).check();
  }
  await expect(page.locator('#materials-delete-selected')).toHaveText('Delete selected (3)');
  page.once('dialog', dialog => dialog.accept());
  await page.locator('#materials-delete-selected').click();
  for (const name of names) await expect(page.locator('#materials-documents')).not.toContainText(name);
});
