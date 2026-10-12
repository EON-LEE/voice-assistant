export default async function waitForApi() {
  if (process.env.VOICE_ASSISTANT_BACKEND_E2E !== '1' ||
      process.env.VOICE_ASSISTANT_API_EXTERNAL !== '1') return;
  const base = new URL(process.env.VOICE_ASSISTANT_WEB_URL ?? 'http://127.0.0.1:5173');
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname)) {
    throw new Error('External API test setup only permits loopback services.');
  }
  const deadline = Date.now() + 30_000;
  let lastFailure = 'No response';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(new URL('/health/live', base), {
        signal: AbortSignal.timeout(2_000)
      });
      if (response.ok && (await response.json()).status === 'live') return;
      lastFailure = `Unexpected health response (${response.status})`;
    } catch (error) {
      lastFailure = error instanceof Error ? error.message : String(error);
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Local API did not become ready within 30 seconds: ${lastFailure}`);
}
