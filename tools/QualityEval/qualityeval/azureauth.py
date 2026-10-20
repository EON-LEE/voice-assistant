"""Microsoft Entra access tokens for Azure AI Speech (TTS) and Azure OpenAI (judge). Keys are never used.

Order: an explicit environment variable, then azure-identity's DefaultAzureCredential (if installed, interactive
browser excluded), then the signed-in Azure CLI. Tokens are kept in memory only and never printed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import time
from typing import Callable

COGNITIVE_SCOPE = "https://cognitiveservices.azure.com/.default"


class AuthError(RuntimeError):
    pass


def _azure_identity(scope: str) -> tuple[str, float] | None:
    try:
        from azure.identity import DefaultAzureCredential  # type: ignore[import-not-found]
    except ImportError:
        return None
    try:
        token = DefaultAzureCredential(exclude_interactive_browser_credential=True).get_token(scope)
    except Exception:  # noqa: BLE001 - provider detail is deliberately not surfaced
        return None
    return token.token, float(token.expires_on)


def _azure_cli(scope: str) -> tuple[str, float] | None:
    executable = shutil.which("az")
    if not executable:
        return None
    try:
        result = subprocess.run([executable, "account", "get-access-token", "--scope", scope, "--query", "accessToken",
                                 "--output", "tsv", "--only-show-errors"], capture_output=True, text=True, timeout=90,
                                check=False)
    except (OSError, subprocess.SubprocessError):
        return None
    token = result.stdout.strip()
    if result.returncode != 0 or not token:
        return None
    return token, time.time() + 600


def token_provider(env_var: str, scope: str = COGNITIVE_SCOPE) -> Callable[[], str]:
    cache: dict[str, object] = {}

    def get() -> str:
        explicit = os.environ.get(env_var, "").strip()
        if explicit:
            return explicit
        if cache and float(cache["expires"]) - 300 > time.time():  # type: ignore[arg-type]
            return str(cache["token"])
        acquired = _azure_identity(scope) or _azure_cli(scope)
        if acquired is None:
            raise AuthError(f"No Microsoft Entra token for {scope}. Set {env_var}, install azure-identity, "
                            "or sign in with Azure CLI.")
        cache["token"], cache["expires"] = acquired
        return acquired[0]

    return get
