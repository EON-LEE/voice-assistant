"""Minimal standard-library WebSocket client (RFC 6455) on asyncio, sufficient for the meeting protocol.

Client frames are masked; server frames must not be. Pings are answered, unsolicited pongs (ASP.NET Core keep-alive)
are ignored, fragmented messages are reassembled and a close frame is echoed. No extensions are negotiated.
"""

from __future__ import annotations

import asyncio
import base64
import hashlib
import os
import ssl
import struct
import urllib.parse

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
OP_CONTINUATION, OP_TEXT, OP_BINARY, OP_CLOSE, OP_PING, OP_PONG = 0x0, 0x1, 0x2, 0x8, 0x9, 0xA
MAX_MESSAGE = 1 << 20
USER_AGENT = "voice-assistant-quality-eval/1"


class WebSocketError(Exception):
    """Transport or framing failure."""


class HandshakeError(WebSocketError):
    def __init__(self, status: int):
        super().__init__(f"WebSocket upgrade was rejected with HTTP {status}.")
        self.status = status


class ConnectionClosed(WebSocketError):
    def __init__(self, code: int | None):
        super().__init__(f"WebSocket closed ({code}).")
        self.code = code


def accept_key(key: str) -> str:
    return base64.b64encode(hashlib.sha1((key + GUID).encode("ascii")).digest()).decode("ascii")


def apply_mask(data: bytes, key: bytes) -> bytes:
    if not data:
        return b""
    repeated = (key * (len(data) // 4 + 1))[:len(data)]
    return (int.from_bytes(data, "little") ^ int.from_bytes(repeated, "little")).to_bytes(len(data), "little")


def encode_frame(opcode: int, payload: bytes, *, mask: bool, fin: bool = True) -> bytes:
    first = (0x80 if fin else 0) | opcode
    mask_bit = 0x80 if mask else 0
    length = len(payload)
    if length < 126:
        header = struct.pack("!BB", first, mask_bit | length)
    elif length < 65536:
        header = struct.pack("!BBH", first, mask_bit | 126, length)
    else:
        header = struct.pack("!BBQ", first, mask_bit | 127, length)
    if not mask:
        return header + payload
    key = os.urandom(4)
    return header + key + apply_mask(payload, key)


async def read_frame(reader: asyncio.StreamReader, *, expect_masked: bool,
                     max_size: int = MAX_MESSAGE) -> tuple[bool, int, bytes]:
    head = await reader.readexactly(2)
    if head[0] & 0x70:
        raise WebSocketError("Reserved frame bits are set without a negotiated extension.")
    fin, opcode = bool(head[0] & 0x80), head[0] & 0x0F
    masked, length = bool(head[1] & 0x80), head[1] & 0x7F
    if masked != expect_masked:
        raise WebSocketError("Unexpected frame masking.")
    if length == 126:
        length = struct.unpack("!H", await reader.readexactly(2))[0]
    elif length == 127:
        length = struct.unpack("!Q", await reader.readexactly(8))[0]
    if length > max_size:
        raise WebSocketError("Frame exceeds the size limit.")
    key = await reader.readexactly(4) if masked else b""
    payload = await reader.readexactly(length)
    if opcode >= 0x8 and (not fin or length > 125):
        raise WebSocketError("Invalid control frame.")
    return fin, opcode, apply_mask(payload, key) if masked else payload


class WebSocketClient:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self._reader = reader
        self._writer = writer
        self._send_lock = asyncio.Lock()
        self.closed = False
        self.close_code: int | None = None

    @classmethod
    async def connect(cls, url: str, *, origin: str, timeout: float = 15.0) -> "WebSocketClient":
        parts = urllib.parse.urlsplit(url)
        if parts.scheme not in ("ws", "wss") or not parts.hostname:
            raise ValueError("WebSocket URL must be ws:// or wss:// with a host.")
        secure = parts.scheme == "wss"
        host = parts.hostname
        port = parts.port or (443 if secure else 80)
        context = ssl.create_default_context() if secure else None
        reader, writer = await asyncio.wait_for(
            asyncio.open_connection(host, port, ssl=context, server_hostname=host if secure else None,
                                    limit=MAX_MESSAGE + 1024), timeout)
        try:
            key = base64.b64encode(os.urandom(16)).decode("ascii")
            host_header = f"[{host}]" if ":" in host else host
            if parts.port:
                host_header += f":{parts.port}"
            path = (parts.path or "/") + (f"?{parts.query}" if parts.query else "")
            request = (f"GET {path} HTTP/1.1\r\nHost: {host_header}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                       f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: {origin}\r\n"
                       f"User-Agent: {USER_AGENT}\r\n\r\n")
            writer.write(request.encode("ascii"))
            await writer.drain()
            head = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), timeout)
            lines = head.decode("iso-8859-1").split("\r\n")
            status_parts = lines[0].split(" ", 2)
            status = int(status_parts[1]) if len(status_parts) > 1 and status_parts[1].isdigit() else 0
            if status != 101:
                raise HandshakeError(status)
            headers = {}
            for line in lines[1:]:
                if ":" in line:
                    name, value = line.split(":", 1)
                    headers[name.strip().lower()] = value.strip()
            if headers.get("upgrade", "").lower() != "websocket" or headers.get("sec-websocket-accept") != accept_key(key):
                raise WebSocketError("Invalid WebSocket upgrade response.")
        except BaseException:
            writer.close()
            try:
                await asyncio.wait_for(writer.wait_closed(), 2)
            except (OSError, RuntimeError, asyncio.TimeoutError, asyncio.CancelledError, ssl.SSLError):
                pass
            raise
        return cls(reader, writer)

    async def _send(self, opcode: int, payload: bytes) -> None:
        if self.closed and opcode != OP_CLOSE:
            raise ConnectionClosed(self.close_code)
        frame = encode_frame(opcode, payload, mask=True)
        async with self._send_lock:
            self._writer.write(frame)
            await self._writer.drain()

    async def send_text(self, text: str) -> None:
        await self._send(OP_TEXT, text.encode("utf-8"))

    async def send_binary(self, data: bytes) -> None:
        await self._send(OP_BINARY, data)

    async def receive(self) -> tuple[int, bytes]:
        """Return the next complete data message as (opcode, payload); raises ConnectionClosed on close."""
        opcode: int | None = None
        parts: list[bytes] = []
        size = 0
        while True:
            fin, frame_opcode, payload = await read_frame(self._reader, expect_masked=False)
            if frame_opcode == OP_PING:
                await self._send(OP_PONG, payload)
                continue
            if frame_opcode == OP_PONG:
                continue
            if frame_opcode == OP_CLOSE:
                code = struct.unpack("!H", payload[:2])[0] if len(payload) >= 2 else 1005
                self.close_code = code
                if not self.closed:
                    self.closed = True
                    try:
                        await self._send(OP_CLOSE, payload[:2])
                    except (OSError, WebSocketError, RuntimeError):
                        pass
                raise ConnectionClosed(code)
            if frame_opcode == OP_CONTINUATION:
                if opcode is None:
                    raise WebSocketError("Continuation frame without a message.")
            elif frame_opcode in (OP_TEXT, OP_BINARY):
                if opcode is not None:
                    raise WebSocketError("A new message started before the previous one finished.")
                opcode = frame_opcode
            else:
                raise WebSocketError(f"Unknown opcode {frame_opcode}.")
            size += len(payload)
            if size > MAX_MESSAGE:
                raise WebSocketError("Message exceeds the size limit.")
            parts.append(payload)
            if fin:
                return opcode, b"".join(parts)

    async def close(self, code: int = 1000, timeout: float = 3.0) -> None:
        if not self.closed:
            self.closed = True
            try:
                await asyncio.wait_for(self._send(OP_CLOSE, struct.pack("!H", code)), timeout)
            except (OSError, WebSocketError, RuntimeError, asyncio.TimeoutError):
                pass
        self._writer.close()
        try:
            await asyncio.wait_for(self._writer.wait_closed(), timeout)
        except (OSError, RuntimeError, asyncio.TimeoutError, ssl.SSLError):
            pass
