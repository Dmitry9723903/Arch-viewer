"""What the use case needs from the world, stated as interfaces it owns.

The application declares these; adapters implement them. The direction is
the point: nothing here imports an adapter, so a new way of finding modules
or reading declarations is added without this file changing.
"""

from __future__ import annotations

from typing import Protocol, runtime_checkable

from ..domain.model import Declaration, Module, SourcePath


@runtime_checkable
class ProjectSource(Protocol):
    """Finds the modules of a repository and the files each one owns."""

    @property
    def describes(self) -> str:
        """What this source read, for the run to report."""

    def modules(self) -> list[Module]:
        """Every module found, each with its files."""


@runtime_checkable
class DeclarationReader(Protocol):
    """Reads the types declared in one file."""

    @property
    def describes(self) -> str:
        """Which reader this is, for the run to report."""

    def read(self, path: SourcePath, text: str) -> list[Declaration]:
        """What that file declares. An unreadable file yields nothing."""


@runtime_checkable
class IncludeReader(Protocol):
    """Reads the files one file includes."""

    def includes(self, path: SourcePath, text: str) -> list[str]:
        """The quoted include targets, as written in the source."""


@runtime_checkable
class FileStore(Protocol):
    """Reads the repository's text. The only thing that touches a disk."""

    def read(self, path: SourcePath) -> str | None:
        """A file's text, or None when it cannot be read as text."""

    def exists(self, path: SourcePath) -> bool:
        """Whether the repository holds that file."""
