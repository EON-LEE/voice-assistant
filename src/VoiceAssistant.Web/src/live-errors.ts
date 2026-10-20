import { CaptureError, type CaptureErrorKind, type CaptureSourceKind } from "./capture.js";
import { CodedError } from "./errors.js";

/**
 * What a failure means for a running live session:
 * - transient: reconnect the server connection; the audio source keeps capturing
 * - rollover: open the next session at once (the server's session time limit), with no message
 * - notice: the session continues; show a short note
 * - fatal: stop, and offer the recovery `action` when one exists
 */
export type Disposition = "transient" | "rollover" | "notice" | "fatal";
export type RecoveryAction = "reshare" | "restart" | "signin";
export interface Classified {
  code: string;
  disposition: Disposition;
  action?: RecoveryAction;
  capture?: { kind: CaptureErrorKind; source: CaptureSourceKind };
}
/** Where the page runs: inside LiveCoach.exe (WebView2) or a regular browser tab. */
export type LiveHost = "app" | "browser";

type Rule = Pick<Classified, "disposition" | "action">;
const transient: Rule = { disposition: "transient" };
const notice: Rule = { disposition: "notice" };
const refresh: Rule = { disposition: "fatal", action: "restart" };

/** Server `error` codes (see contracts/websocket-v1.md). Unknown codes follow the server's `retryable` flag. */
export const serverCodes: Readonly<Record<string, Rule>> = {
  session_time_limit: { disposition: "rollover" },
  session_superseded: refresh,
  grounding_unavailable: notice,
  no_transcript: notice,
  model_busy: notice,
  response_timeout: notice,
  response_unavailable: notice,
  empty_response: notice,
  response_limit: notice,
  transcribe_only: notice,
  idle_timeout: transient,
  session_ended: transient,
  session_overloaded: transient,
  startup_timeout: transient,
  provider_unavailable: transient,
  speech_unavailable: transient,
  transcript_limit: transient,
  response_overload: transient,
  command_limit: transient,
  invalid_start: refresh,
  invalid_audio: refresh,
  invalid_command: refresh,
  message_limit: refresh,
};

/** Codes the page itself raises (transport, sign-in, ticket and configuration). */
export const localCodes: Readonly<Record<string, Rule>> = {
  connect_timeout: transient,
  handshake_timeout: transient,
  socket_error: transient,
  socket_closed: transient,
  server_closed: transient,
  socket_not_open: transient,
  upload_backpressure: transient,
  ticket_unavailable: transient,
  ticket_invalid: transient,
  config_unavailable: transient,
  cancelled: transient,
  network: transient,
  signin_required: { disposition: "fatal", action: "signin" },
  config_invalid: { disposition: "fatal" },
  invalid_options: refresh,
  protocol: refresh,
  bad_audio: refresh,
  reconnect_exhausted: refresh,
};

export function classifyServerError(code: string, retryable: boolean): Classified {
  const rule = Object.hasOwn(serverCodes, code) ? serverCodes[code] : undefined;
  if (rule) return { code, ...rule };
  return retryable ? { code, ...notice } : { code, ...refresh };
}

export function classifyCapture(kind: CaptureErrorKind, source: CaptureSourceKind): Classified {
  const capture = { kind, source };
  const code = `capture_${kind}`;
  if (kind === "muted" || kind === "silent") return { code, disposition: "notice", capture };
  if (kind === "unsupported") return { code, disposition: "fatal", capture };
  return { code, disposition: "fatal", action: source === "display" ? "reshare" : "restart", capture };
}

/** Classifies by type and code only, never by the wording of a message. Unknown failures are retried. */
export function classifyError(error: unknown): Classified {
  if (error instanceof CaptureError) return classifyCapture(error.kind, error.source);
  if (error instanceof CodedError) {
    const rule = Object.hasOwn(localCodes, error.code) ? localCodes[error.code] : undefined;
    if (rule) return { code: error.code, ...rule };
  }
  // fetch() rejects with TypeError when the network is down; timeouts and aborts are DOMExceptions.
  if (error instanceof TypeError) return { code: "network", ...transient };
  const name = typeof error === "object" && error !== null && "name" in error && typeof error.name === "string" ? error.name : "";
  if (name === "TimeoutError" || name === "AbortError" || name === "NetworkError") return { code: "network", ...transient };
  return { code: "unknown", ...transient };
}

export function liveHost(scope: object = globalThis): LiveHost {
  const chrome = (scope as { chrome?: { webview?: unknown } }).chrome;
  return chrome?.webview ? "app" : "browser";
}

export const actionLabels: Readonly<Record<RecoveryAction | "start", string>> = {
  reshare: "다시 공유", restart: "다시 시작", signin: "로그인", start: "라이브 시작",
};

const displaySteps: Readonly<Record<LiveHost, string>> = {
  app: "'전체 화면'을 선택하고 '시스템 오디오 공유'를 체크하세요. 창(Window) 공유에는 소리가 포함되지 않습니다.",
  browser: "Teams 웹 탭을 고르고 '탭 오디오도 공유'를 켜거나, '전체 화면'을 고르고 '시스템 오디오 공유'를 체크하세요. 창(Window) 공유에는 소리가 포함되지 않습니다.",
};

/** The Korean message for a capture problem, with the steps for this host. */
export function captureMessage(kind: CaptureErrorKind, source: CaptureSourceKind, host: LiveHost): string {
  if (source === "display") {
    switch (kind) {
      case "cancelled": return `화면 공유가 시작되지 않았습니다. '다시 공유'를 눌러 공유 창을 다시 여세요. ${displaySteps[host]}`;
      case "no-audio": return `공유한 화면에 소리가 없습니다. '다시 공유'를 누른 뒤 ${displaySteps[host]}`;
      case "ended": return "화면 공유가 중지되었습니다. '다시 공유'를 누르면 대화를 이어서 듣습니다.";
      case "muted": return "공유한 소리가 잠시 끊겼습니다. 소리가 돌아오면 자동으로 이어집니다.";
      case "silent": return host === "app"
        ? "소리가 들어오지 않습니다 (15초). 회의 소리가 Windows 기본 출력 장치로 나오는지, '전체 화면' + '시스템 오디오 공유'로 공유했는지 확인하세요."
        : "소리가 들어오지 않습니다 (15초). Teams 탭을 '탭 오디오도 공유'로 공유했는지, 시스템 오디오라면 회의 소리가 Windows 기본 출력 장치로 나오는지 확인하세요.";
      case "unsupported": return host === "app"
        ? "이 앱에서는 화면 오디오 공유를 시작할 수 없습니다. WebView2 런타임을 업데이트하거나 Edge/Chrome에서 live.html을 여세요."
        : "이 브라우저는 화면 오디오 공유를 지원하지 않습니다. HTTPS로 연 최신 Edge/Chrome을 사용하세요.";
      case "permission": return "화면 공유가 시스템 또는 조직 정책으로 차단되었습니다. 설정을 확인한 뒤 '다시 공유'를 누르세요.";
    }
  }
  switch (kind) {
    case "cancelled": return "마이크를 시작하지 못했습니다. '다시 시작'을 눌러 다시 시도하세요.";
    case "no-audio": return "마이크 소리를 가져올 수 없습니다. 마이크 연결과 선택한 장치, 다른 앱의 사용 여부를 확인한 뒤 '다시 시작'을 누르세요.";
    case "ended": return "마이크 연결이 끊겼습니다. 마이크를 확인하고 '다시 시작'을 누르세요.";
    case "muted": return "마이크가 시스템이나 브라우저에서 음소거되었습니다. 음소거를 해제하면 이어집니다.";
    case "silent": return "마이크 소리가 들어오지 않습니다 (15초). 마이크 음소거와 위치를 확인하세요.";
    case "unsupported": return "이 브라우저에서는 마이크를 사용할 수 없습니다. HTTPS로 연 최신 Edge/Chrome을 사용하세요.";
    case "permission": return host === "app"
      ? "마이크 권한이 거부되었습니다. Windows 설정 → 개인 정보 → 마이크에서 앱 접근을 허용한 뒤 '다시 시작'을 누르세요."
      : "마이크 권한이 거부되었습니다. 주소창의 사이트 권한에서 마이크를 허용한 뒤 '다시 시작'을 누르세요.";
  }
}

const refreshText = "앱과 서버의 통신 형식이 맞지 않습니다. 페이지를 새로고침한 뒤 '다시 시작'을 누르세요.";
const replyRetry = "우클릭 → '추천 답변 다시 생성'으로 다시 만들 수 있어요.";
/** Korean text per code. Transient codes share the reconnect line; `grounding_unavailable` is a label on the reply instead. */
const codeTexts: Readonly<Record<string, string>> = {
  session_time_limit: "세션 시간이 다 되어 새 세션으로 이어서 듣는 중입니다.",
  session_superseded: "다른 창이나 기기에서 라이브를 시작해 여기서는 멈췄습니다. 이 화면에서 계속하려면 '다시 시작'을 누르세요.",
  grounding_unavailable: "",
  no_transcript: "아직 인식된 발언이 없어 답변을 만들 수 없습니다.",
  model_busy: `요청이 많아 이번 답변을 만들지 못했습니다. ${replyRetry}`,
  response_timeout: `답변이 제한 시간 안에 끝나지 않았습니다. ${replyRetry}`,
  response_unavailable: `답변 서비스를 잠시 쓸 수 없습니다. ${replyRetry}`,
  empty_response: `빈 답변이 나왔습니다. ${replyRetry}`,
  response_limit: `답변이 너무 길어 중단되었습니다. ${replyRetry}`,
  transcribe_only: "이 세션은 받아쓰기만 합니다.",
  invalid_start: "서버가 이 세션 설정을 받지 않았습니다. 페이지를 새로고침한 뒤 '다시 시작'을 누르세요.",
  invalid_audio: refreshText,
  invalid_command: refreshText,
  message_limit: refreshText,
  protocol: refreshText,
  bad_audio: refreshText,
  signin_required: "로그인이 필요합니다. '로그인'을 누르면 오디오를 끊지 않고 이어서 연결합니다.",
  config_invalid: "서비스 설정이 올바르지 않아 시작할 수 없습니다. 관리자에게 문의하세요.",
  invalid_options: "회의 주제에 쓸 수 없는 문자가 있습니다. 우클릭 → 설정에서 주제를 고친 뒤 '다시 시작'을 누르세요.",
  reconnect_exhausted: "연결이 계속 끊깁니다. 네트워크를 확인한 뒤 '다시 시작'을 누르세요.",
  capture_failed: "오디오를 시작하지 못했습니다. 다시 시도해 주세요.",
};

/** The reconnect line shown while the connection is being restored; the audio source keeps running meanwhile. */
export function reconnectText(attempt: number, max: number, offline: boolean): string {
  return offline ? "네트워크 연결을 기다리는 중 · 연결되면 자동으로 이어집니다 (최근 4초 오디오 보관)"
    : `연결이 끊겨 자동 재연결 중${attempt > 0 ? ` (${attempt}/${max})` : ""} · 대화와 추천은 유지됩니다`;
}

/** The Korean message shown for a classified failure or notice. */
export function liveMessage(classified: Classified, host: LiveHost): string {
  if (classified.capture) return captureMessage(classified.capture.kind, classified.capture.source, host);
  const text = Object.hasOwn(codeTexts, classified.code) ? codeTexts[classified.code] : undefined;
  if (text !== undefined) return text;
  switch (classified.disposition) {
    case "transient": return "연결이 끊겨 자동으로 다시 연결하는 중입니다 · 대화와 추천은 유지됩니다";
    case "rollover": return codeTexts.session_time_limit!;
    case "notice": return `이번 답변을 처리하지 못했습니다. ${replyRetry}`;
    case "fatal": return "서버가 연결을 끝냈습니다. '다시 시작'을 눌러 이어서 들으세요.";
  }
}
