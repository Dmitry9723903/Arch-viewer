"""The use case: turn a repository of C and C++ into modules and references.

It reads files through ports and decides nothing about their format. What it
does decide is the part no adapter can: which module owns a file, what an
include between two modules means, and what to do when an include points
outside everything known.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from ..domain.model import Module, ModuleId, Reference, Repository, SourcePath
from .ports import DeclarationReader, FileStore, IncludeReader, ProjectSource


@dataclass
class Reading:
    """What one reading of a repository produced, findings included.

    The counts are not decoration. This tool's rule is that nothing unread
    is passed over in silence, and a count is how silence is broken: files
    that would not open, includes that pointed nowhere, declarations found.
    """

    repository: Repository
    files_read: int = 0
    files_unreadable: int = 0
    includes_seen: int = 0
    includes_unresolved: int = 0
    unresolved_examples: list[str] = field(default_factory=list)

    @property
    def declarations(self) -> int:
        """How many types were declared across every module."""
        return sum(len(m.declarations) for m in self.repository.modules)


class ReadRepository:
    """Reads a repository into modules, declarations and references."""

    def __init__(
        self,
        files: FileStore,
        projects: ProjectSource,
        declarations: DeclarationReader,
        includes: IncludeReader,
    ) -> None:
        self._files = files
        self._projects = projects
        self._declarations = declarations
        self._includes = includes

    def run(self, root: str, name: str) -> Reading:
        """Reads everything and returns it with what could not be read."""
        repository = Repository(root, name)

        for module in self._projects.modules():
            repository.add(module)

        reading = Reading(repository)
        found: dict[SourcePath, list[str]] = {}

        for path in repository.paths:
            text = self._files.read(path)

            if text is None:
                reading.files_unreadable += 1
                continue

            reading.files_read += 1
            repository.declare(path, self._declarations.read(path, text))
            found[path] = self._includes.includes(path, text)

        self._connect(repository, reading, found)
        return reading

    def _connect(
        self,
        repository: Repository,
        reading: Reading,
        found: dict[SourcePath, list[str]],
    ) -> None:
        """Turns includes into references between the modules that own them."""
        seen: set[tuple[ModuleId, ModuleId]] = set()

        for path, targets in found.items():
            source = repository.owner(path)

            if source is None:
                continue

            for target in targets:
                reading.includes_seen += 1
                resolved = self._resolve(repository, path, target)

                if resolved is None:
                    reading.includes_unresolved += 1

                    if len(reading.unresolved_examples) < 5:
                        reading.unresolved_examples.append(f"{path} -> {target}")

                    continue

                owner = repository.owner(resolved)

                # An include of a file no module claims is not a defect: a
                # repository may hold headers outside every project. It is
                # simply not an edge between modules.
                if owner is None or owner == source or (source, owner) in seen:
                    continue

                seen.add((source, owner))
                repository.refer(
                    Reference(source, owner, "dependency", via=(path, resolved))
                )

    def _resolve(
        self, repository: Repository, path: SourcePath, target: str
    ) -> SourcePath | None:
        """Finds the file an include names.

        A compiler resolves an include against the include paths of the
        build, which this tool does not have and will not guess at. What it
        does instead is what the text supports: the directory of the
        including file first, as the quoted form requires, then the
        repository's own files by name. A guess that cannot be justified is
        counted as unresolved rather than made.
        """
        cleaned = target.replace("\\", "/").strip()

        while cleaned.startswith("./"):
            cleaned = cleaned[2:]

        beside = _join(path.directory, cleaned)

        if beside is not None:
            found = repository.locate(beside.value)

            if found is not None:
                return found

        if cleaned and "\\" not in cleaned:
            found = repository.locate(cleaned)

            if found is not None:
                return found

        name = cleaned.rsplit("/", 1)[-1].lower()
        matches = [p for p in repository.paths if p.name.lower() == name]

        # One file of that name: it is that file. Several: the include is
        # ambiguous without the build's include paths, and naming one of them
        # would be inventing the answer.
        return matches[0] if len(matches) == 1 else None


def _join(directory: str, relative: str) -> SourcePath | None:
    """Resolves a relative include against a directory, collapsing `..`."""
    parts = [p for p in directory.split("/") if p] if directory else []

    for piece in relative.split("/"):
        if piece in ("", "."):
            continue
        if piece == "..":
            if not parts:
                return None
            parts.pop()
            continue
        parts.append(piece)

    return SourcePath("/".join(parts)) if parts else None
