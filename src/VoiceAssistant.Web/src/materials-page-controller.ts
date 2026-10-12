import type { BrowserAuth, ClientConfig } from "./auth.js";
import { MaterialsPanel } from "./materials-panel.js";

export type MaterialsAuth = Pick<BrowserAuth, "initialize" | "signIn" | "signedIn" | "knowledgeRequest">;

export function mountMaterialsPage(auth: MaterialsAuth): void {
  const element = <T extends HTMLElement>(id: string): T => {
    const found = document.getElementById(id);
    if (!found) throw new Error(`Missing materials page element: ${id}`);
    return found as T;
  };
  const signIn = element<HTMLButtonElement>("materials-signin");
  const reconnect = element<HTMLButtonElement>("materials-reconnect");
  const status = element("materials-auth-status");
  let config: ClientConfig | undefined;
  let busy = false;
  const panel = new MaterialsPanel(element("materials-host"), auth, updateAccess);

  function updateAccess(): void {
    const enabled = !!config && auth.signedIn;
    signIn.disabled = busy || !config || config.mode === "Fake";
    reconnect.disabled = busy;
    const message = !config ? "Service configuration unavailable. Reload it to try again."
      : config.mode === "Fake" ? "LOCAL FAKE: materials are held by the local test service, not Azure."
      : auth.signedIn ? "Signed in in this tab. Indexed materials are private to your account."
      : "Sign in in this tab to upload or manage your meeting materials.";
    status.textContent = message;
    panel.setAccess(enabled, message);
  }
  async function initialize(): Promise<void> {
    busy = true; updateAccess(); status.textContent = "Loading service configuration…";
    try { config = await auth.initialize(); }
    catch { config = undefined; }
    finally { busy = false; updateAccess(); }
    if (config && auth.signedIn) await panel.refresh();
  }
  signIn.addEventListener("click", () => {
    if (busy || !config) return;
    busy = true; updateAccess();
    // Call within the click, after initialize has finished: MSAL must open the popup under user activation.
    void auth.signIn().then(() => {
      busy = false; updateAccess(); return panel.refresh();
    }).catch(() => {
      busy = false; updateAccess();
      status.textContent = "Sign-in did not complete. Allow the sign-in popup and try again. Nothing was uploaded.";
    });
  });
  reconnect.addEventListener("click", () => { if (!busy) void initialize(); });
  void initialize();
}
