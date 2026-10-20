import { defineConfig } from '@playwright/test';
import { join } from 'node:path';

const baseURL = process.env.VOICE_ASSISTANT_WEB_URL ?? 'http://127.0.0.1:5173';
const endpoint = new URL(baseURL);
if (!['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)) {
  throw new Error('Browser automation must target a local test instance, not a real meeting service.');
}

const servers = [];
if (process.env.VOICE_ASSISTANT_BACKEND_E2E === '1' &&
    process.env.VOICE_ASSISTANT_API_EXTERNAL !== '1') {
  servers.push({
    command: `dotnet "${join('bin', 'Release', 'net10.0', 'VoiceAssistant.Api.dll')}"`,
    cwd: '../../src/VoiceAssistant.Api',
    url: 'http://localhost:5080/health/live',
    reuseExistingServer: false,
    timeout: 60_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      Provider__Mode: 'Fake',
      ASPNETCORE_URLS: 'http://localhost:5080'
    }
  });
}
if (!process.env.VOICE_ASSISTANT_WEB_URL) {
  servers.push({
    command: `node "${join('node_modules', 'vite', 'bin', 'vite.js')}" --host 127.0.0.1 --port 5173 --strictPort`,
    cwd: '../../src/VoiceAssistant.Web',
    url: baseURL,
    reuseExistingServer: false,
    timeout: 60_000
  });
}

export default defineConfig({
  globalSetup: './wait-for-api.js',
  testDir: './specs',
  timeout: 30_000,
  // Serial runs of the whole suite (including the 20-restart media case) take several minutes on a busy machine;
  // a short global cap aborted the last specs as "did not run".
  globalTimeout: 900_000,
  workers: 1,
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  // One retry on CI only, for runner-level faults (a page.goto once failed with net::ERR_NO_BUFFER_SPACE). A test that
  // passes on its retry is still reported as "flaky" by the list and html reporters below; local runs never retry.
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    browserName: 'chromium',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  webServer: servers
});
