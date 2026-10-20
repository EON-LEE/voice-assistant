"""Evaluation identity: object-ID hashing and the guard that keeps real identities out of evaluation runs."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import re
from typing import Mapping

FAKE_OWNER = "00000000-0000-0000-0000-000000000001"
_GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class IdentityError(PermissionError):
    """The selected identity must not be used for an evaluation run."""


def jwt_claims(token: str) -> dict:
    """Decode (without verifying) the JWT payload; used only to read the object ID for the guard."""
    parts = token.split(".")
    if len(parts) < 2:
        return {}
    payload = parts[1] + "=" * (-len(parts[1]) % 4)
    try:
        claims = json.loads(base64.urlsafe_b64decode(payload.encode("ascii")).decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return {}
    return claims if isinstance(claims, dict) else {}


def object_id(token: str) -> str | None:
    oid = jwt_claims(token).get("oid")
    return oid.lower() if isinstance(oid, str) and _GUID.match(oid) else None


def oid_sha256(oid: str) -> str:
    return hashlib.sha256(oid.strip().lower().encode("ascii")).hexdigest()


def _hashes(value: str | None) -> set[str]:
    return {part.strip().lower() for part in (value or "").split(",") if part.strip()}


def guard(oid: str, environ: Mapping[str, str] = os.environ) -> str:
    """Return the identity hash or raise when it is forbidden or not the expected evaluation identity.

    EVAL_FORBIDDEN_OID_SHA256: comma-separated SHA-256 hashes of identities that must never run evaluations
    (for example the owner's real account). EVAL_EXPECTED_OID_SHA256: the dedicated evaluation identity's hash.
    """
    digest = oid_sha256(oid)
    if digest in _hashes(environ.get("EVAL_FORBIDDEN_OID_SHA256")):
        raise IdentityError("This identity is listed in EVAL_FORBIDDEN_OID_SHA256; use the dedicated synthetic identity.")
    expected = _hashes(environ.get("EVAL_EXPECTED_OID_SHA256"))
    if expected and digest not in expected:
        raise IdentityError("This identity does not match EVAL_EXPECTED_OID_SHA256; refusing to run.")
    return digest
