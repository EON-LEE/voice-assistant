"""Write boundaries: evaluation output never lands in tracked repository paths.

Inside the repository only the tool's git-ignored ``out/`` and ``.tts-cache/`` folders are writable (verified with
``git check-ignore`` when Git is available). Any other repository path, including the committed corpus, is refused.
Paths outside the repository are allowed.
"""

from __future__ import annotations

import subprocess
import time
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parents[1]
CORPUS_DIR = TOOL_DIR / "corpus"
DEFAULT_OUTPUT_DIR = TOOL_DIR / "out"
DEFAULT_CACHE_DIR = TOOL_DIR / ".tts-cache"


class PrivacyError(PermissionError):
    """Raised when an operation would cross the public/private boundary."""


def find_repo_root(start: Path) -> Path | None:
    for candidate in (start, *start.parents):
        if (candidate / ".git").exists():
            return candidate
    return None


REPO_ROOT = find_repo_root(TOOL_DIR)


def inside(path: Path, base: Path) -> bool:
    try:
        path.relative_to(base)
        return True
    except ValueError:
        return False


def git_ignored(path: Path, repo_root: Path) -> bool | None:
    """True/False from ``git check-ignore``; None when Git cannot answer."""
    try:
        result = subprocess.run(["git", "-C", str(repo_root), "check-ignore", "-q", "--", str(path)],
                                capture_output=True, timeout=20, check=False)
    except (OSError, subprocess.SubprocessError):
        return None
    return {0: True, 1: False}.get(result.returncode)


def check_output_path(path: str | Path, *, repo_root: Path | None = REPO_ROOT,
                      allowed_dirs: tuple[Path, ...] = (DEFAULT_OUTPUT_DIR, DEFAULT_CACHE_DIR),
                      require_git_ignore: bool = True) -> Path:
    """Return the resolved path when writing there is allowed, otherwise raise PrivacyError."""
    resolved = Path(path).resolve()
    if repo_root is None or not inside(resolved, repo_root.resolve()):
        return resolved
    bases = [base.resolve() for base in allowed_dirs]
    if not any(inside(resolved, base) for base in bases):
        raise PrivacyError(
            "Refusing to write evaluation output inside the repository. Use the git-ignored "
            "tools/QualityEval/out or tools/QualityEval/.tts-cache folders, or a path outside the repository.")
    if require_git_ignore and resolved not in bases and git_ignored(resolved, repo_root) is False:
        raise PrivacyError("The output path is inside the repository but is not git-ignored; refusing to write.")
    return resolved


def safe_mkdir(path: str | Path, **kwargs) -> Path:
    resolved = check_output_path(path, **kwargs)
    resolved.mkdir(parents=True, exist_ok=True)
    return resolved


def safe_write_bytes(path: str | Path, data: bytes, *, overwrite: bool = False, **kwargs) -> Path:
    resolved = check_output_path(path, **kwargs)
    resolved.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(4):
        try:
            with open(resolved, "wb" if overwrite else "xb") as handle:
                handle.write(data)
            return resolved
        except PermissionError:
            # Windows scanners can hold a just-written file for a moment; never retried for create-only writes.
            if not overwrite or attempt == 3:
                raise
            time.sleep(0.25 * (attempt + 1))
    return resolved


def safe_write_text(path: str | Path, text: str, *, overwrite: bool = False, **kwargs) -> Path:
    return safe_write_bytes(path, text.encode("utf-8"), overwrite=overwrite, **kwargs)
