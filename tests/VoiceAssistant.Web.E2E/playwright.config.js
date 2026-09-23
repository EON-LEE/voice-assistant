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
    command: `dotnet "${join('bin', 'Release', 'net8.0', 'VoiceAssistant.Api.dll')}"`,
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
  globalTimeout: 300_000,
  workers: 1,
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    browserName: 'chromium',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  webServer: servers
});
