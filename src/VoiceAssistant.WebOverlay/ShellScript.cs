using System.Globalization;
using System.Text.Json;

namespace VoiceAssistant.WebOverlay;

/// <summary>Script injected into the hosted page: see-through background, drag handle and host-only menu items.</summary>
internal static class ShellScript
{
    public static string Build(string origin, double opacity) => """
        (() => {
          if (location.origin !== __ORIGIN__ || !window.chrome?.webview) return;
          const post = message => window.chrome.webview.postMessage(message);
          const style = document.createElement("style");
          const css = alpha => `
            html, body, body.live-body { background: transparent !important; }
            .live-stage { padding: 0 !important; min-height: 100vh !important; }
            .live-panel { width: 100vw !important; max-width: 100vw !important; height: 100vh !important;
              background: rgba(20, 29, 43, ${alpha}) !important; }
            .live-status { cursor: move; user-select: none; }
            .live-answers, .live-history { scrollbar-width: thin; scrollbar-color: rgba(160, 176, 200, .45) transparent; }
            [data-action="float"] { display: none !important; }`;
          window.__liveShell = { setOpacity: alpha => { style.textContent = css(alpha); } };
          window.__liveShell.setOpacity(__OPACITY__);
          const install = () => {
            document.head.append(style);
            const version = document.querySelector(".live-version");
            if (version) version.textContent = "APP · WEB v2";
            document.querySelector(".live-status")?.addEventListener("mousedown", event => {
              if (event.button === 0 && !(event.target instanceof HTMLButtonElement)) { event.preventDefault(); post("drag"); }
            });
            const menu = document.getElementById("live-menu");
            if (!menu || menu.querySelector("[data-shell]")) return;
            const add = (label, message) => {
              const button = document.createElement("button");
              button.type = "button"; button.setAttribute("role", "menuitem"); button.dataset.shell = message;
              button.textContent = label;
              button.addEventListener("click", () => { menu.hidden = true; post(message); });
              menu.append(button);
            };
            add("투명도 바꾸기", "opacity");
            add("창 최소화", "minimize");
            add("프로그램 종료", "exit");
          };
          if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", install, { once: true });
          else install();
        })();
        """
        .Replace("__ORIGIN__", JsonSerializer.Serialize(origin))
        .Replace("__OPACITY__", opacity.ToString(CultureInfo.InvariantCulture));
}
