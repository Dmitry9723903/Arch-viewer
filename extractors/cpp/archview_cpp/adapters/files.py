"""Reading the repository's text off a disk.

A legacy repository is not UTF-8 throughout. Encodings are tried in turn and
the first that decodes wins; a file none of them reads is counted, not
guessed at.
"""

from __future__ import annotations

import os

from ..domain.model import SourcePath

_ENCODINGS = ("utf-8-sig", "utf-8", "cp1251", "cp1252", "latin-1")


class DiskFiles:
    """The repository's files, read as text."""

    def __init__(self, root: str, limit: int = 4 * 1024 * 1024) -> None:
        self._root = root
        self._limit = limit

    def read(self, path: SourcePath) -> str | None:
        """A file's text, or None when it will not decode or will not open."""
        full = os.path.join(self._root, path.value)

        try:
            if os.path.getsize(full) > self._limit:
                return None

            with open(full, "rb") as handle:
                raw = handle.read()
        except OSError:
            return None

        for encoding in _ENCODINGS:
            try:
                return raw.decode(encoding)
            except UnicodeDecodeError:
                continue

        return None

    def exists(self, path: SourcePath) -> bool:
        """Whether the repository holds that file."""
        return os.path.isfile(os.path.join(self._root, path.value))
