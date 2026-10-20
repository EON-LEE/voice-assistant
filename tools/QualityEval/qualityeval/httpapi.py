"""HTTP calls to a Live Coach server: client config, demo login, session ticket and the materials API.

Requests never follow redirects (a redirect could forward the bearer token elsewhere), always send the configured
Origin (the API checks it on mutations, tickets and WebSockets) and never include tokens in errors or logs.
"""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable

USER_AGENT = "voice-assistant-quality-eval/1"
LOOPBACK_HOSTS = ("localhost", "127.0.0.1", "::1")

Transport = Callable[[str, str, dict, bytes | None, float], tuple[int, bytes]]


class ApiError(RuntimeError):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status = status
        self.code = code


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D401 - urllib hook
        return None


_OPENER = urllib.request.build_opener(_NoRedirect)


def http_request(method: str, url: str, headers: dict, body: bytes | None, timeout: float) -> tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with _OPENER.open(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        try:
            return error.code, error.read() or b""
        finally:
            error.close()


def origin_of(url: str) -> str:
    parts = urllib.parse.urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.hostname:
        raise ValueError("Use an http(s) base URL with a host, for example http://localhost:5080.")
    return f"{parts.scheme}://{parts.netloc}"


def is_loopback(url: str) -> bool:
    return (urllib.parse.urlsplit(url).hostname or "") in LOOPBACK_HOSTS


class LiveCoachApi:
    def __init__(self, base_url: str, *, origin: str | None = None, transport: Transport = http_request,
                 timeout: float = 30.0):
        self.base = origin_of(base_url)
        self.origin = origin_of(origin) if origin else self.base
        self.loopback = is_loopback(self.base)
        self._transport = transport
        self._timeout = timeout

    def _call(self, method: str, path: str, *, bearer: str | None = None, payload: object = None,
              expected: tuple[int, ...] = (200,)) -> object:
        headers = {"Accept": "application/json", "Origin": self.origin, "User-Agent": USER_AGENT,
                   "Cache-Control": "no-store"}
        body = None
        if payload is not None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"
        if bearer:
            headers["Authorization"] = f"Bearer {bearer}"
        status, data = self._transport(method, self.base + path, headers, body, self._timeout)
        if status not in expected:
            code = "http_error"
            try:
                parsed = json.loads(data.decode("utf-8")) if data else {}
                if isinstance(parsed, dict) and isinstance(parsed.get("error"), str):
                    code = re.sub(r"[^a-z0-9_]", "", parsed["error"].lower())[:40] or code
            except (UnicodeDecodeError, json.JSONDecodeError):
                pass
            raise ApiError(status, code, f"{method} {path} failed with HTTP {status} ({code}).")
        if not data:
            return None
        try:
            return json.loads(data.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ApiError(status, "invalid_json", f"{method} {path} returned invalid JSON.") from error

    def client_config(self) -> dict:
        config = self._call("GET", "/api/client-config")
        if not isinstance(config, dict) or config.get("mode") not in ("Fake", "Azure") or \
                config.get("webSocketPath") != "/api/meeting":
            raise ApiError(200, "invalid_config", "The server returned an invalid client configuration.")
        return config

    def demo_login(self, username: str, password: str) -> str:
        body = self._call("POST", "/api/demo/login", payload={"username": username, "password": password})
        token = body.get("token") if isinstance(body, dict) else None
        if not isinstance(token, str) or not 20 <= len(token) <= 4096:
            raise ApiError(200, "invalid_login_response", "Demo login returned an invalid token.")
        return token

    def ticket(self, bearer: str) -> str:
        body = self._call("POST", "/api/session/ticket", bearer=bearer)
        ticket = body.get("ticket") if isinstance(body, dict) else None
        if not isinstance(ticket, str) or not re.fullmatch(r"[A-Za-z0-9_-]{20,200}", ticket):
            raise ApiError(200, "invalid_ticket", "The server returned an invalid meeting ticket.")
        return ticket

    def list_materials(self, bearer: str | None) -> list[dict]:
        body = self._call("GET", "/api/knowledge", bearer=bearer)
        documents = body.get("documents") if isinstance(body, dict) else None
        if not isinstance(documents, list):
            raise ApiError(200, "invalid_materials", "The materials list is invalid.")
        return [document for document in documents if isinstance(document, dict)]

    def upload_material(self, bearer: str | None, title: str, text: str) -> dict:
        body = self._call("POST", "/api/knowledge", bearer=bearer, payload={"title": title, "text": text},
                          expected=(201,))
        if not isinstance(body, dict) or not re.fullmatch(r"[0-9a-f]{32}", str(body.get("id", ""))):
            raise ApiError(201, "invalid_upload", "The materials upload response is invalid.")
        return body

    def delete_material(self, bearer: str | None, document_id: str) -> None:
        if not re.fullmatch(r"[0-9a-f]{32}", document_id):
            raise ValueError("Invalid materials document id.")
        self._call("DELETE", f"/api/knowledge/{document_id}", bearer=bearer, expected=(204,))

    def websocket_url(self, ticket: str | None) -> str:
        parts = urllib.parse.urlsplit(self.base)
        scheme = "wss" if parts.scheme == "https" else "ws"
        query = "" if ticket is None else "?" + urllib.parse.urlencode({"ticket": ticket})
        return f"{scheme}://{parts.netloc}/api/meeting{query}"
