import { PublicClientApplication, InteractionRequiredAuthError, type AccountInfo } from "@azure/msal-browser";
import { isLoopback } from "./transport.js";

export interface ClientConfig { clientId: string; authority: string; scope: string; mode: "Fake" | "Azure"; webSocketPath: "/api/meeting" }
export class BrowserAuth {
  private app: PublicClientApplication | undefined;
  private account: AccountInfo | undefined;
  private config: ClientConfig | undefined;
  get signedIn(): boolean { return !!this.account || this.config?.mode === "Fake"; }
  async initialize(): Promise<ClientConfig> {
    if (this.config) return this.config;
    const response = await fetch("/api/client-config", { credentials: "same-origin", cache: "no-store", signal: AbortSignal.timeout(10000) });
    if (!response.ok) throw new Error("Server authentication configuration unavailable. Live mode is disabled.");
    const config = await response.json() as ClientConfig;
    if (!["Fake", "Azure"].includes(config.mode) || config.webSocketPath !== "/api/meeting") throw new Error("Invalid server authentication configuration.");
    if (config.mode === "Fake") {
      if (!isLoopback(location.hostname)) throw new Error("Fake backend mode is only allowed on localhost.");
    } else {
      if (location.protocol !== "https:") throw new Error("Production requires HTTPS and WSS.");
      const authority = new URL(config.authority);
      if (authority.protocol !== "https:" || !/^[\da-f-]{36}$/i.test(config.clientId) || !config.scope) throw new Error("Invalid Entra configuration.");
      this.app = new PublicClientApplication({
        auth: { clientId: config.clientId, authority: config.authority, redirectUri: location.origin },
        cache: { cacheLocation: "memoryStorage", temporaryCacheLocation: "memoryStorage" },
      });
      await this.app.initialize();
    }
    this.config = config;
    return config;
  }
  async signIn(): Promise<void> {
    // initialize must already finish before the user clicks Sign in, preserving popup activation.
    if (!this.config) throw new Error("Wait for authentication configuration to load.");
    if (this.config.mode === "Fake") return;
    const result = await this.app!.loginPopup({ scopes: [this.config.scope] });
    this.account = result.account;
  }
  async endpoint(synthetic: boolean): Promise<URL> {
    const config = await this.initialize();
    const url = new URL("/api/meeting", location.origin);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    if (config.mode === "Fake") {
      if (!isLoopback(location.hostname)) throw new Error("Fake service must be loopback-only.");
      return url;
    }
    if (synthetic) throw new Error("Synthetic mode requires an explicit local Fake backend, never Azure.");
    if (!this.account || !this.app) throw new Error("Sign in before sharing audio.");
    let accessToken: string;
    try {
      accessToken = (await this.app.acquireTokenSilent({ scopes: [config.scope], account: this.account })).accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) { this.account = undefined; throw new Error("Sign-in expired. Stop and sign in again before sharing."); }
      throw error;
    }
    const response = await fetch("/api/session/ticket", {
      method: "POST", credentials: "same-origin", cache: "no-store",
      headers: { Authorization: `Bearer ${accessToken}` }, signal: AbortSignal.timeout(10000),
    });
    accessToken = "";
    if (!response.ok) throw new Error(`Meeting authorization failed (${response.status}). Sign in again.`);
    const body = await response.json() as { ticket: unknown; expiresAt: unknown };
    const expiry = typeof body.expiresAt === "string" ? Date.parse(body.expiresAt) : NaN;
    if (typeof body.ticket !== "string" || !body.ticket || body.ticket.length > 4096 ||
        !Number.isFinite(expiry) || expiry <= Date.now() || expiry > Date.now() + 60000)
      throw new Error("Server returned an invalid or expired meeting ticket.");
    url.searchParams.set("ticket", body.ticket);
    return url;
  }
}
