"""In-process fake Live Coach server for offline tests: the HTTP endpoints plus the v1 WebSocket protocol.

Recognition is scripted: each utterance (nonzero audio followed by silence) yields the next scripted transcript as a
final shortly after its speech ends, and every final containing "?" gets a streamed reply.
"""

from __future__ import annotations

import asyncio
import base64
import json
import secrets
import threading
import urllib.parse
import uuid

from qualityeval.wsclient import (OP_BINARY, OP_CLOSE, OP_PONG, OP_PING, OP_TEXT, accept_key, encode_frame,
                                  read_frame)

OID = "11111111-2222-4333-8444-555555555555"


def b64url(value: dict) -> str:
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")


def make_token(oid: str = OID) -> str:
    return f"{b64url({'alg': 'none', 'typ': 'JWT'})}.{b64url({'oid': oid, 'scp': 'Meeting.Access'})}.signature"


def default_reply(text: str) -> tuple[str, str, str, list[dict]]:
    return "Let's confirm the goal and the next step.", "We can agree on an owner today.", "disabled", []


class FakeServer:
    def __init__(self, *, mode: str = "Azure", login: str = "demo", oid: str = OID, transcripts=None, reply=default_reply,
                 primary_done: bool = True, empty_delta_bug: bool = False, reject_websocket: int | None = None,
                 send_ping: bool = True, final_delay: float = 0.03, reply_delay: float = 0.02):
        self.mode, self.login = mode, login
        self.username, self.password = "eval-user", "eval-pass"
        self.token = make_token(oid)
        self.transcripts = transcripts or []
        self.reply = reply
        self.primary_done = primary_done
        self.empty_delta_bug = empty_delta_bug
        self.reject_websocket = reject_websocket
        self.send_ping = send_ping
        self.final_delay = final_delay
        self.reply_delay = reply_delay
        self.documents: dict[str, dict] = {}
        self.tickets: set[str] = set()
        self.starts: list[dict] = []
        self.sessions = 0
        self.pongs = 0
        self.stops = 0
        self.base_url = ""

    def __enter__(self) -> "FakeServer":
        ready = threading.Event()

        def serve() -> None:
            self._loop = asyncio.new_event_loop()
            asyncio.set_event_loop(self._loop)
            self._server = self._loop.run_until_complete(asyncio.start_server(self._handle, "127.0.0.1", 0))
            self.base_url = f"http://127.0.0.1:{self._server.sockets[0].getsockname()[1]}"
            ready.set()
            self._loop.run_forever()

        self._thread = threading.Thread(target=serve, daemon=True)
        self._thread.start()
        if not ready.wait(10):
            raise RuntimeError("Fake server did not start.")
        return self

    def __exit__(self, *exc) -> None:
        async def shutdown() -> None:
            self._server.close()
            try:
                await asyncio.wait_for(self._server.wait_closed(), 2)
            except asyncio.TimeoutError:
                pass
            pending = [task for task in asyncio.all_tasks() if task is not asyncio.current_task()]
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)

        asyncio.run_coroutine_threadsafe(shutdown(), self._loop).result(10)
        self._loop.call_soon_threadsafe(self._loop.stop)
        self._thread.join(10)
        self._loop.close()

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            head = await reader.readuntil(b"\r\n\r\n")
            lines = head.decode("iso-8859-1").split("\r\n")
            method, target, _ = lines[0].split(" ", 2)
            headers = {}
            for line in lines[1:]:
                if ":" in line:
                    name, value = line.split(":", 1)
                    headers[name.strip().lower()] = value.strip()
            body = await reader.readexactly(int(headers.get("content-length", "0") or 0))
            path, _, query = target.partition("?")
            if headers.get("upgrade", "").lower() == "websocket" and path == "/api/meeting":
                await self._websocket(reader, writer, headers, query)
                return
            status, payload = self._http(method, path, headers, body)
            data = b"" if payload is None else json.dumps(payload).encode()
            writer.write(f"HTTP/1.1 {status} Fake\r\nContent-Type: application/json\r\nContent-Length: {len(data)}\r\n"
                         f"Connection: close\r\n\r\n".encode() + data)
            await writer.drain()
        except (asyncio.IncompleteReadError, ConnectionError, asyncio.LimitOverrunError):
            pass
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except (ConnectionError, OSError):
                pass

    def _http(self, method: str, path: str, headers: dict, body: bytes):
        origin_ok = headers.get("origin") == self.base_url
        authed = self.mode == "Fake" or headers.get("authorization") == f"Bearer {self.token}"
        if path == "/api/client-config" and method == "GET":
            return 200, {"clientId": "", "authority": "", "scope": "", "mode": self.mode, "login": self.login,
                         "webSocketPath": "/api/meeting"}
        if path == "/api/demo/login" and method == "POST":
            if not origin_ok:
                return 403, None
            if json.loads(body) == {"username": self.username, "password": self.password}:
                return 200, {"token": self.token, "username": self.username, "expiresAt": "2030-01-01T00:00:00Z"}
            return 401, {"error": "invalid_login", "message": "The username or password is incorrect."}
        if path == "/api/session/ticket" and method == "POST":
            if not authed:
                return 401, None
            if not origin_ok:
                return 403, None
            ticket = secrets.token_urlsafe(32)[:43]
            self.tickets.add(ticket)
            return 200, {"ticket": ticket, "expiresAt": "2030-01-01T00:00:30Z"}
        if path == "/api/knowledge" or path.startswith("/api/knowledge/"):
            if not authed:
                return 401, None
            if method == "GET" and path == "/api/knowledge":
                return 200, {"documents": [{key: value for key, value in document.items() if key != "text"}
                                           for document in self.documents.values()]}
            if not origin_ok:
                return 403, None
            if method == "POST" and path == "/api/knowledge":
                payload = json.loads(body)
                document_id = uuid.uuid4().hex
                self.documents[document_id] = {"id": document_id, "title": payload["title"], "chunks": 1,
                                               "updatedAt": "2026-10-20T00:00:00Z", "text": payload["text"]}
                return 201, {"id": document_id, "title": payload["title"], "chunks": 1,
                             "characters": len(payload["text"]), "updatedAt": "2026-10-20T00:00:00Z"}
            if method == "DELETE":
                self.documents.pop(path.rsplit("/", 1)[-1], None)
                return 204, None
        return 404, None

    async def _websocket(self, reader, writer, headers, query) -> None:
        if self.reject_websocket:
            writer.write(f"HTTP/1.1 {self.reject_websocket} Rejected\r\nContent-Length: 0\r\n\r\n".encode())
            await writer.drain()
            return
        ticket = urllib.parse.parse_qs(query).get("ticket", [""])[0]
        if headers.get("origin") != self.base_url or (self.mode == "Azure" and ticket not in self.tickets):
            writer.write(b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n")
            await writer.drain()
            return
        self.tickets.discard(ticket)
        writer.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      f"Sec-WebSocket-Accept: {accept_key(headers['sec-websocket-key'])}\r\n\r\n").encode())
        await writer.drain()
        session_index = self.sessions
        self.sessions += 1
        script = self.transcripts[session_index] if session_index < len(self.transcripts) else []
        lock = asyncio.Lock()
        tasks: list[asyncio.Task] = []

        async def send(event: dict | None = None, opcode: int = OP_TEXT, payload: bytes = b"") -> None:
            data = json.dumps(event).encode() if event is not None else payload
            async with lock:
                writer.write(encode_frame(opcode, data, mask=False))
                await writer.drain()

        async def respond(turn_id: str, text: str) -> None:
            await asyncio.sleep(self.reply_delay)
            primary, alternative, grounding, sources = self.reply(text)
            response_id = uuid.uuid4().hex
            await send({"type": "response.started", "responseId": response_id, "turnId": turn_id})
            middle = len(primary) // 2
            for chunk in (primary[:middle], primary[middle:]):
                await asyncio.sleep(0.005)
                await send({"type": "response.delta", "responseId": response_id, "turnId": turn_id, "text": chunk})
            if self.empty_delta_bug:
                await send({"type": "response.delta", "responseId": response_id, "turnId": turn_id, "text": ""})
            if self.primary_done:
                await send({"type": "response.delta", "responseId": response_id, "turnId": turn_id, "text": "",
                            "primaryDone": True})
            await send({"type": "response.completed", "responseId": response_id, "turnId": turn_id, "text": primary,
                        "suggestions": [primary, alternative] if alternative else [primary], "sources": sources,
                        "grounding": grounding, "responseRoute": "knowledge", "retrievalPrefetched": False})

        async def finalize(index: int, turn_id: str) -> None:
            await asyncio.sleep(self.final_delay)
            text = script[index] if index < len(script) else "Could you briefly explain the next steps?"
            await send({"type": "transcript.final", "turnId": turn_id, "revision": 2, "text": text})
            if "?" in text:
                tasks.append(asyncio.create_task(respond(turn_id, text)))

        speaking, utterance, zeros, turn_id, closing = False, -1, 0, "", False
        try:
            while True:
                fin, opcode, payload = await read_frame(reader, expect_masked=True)
                if opcode == OP_CLOSE:
                    if not closing:
                        await send(opcode=OP_CLOSE, payload=payload[:2])
                    break
                if opcode == OP_PONG:
                    self.pongs += 1
                elif opcode == OP_TEXT:
                    message = json.loads(payload)
                    if message["type"] == "session.start":
                        self.starts.append(message)
                        await send({"type": "session.ready", "protocolVersion": 1, "sessionUpdate": True})
                        if self.send_ping:
                            await send(opcode=OP_PING, payload=b"ka")
                    elif message["type"] == "session.stop":
                        self.stops += 1
                        await asyncio.gather(*tasks, return_exceptions=True)
                        closing = True
                        await send(opcode=OP_CLOSE, payload=(1000).to_bytes(2, "big"))
                elif opcode == OP_BINARY:
                    if payload.count(0) == len(payload):
                        zeros += len(payload)
                        if speaking and zeros >= 640:
                            speaking = False
                            tasks.append(asyncio.create_task(finalize(utterance, turn_id)))
                    else:
                        zeros = 0
                        if not speaking:
                            speaking, utterance, turn_id = True, utterance + 1, uuid.uuid4().hex
                            await send({"type": "transcript.partial", "turnId": turn_id, "revision": 1,
                                        "text": "..."})
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        finally:
            for task in tasks:
                task.cancel()
