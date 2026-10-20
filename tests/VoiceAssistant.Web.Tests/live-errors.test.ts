import { test } from "node:test";
import assert from "node:assert/strict";
import { CaptureError, type CaptureErrorKind, type CaptureSourceKind } from "../../src/VoiceAssistant.Web/src/capture.js";
import { CodedError } from "../../src/VoiceAssistant.Web/src/errors.js";
import { actionLabels, captureMessage, classifyCapture, classifyError, classifyServerError, liveHost, liveMessage, reconnectText,
  type Disposition, type RecoveryAction } from "../../src/VoiceAssistant.Web/src/live-errors.js";

const hangul = /[\uac00-\ud7a3]/u;

test("Every server error code maps to transient, rollover, notice or fatal with Korean text, whatever its English message", () => {
  const table: [code: string, retryable: boolean, disposition: Disposition, action?: RecoveryAction][] = [
    ["session_time_limit", false, "rollover"],
    ["session_superseded", false, "fatal", "restart"],
    ["grounding_unavailable", true, "notice"],
    ["no_transcript", true, "notice"],
    ["model_busy", true, "notice"],
    ["response_timeout", true, "notice"],
    ["response_unavailable", true, "notice"],
    ["empty_response", true, "notice"],
    ["response_limit", true, "notice"],
    ["transcribe_only", false, "notice"],
    ["idle_timeout", true, "transient"],
    ["session_ended", true, "transient"],
    ["session_overloaded", true, "transient"],
    ["startup_timeout", true, "transient"],
    ["provider_unavailable", true, "transient"],
    ["speech_unavailable", true, "transient"],
    ["transcript_limit", true, "transient"],
    ["response_overload", true, "transient"],
    ["command_limit", true, "transient"],
    ["invalid_start", true, "fatal", "restart"],
    ["invalid_audio", true, "fatal", "restart"],
    ["invalid_command", true, "fatal", "restart"],
    ["message_limit", true, "fatal", "restart"],
    ["a_future_notice", true, "notice"],
    ["a_future_failure", false, "fatal", "restart"],
  ];
  for (const [code, retryable, disposition, action] of table) {
    const classified = classifyServerError(code, retryable);
    assert.equal(classified.code, code);
    assert.equal(classified.disposition, disposition, code);
    assert.equal(classified.action, action, code);
    const text = liveMessage(classified, "browser");
    if (code === "grounding_unavailable") assert.equal(text, "", "a search failure is a label on its reply, not an error line");
    else assert.match(text, hangul, code);
    assert.doesNotMatch(text, /세션 종료/, code);
  }
  assert.equal(classifyServerError("session_time_limit", true).disposition, "rollover", "known codes ignore the retryable flag");
  assert.equal(classifyServerError("grounding_unavailable", false).disposition, "notice");
});

test("Page errors classify by type and code, never by message wording", () => {
  const table: [error: unknown, code: string, disposition: Disposition, action?: RecoveryAction][] = [
    [new CodedError("connect_timeout", "x"), "connect_timeout", "transient"],
    [new CodedError("handshake_timeout", "x"), "handshake_timeout", "transient"],
    [new CodedError("socket_error", "x"), "socket_error", "transient"],
    [new CodedError("socket_closed", "x"), "socket_closed", "transient"],
    [new CodedError("server_closed", "x"), "server_closed", "transient"],
    [new CodedError("socket_not_open", "x"), "socket_not_open", "transient"],
    [new CodedError("upload_backpressure", "x"), "upload_backpressure", "transient"],
    [new CodedError("ticket_unavailable", "Meeting authorization failed (503)."), "ticket_unavailable", "transient"],
    [new CodedError("ticket_invalid", "x"), "ticket_invalid", "transient"],
    [new CodedError("config_unavailable", "x"), "config_unavailable", "transient"],
    [new CodedError("signin_required", "Meeting authorization failed (401)."), "signin_required", "fatal", "signin"],
    [new CodedError("config_invalid", "x"), "config_invalid", "fatal"],
    [new CodedError("invalid_options", "x"), "invalid_options", "fatal", "restart"],
    [new CodedError("protocol", "x"), "protocol", "fatal", "restart"],
    [new CodedError("bad_audio", "x"), "bad_audio", "fatal", "restart"],
    [new TypeError("Failed to fetch"), "network", "transient"],
    [new DOMException("timed out", "TimeoutError"), "network", "transient"],
    // The message that used to be read as a "disconnect" no longer decides anything: only the type does.
    [new Error("Sharing ended or the source disconnected. All capture has stopped."), "unknown", "transient"],
    [new CaptureError("ended", "display", "Sharing ended or the source disconnected. All capture has stopped."), "capture_ended", "fatal", "reshare"],
    [new CaptureError("muted", "display", "Shared audio became unavailable."), "capture_muted", "notice"],
  ];
  for (const [error, code, disposition, action] of table) {
    const classified = classifyError(error);
    assert.equal(classified.code, code, String(error));
    assert.equal(classified.disposition, disposition, String(error));
    assert.equal(classified.action, action, String(error));
    assert.match(liveMessage(classified, "app"), hangul, String(error));
  }
});

test("Capture problems have their own kind, recovery step and host-specific Korean steps", () => {
  const kinds: CaptureErrorKind[] = ["cancelled", "no-audio", "ended", "muted", "silent", "unsupported", "permission"];
  for (const source of ["display", "microphone"] as CaptureSourceKind[]) {
    for (const kind of kinds) {
      const classified = classifyCapture(kind, source);
      const expected = kind === "muted" || kind === "silent" ? "notice" : "fatal";
      assert.equal(classified.disposition, expected, `${source} ${kind}`);
      const action = expected === "notice" || kind === "unsupported" ? undefined : source === "display" ? "reshare" : "restart";
      assert.equal(classified.action, action, `${source} ${kind}`);
      for (const host of ["app", "browser"] as const) {
        const text = liveMessage(classified, host);
        assert.match(text, hangul, `${source} ${kind} ${host}`);
        assert.equal(text, captureMessage(kind, source, host));
        if (action) assert.ok(text.includes(actionLabels[action]), `${source} ${kind} names its button`);
      }
    }
  }
  const app = captureMessage("no-audio", "display", "app"), browser = captureMessage("no-audio", "display", "browser");
  assert.match(app, /'전체 화면'을 선택하고 '시스템 오디오 공유'를 체크/);
  assert.match(app, /창\(Window\) 공유에는 소리가 포함되지 않습니다/);
  assert.doesNotMatch(app, /탭 오디오/, "WebView2 has no tabs to share");
  assert.match(browser, /Teams 웹 탭/);
  assert.match(browser, /'탭 오디오도 공유'/);
  assert.match(browser, /'전체 화면'을 고르고 '시스템 오디오 공유'/);
  assert.match(captureMessage("silent", "display", "app"), /^소리가 들어오지 않습니다/);
});

test("The host is LiveCoach.exe only when the WebView2 bridge exists", () => {
  assert.equal(liveHost({ chrome: { webview: { postMessage() {} } } }), "app");
  assert.equal(liveHost({ chrome: {} }), "browser");
  assert.equal(liveHost({}), "browser");
});

test("The reconnect line counts attempts, and waiting offline says so instead", () => {
  assert.equal(reconnectText(3, 20, false), "연결이 끊겨 자동 재연결 중 (3/20) · 대화와 추천은 유지됩니다");
  assert.equal(reconnectText(0, 20, false), "연결이 끊겨 자동 재연결 중 · 대화와 추천은 유지됩니다", "no count before the first attempt");
  assert.match(reconnectText(0, 20, true), /네트워크 연결을 기다리는 중/);
  assert.deepEqual(actionLabels, { reshare: "다시 공유", restart: "다시 시작", signin: "로그인", start: "라이브 시작" });
});
