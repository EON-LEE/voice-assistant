import { PublicClientApplication, InteractionRequiredAuthError, type AccountInfo, type Configuration } from "@azure/msal-browser";
import { isLoopback } from "./transport.js";

export interface ClientConfig { clientId: string; authority: string; scope: string; mode: "Fake" | "Azure"; webSocketPath: "/api/meeting"; login?: "entra" | "demo" }
type AuthClient = Pick<PublicClientApplication, "initialize" | "loginPopup" | "acquireTokenSilent">;
export interface DemoCredentials { username: string; password: string }
export interface AuthDependencies {
  fetch: typeof fetch;
  location: Pick<Location, "hostname" | "protocol" | "origin">;
  createClient(config: Configuration): AuthClient;
  /** Asks the user for the shared demo account; resolves null when cancelled. */
  promptDemo?(error: string, username: string): Promise<DemoCredentials | null>;
  storage?: Pick<Storage, "getItem" | "setItem" | "removeItem">;
}
interface DemoSession { token: string; username: string; expiresAt: number }
const demoKey = "live-coach-demo-session";
export class BrowserAuth {
  private app: AuthClient | undefined;
  private account: AccountInfo | undefined;
  private demo: DemoSession | undefined;
  private config: ClientConfig | undefined;
  private initializing: Promise<ClientConfig> | undefined;
  constructor(private readonly deps: AuthDependencies = {
    fetch: (...args) => fetch(...args), location,
    createClient: config => new PublicClientApplication(config),
  }, private readonly options: { persistent?: boolean } = {}) {}
  get signedIn(): boolean { return !!this.account || !!this.demoSession() || this.config?.mode === "Fake"; }
  get isFake(): boolean { return this.config?.mode === "Fake"; }
  get isDemo(): boolean { return this.config?.login === "demo" && this.config.mode === "Azure"; }
  private get storage(): Pick<Storage, "getItem" | "setItem" | "removeItem"> | undefined {
    return this.deps.storage ?? (typeof localStorage === "undefined" ? undefined : localStorage);
  }
  private demoSession(): DemoSession | undefined {
    if (this.demo && this.demo.expiresAt - 60000 <= Date.now()) this.clearDemo();
    return this.demo;
  }
  private clearDemo(): void { this.demo = undefined; this.storage?.removeItem(demoKey); }
  async initialize(): Promise<ClientConfig> {
    if (this.config) return this.config;
    if (!this.initializing) this.initializing = this.loadConfig().finally(() => { this.initializing = undefined; });
    return this.initializing;
  }
  private async loadConfig(): Promise<ClientConfig> {
    const response = await this.deps.fetch("/api/client-config", { credentials: "same-origin", cache: "no-store", signal: AbortSignal.timeout(10000) });
    if (!response.ok) throw new Error("Server authentication configuration unavailable. Live mode is disabled.");
    const config = await response.json() as ClientConfig;
    if (!["Fake", "Azure"].includes(config.mode) || config.webSocketPath !== "/api/meeting") throw new Error("Invalid server authentication configuration.");
    if (config.mode === "Fake") {
      if (!isLoopback(this.deps.location.hostname)) throw new Error("Fake backend mode is only allowed on localhost.");
    } else {
      if (this.deps.location.protocol !== "https:") throw new Error("Production requires HTTPS and WSS.");
      if (config.login === "demo") {
        // Shared demo account: the signed token is kept in this browser so one sign-in lasts until it expires.
        try {
          const saved = JSON.parse(this.storage?.getItem(demoKey) ?? "null") as DemoSession | null;
          if (saved && typeof saved.token === "string" && typeof saved.username === "string" && Number.isFinite(saved.expiresAt))
            this.demo = saved;
        } catch { this.storage?.removeItem(demoKey); }
        this.config = config;
        this.demoSession();
        return config;
      }
      const authority = new URL(config.authority);
      if (authority.protocol !== "https:" || !/^[\da-f-]{36}$/i.test(config.clientId) || !config.scope) throw new Error("Invalid Entra configuration.");
      // Persistent pages keep the MSAL cache in this browser profile so one sign-in survives reloads.
      const storage = this.options.persistent ? "localStorage" : "memoryStorage";
      this.app = this.deps.createClient({
        auth: { clientId: config.clientId, authority: config.authority, redirectUri: this.deps.location.origin },
        cache: { cacheLocation: storage, temporaryCacheLocation: this.options.persistent ? "sessionStorage" : "memoryStorage" },
      });
      await this.app.initialize();
      if (this.options.persistent)
        this.account = (this.app as Partial<Pick<PublicClientApplication, "getAllAccounts">>).getAllAccounts?.()[0];
    }
    this.config = config;
    return config;
  }
  async signIn(): Promise<void> {
    // initialize must already finish before the user clicks Sign in, preserving popup activation.
    if (!this.config) throw new Error("Wait for authentication configuration to load.");
    if (this.config.mode === "Fake") return;
    if (this.isDemo) { await this.demoSignIn(); return; }
    const result = await this.app!.loginPopup({ scopes: [this.config.scope] });
    this.account = result.account;
  }
  private async demoSignIn(): Promise<void> {
    const prompt = this.deps.promptDemo ?? promptDemoLogin;
    let error = "", username = "";
    for (;;) {
      const credentials = await prompt(error, username);
      if (!credentials) throw new Error("Sign-in was cancelled.");
      username = credentials.username;
      const response = await this.deps.fetch("/api/demo/login", {
        method: "POST", credentials: "same-origin", cache: "no-store", redirect: "error",
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(credentials), signal: AbortSignal.timeout(15000),
      });
      if (response.status === 401) { error = "아이디 또는 비밀번호가 올바르지 않습니다."; continue; }
      if (response.status === 429) throw new Error("로그인 실패가 너무 많습니다. 10분 뒤 다시 시도하세요.");
      if (!response.ok) throw new Error(`Sign-in service unavailable (${response.status}).`);
      const body = await response.json() as { token?: unknown; username?: unknown; expiresAt?: unknown };
      const expiresAt = typeof body.expiresAt === "string" ? Date.parse(body.expiresAt) : NaN;
      if (typeof body.token !== "string" || body.token.length > 4096 || typeof body.username !== "string" || !Number.isFinite(expiresAt))
        throw new Error("Sign-in returned an invalid response.");
      this.demo = { token: body.token, username: body.username, expiresAt };
      this.storage?.setItem(demoKey, JSON.stringify(this.demo));
      return;
    }
  }
  /** Signed-in account display name, if any. */
  get accountName(): string { return this.demoSession()?.username ?? this.account?.username ?? ""; }
  /** Verifies the cached account silently; clears it when Microsoft requires interaction again. */
  async verify(): Promise<boolean> {
    if (this.config?.mode === "Fake") return true;
    if (this.isDemo) return !!this.demoSession();
    if (!this.account) return false;
    try { await this.token(); return true; }
    catch { return false; }
  }
  private rejected(): void { this.account = undefined; this.clearDemo(); }
  private async token(): Promise<string> {
    if (this.isDemo) {
      const session = this.demoSession();
      if (!session) throw new Error("Sign in before accessing meeting materials or sharing audio.");
      return session.token;
    }
    if (!this.account || !this.app || !this.config) throw new Error("Sign in before accessing meeting materials or sharing audio.");
    try {
      return (await this.app.acquireTokenSilent({ scopes: [this.config.scope], account: this.account })).accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) { this.account = undefined; throw new Error("Sign-in expired. Sign in again to continue."); }
      throw error;
    }
  }
  async knowledgeRequest(path: string, init: RequestInit = {}): Promise<Response> {
    if (!/^\/api\/knowledge(?:\/[0-9a-f]{32})?$/.test(path)) throw new Error("Invalid materials endpoint.");
    const config = await this.initialize();
    const headers = new Headers(init.headers);
    if (config.mode === "Azure") headers.set("Authorization", `Bearer ${await this.token()}`);
    else headers.delete("Authorization");
    const response = await this.deps.fetch(path, { ...init, headers, credentials: "same-origin", cache: "no-store", redirect: "error" });
    if (response.status === 401 || response.status === 403) this.rejected();
    return response;
  }
  async coachRequest(path: string, init: RequestInit): Promise<Response> {
    if (!/^\/api\/(?:assist\/(?:enrich|speak)|practice\/(?:turn|suggest|feedback|summary))$/.test(path) || init.method !== "POST")
      throw new Error("Invalid coach endpoint.");
    const config = await this.initialize();
    const headers = new Headers(init.headers);
    if (config.mode === "Azure") headers.set("Authorization", `Bearer ${await this.token()}`);
    else headers.delete("Authorization");
    const response = await this.deps.fetch(path, { ...init, headers, credentials: "same-origin", cache: "no-store", redirect: "error" });
    if (response.status === 401 || response.status === 403) this.rejected();
    return response;
  }
  async endpoint(synthetic: boolean): Promise<URL> {
    const config = await this.initialize();
    const url = new URL("/api/meeting", this.deps.location.origin);
    url.protocol = this.deps.location.protocol === "https:" ? "wss:" : "ws:";
    if (config.mode === "Fake") {
      if (!isLoopback(this.deps.location.hostname)) throw new Error("Fake service must be loopback-only.");
      return url;
    }
    if (synthetic) throw new Error("Synthetic mode requires an explicit local Fake backend, never Azure.");
    let accessToken: string;
    if (this.isDemo) accessToken = await this.token();
    else {
      if (!this.account || !this.app) throw new Error("Sign in before sharing audio.");
      try {
        accessToken = (await this.app.acquireTokenSilent({ scopes: [config.scope], account: this.account })).accessToken;
      } catch (error) {
        if (error instanceof InteractionRequiredAuthError) { this.account = undefined; throw new Error("Sign-in expired. Stop and sign in again before sharing."); }
        throw error;
      }
    }
    const response = await this.deps.fetch("/api/session/ticket", {
      method: "POST", credentials: "same-origin", cache: "no-store",
      headers: { Authorization: `Bearer ${accessToken}` }, signal: AbortSignal.timeout(10000),
    });
    accessToken = "";
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) this.rejected();
      throw new Error(`Meeting authorization failed (${response.status}). Sign in again.`);
    }
    const body = await response.json() as { ticket: unknown; expiresAt: unknown };
    const expiry = typeof body.expiresAt === "string" ? Date.parse(body.expiresAt) : NaN;
    if (typeof body.ticket !== "string" || !body.ticket || body.ticket.length > 4096 ||
        !Number.isFinite(expiry) || expiry <= Date.now() || expiry > Date.now() + 60000)
      throw new Error("Server returned an invalid or expired meeting ticket.");
    url.searchParams.set("ticket", body.ticket);
    return url;
  }
}

/** Minimal accessible sign-in dialog shared by every page in demo-login mode. */
export function promptDemoLogin(error: string, previousUsername = ""): Promise<DemoCredentials | null> {
  return new Promise(resolve => {
    const dialog = document.createElement("dialog");
    dialog.setAttribute("aria-labelledby", "demo-login-title");
    dialog.style.cssText = "width:min(340px,calc(100vw - 32px));padding:20px 22px;border:1px solid #74849b;border-radius:12px;" +
      "background:#182231;color:#edf2fa;font:14px 'Segoe UI','Malgun Gothic',system-ui,sans-serif";
    const form = document.createElement("form");
    form.method = "dialog";
    const title = document.createElement("h2");
    title.id = "demo-login-title"; title.textContent = "로그인"; title.style.cssText = "margin:0 0 12px;font-size:16px";
    const field = (label: string, type: string, autocomplete: AutoFill): HTMLInputElement => {
      const wrapper = document.createElement("label");
      wrapper.style.cssText = "display:grid;gap:4px;margin-top:10px;font-size:13px;color:#c1cfe2";
      wrapper.textContent = label;
      const input = document.createElement("input");
      input.type = type; input.required = true; input.autocomplete = autocomplete; input.maxLength = 200;
      input.style.cssText = "font:inherit;padding:7px 9px;border-radius:6px;border:1px solid #4b5c74;background:#0f1622;color:#edf2fa";
      wrapper.append(input); form.append(wrapper);
      return input;
    };
    form.append(title);
    const username = field("아이디", "text", "username");
    username.value = previousUsername;
    const password = field("비밀번호", "password", "current-password");
    const message = document.createElement("p");
    message.setAttribute("role", "alert"); message.textContent = error;
    message.style.cssText = "min-height:18px;margin:10px 0 0;font-size:12px;color:#ffb5b5";
    const actions = document.createElement("div");
    actions.style.cssText = "display:flex;justify-content:flex-end;gap:8px;margin-top:12px";
    const button = (label: string, value: string, primary: boolean): HTMLButtonElement => {
      const element = document.createElement("button");
      element.value = value; element.textContent = label;
      if (!primary) element.formNoValidate = true;
      element.style.cssText = `font:inherit;font-size:13px;padding:6px 14px;border-radius:6px;cursor:pointer;color:#edf2fa;` +
        `border:1px solid #5d7394;background:${primary ? "#2f6fd6" : "#2b3d57"}`;
      actions.append(element);
      return element;
    };
    button("취소", "cancel", false);
    button("로그인", "login", true);
    form.append(message, actions);
    dialog.append(form);
    document.body.append(dialog);
    dialog.addEventListener("close", () => {
      const value = dialog.returnValue === "login" ? { username: username.value.trim(), password: password.value } : null;
      dialog.remove();
      resolve(value);
    }, { once: true });
    dialog.showModal();
    (previousUsername ? password : username).focus();
  });
}
