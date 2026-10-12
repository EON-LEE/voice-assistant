import { describe, expect, it } from "vitest";
import { BrowserAuth, type AuthDependencies } from "../../../src/VoiceAssistant.Web/src/auth.js";

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return {
    get length() { return data.size; }, clear: () => data.clear(), key: index => [...data.keys()][index] ?? null,
    getItem: key => data.get(key) ?? null, setItem: (key, value) => { data.set(key, value); }, removeItem: key => { data.delete(key); },
  };
}
const json = (value: unknown, status = 200): Response =>
  new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });

function setup(storage = memoryStorage(), answers: ({ username: string; password: string } | null)[] = []) {
  const calls: { url: string; auth: string | null; body?: string }[] = [];
  const prompts: string[] = [];
  const deps: AuthDependencies = {
    location: { hostname: "voice.example", protocol: "https:", origin: "https://voice.example" },
    createClient: () => { throw new Error("MSAL must not be used in demo-login mode."); },
    storage,
    promptDemo: async (error, username) => { prompts.push(`${error}|${username}`); return answers.shift() ?? null; },
    fetch: async (input, init) => {
      const url = String(input);
      calls.push({ url, auth: new Headers(init?.headers).get("Authorization"), body: init?.body as string | undefined });
      if (url === "/api/client-config")
        return json({ clientId: "00000000-0000-0000-0000-000000000000", authority: "https://login.example/t", scope: "api://x/Meeting.Access",
          mode: "Azure", login: "demo", webSocketPath: "/api/meeting" });
      if (url === "/api/demo/login") {
        const body = JSON.parse(init!.body as string) as { password: string };
        return body.password === "test"
          ? json({ token: "demo-token", username: "test", expiresAt: new Date(Date.now() + 86400000).toISOString() })
          : json({ error: "invalid_login" }, 401);
      }
      if (url === "/api/session/ticket") return json({ ticket: "t".repeat(43), expiresAt: new Date(Date.now() + 30000).toISOString() });
      if (url === "/api/assist/enrich") return json({ korean: "안녕", pronunciation: null });
      throw new Error(`unexpected ${url}`);
    },
  };
  return { auth: new BrowserAuth(deps), calls, prompts, storage };
}

describe("demo login", () => {
  it("retries wrong passwords, stores the token and uses it for tickets and coach calls", async () => {
    const { auth, calls, prompts, storage } = setup(undefined, [{ username: "test", password: "bad" }, { username: "test", password: "test" }]);
    await auth.initialize();
    expect(auth.isDemo).toBe(true);
    expect(await auth.verify()).toBe(false);
    await auth.signIn();
    expect(prompts).toEqual(["|", "아이디 또는 비밀번호가 올바르지 않습니다.|test"]);
    expect(auth.signedIn).toBe(true);
    expect(auth.accountName).toBe("test");
    const url = await auth.endpoint(false);
    expect(url.href).toBe(`wss://voice.example/api/meeting?ticket=${"t".repeat(43)}`);
    await auth.coachRequest("/api/assist/enrich", { method: "POST", body: "{}" });
    expect(calls.filter(call => call.url !== "/api/demo/login" && call.url !== "/api/client-config").map(call => call.auth))
      .toEqual(["Bearer demo-token", "Bearer demo-token"]);
    // A new page load reuses the stored session without asking again.
    const again = setup(storage);
    await again.auth.initialize();
    expect(await again.auth.verify()).toBe(true);
    expect(again.prompts).toEqual([]);
  });

  it("cancelling the dialog throws and keeps the user signed out", async () => {
    const { auth } = setup(undefined, [null]);
    await auth.initialize();
    await expect(auth.signIn()).rejects.toThrow("cancelled");
    expect(auth.signedIn).toBe(false);
  });

  it("drops an expired stored session", async () => {
    const storage = memoryStorage();
    storage.setItem("live-coach-demo-session", JSON.stringify({ token: "old", username: "test", expiresAt: Date.now() - 1000 }));
    const { auth } = setup(storage);
    await auth.initialize();
    expect(auth.signedIn).toBe(false);
    expect(storage.getItem("live-coach-demo-session")).toBeNull();
  });
});
