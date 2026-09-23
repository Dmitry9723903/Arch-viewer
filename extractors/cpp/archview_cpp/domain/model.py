"""What a C or C++ repository is made of, in its own words.

This module names the concepts and the rules that hold between them. It
reads no file, knows no XML and never hears of clang: everything here would
be as true of a repository nobody had written down yet. Anything that needs
a disk belongs in an adapter.
"""

from __future__ import annotations

from dataclasses import dataclass, field, replace
from enum import Enum


class DeclarationKind(str, Enum):
    """What was declared. The words are C++'s own, not invented ones."""

    CLASS = "class"
    STRUCT = "struct"
    UNION = "union"
    ENUM = "enum"
    NAMESPACE = "namespace"
    FUNCTION = "function"


@dataclass(frozen=True)
class SourcePath:
    """A file, as this repository refers to it.

    Always relative to the repository root and always with forward slashes,
    so that the same file has the same name on every machine that reads it.
    A path is compared, grouped and printed; letting two spellings of one
    file exist would split its declarations between two modules.
    """

    value: str

    def __post_init__(self) -> None:
        if not self.value:
            raise ValueError("A source path is never empty.")
        if "\\" in self.value:
            raise ValueError(f"A source path uses forward slashes: {self.value!r}")

    @property
    def directory(self) -> str:
        """The directory holding the file, or the empty string at the root."""
        cut = self.value.rfind("/")
        return "" if cut < 0 else self.value[:cut]

    @property
    def name(self) -> str:
        """The file's own name, without its directory."""
        return self.value.rsplit("/", 1)[-1]

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True)
class Span:
    """Where a declaration begins and ends, counted in lines from one.

    A span that ends before it begins is not a defect to report later: it
    cannot be constructed.
    """

    first: int
    last: int

    def __post_init__(self) -> None:
        if self.first < 1:
            raise ValueError(f"Lines are counted from one, not {self.first}.")
        if self.last < self.first:
            raise ValueError(f"A span ends after it begins: {self.first}..{self.last}")

    @property
    def lines(self) -> int:
        """How many lines the declaration occupies."""
        return self.last - self.first + 1


@dataclass(frozen=True)
class Declaration:
    """A type declared in the repository's own source.

    `bases` are written as the source writes them — `Sensor`, not a resolved
    identity. Resolving a base name needs the whole translation unit, which
    only the clang reader has; a name a reader could not resolve is a name,
    never a silent omission.
    """

    name: str
    kind: DeclarationKind
    path: SourcePath
    span: Span
    qualifier: str = ""
    bases: tuple[str, ...] = ()
    members: tuple[str, ...] = ()
    stereotype: str | None = None
    source: str | None = None

    def __post_init__(self) -> None:
        if not self.name:
            raise ValueError("A declaration has a name.")

    @property
    def full_name(self) -> str:
        """The name as C++ would write it, namespaces included."""
        return f"{self.qualifier}::{self.name}" if self.qualifier else self.name

    def without_source(self) -> "Declaration":
        """The same declaration with its text dropped, its span kept."""
        return replace(self, source=None)


@dataclass(frozen=True)
class ModuleId:
    """What a module is called. Unique within a repository."""

    value: str

    def __post_init__(self) -> None:
        if not self.value:
            raise ValueError("A module has a name.")

    def __str__(self) -> str:
        return self.value


@dataclass
class Module:
    """One compiled thing: a library or a program, and the files it owns.

    A file belongs to exactly one module. Two projects that both list a file
    do exist in the wild — a shared header compiled into several libraries —
    and the first to claim it keeps it, because a file counted twice would
    make every edge through it double.
    """

    id: ModuleId
    name: str
    project: SourcePath | None
    folder: str
    role: str | None = None
    paths: set[SourcePath] = field(default_factory=set)
    declarations: list[Declaration] = field(default_factory=list)

    def claims(self, path: SourcePath) -> bool:
        """Whether this module owns that file."""
        return path in self.paths


@dataclass(frozen=True)
class Reference:
    """One module reaching another, and what it was that reached.

    `via` keeps the two files, because "Devices depends on Kernel" is not
    actionable and "Sensor.h includes Kernel/Units.h" is.
    """

    source: ModuleId
    target: ModuleId
    kind: str
    via: tuple[SourcePath, SourcePath] | None = None

    def __post_init__(self) -> None:
        if self.source == self.target:
            raise ValueError("A module referencing itself is not a reference.")


class Repository:
    """The modules of a repository and the references between them.

    The invariant it protects is small and worth protecting: a reference
    always joins two modules this repository holds. An edge to a module that
    does not exist draws an arrow into nothing, and the screen has no way to
    show that the fault is in the model rather than in the code.
    """

    def __init__(self, root: str, name: str) -> None:
        self._root = root
        self._name = name
        self._modules: dict[ModuleId, Module] = {}
        self._references: list[Reference] = []
        self._owners: dict[SourcePath, ModuleId] = {}
        self._spellings: dict[str, SourcePath] = {}

    @property
    def name(self) -> str:
        """What the repository is called on screen."""
        return self._name

    @property
    def root(self) -> str:
        """Where the repository sits on disk."""
        return self._root

    @property
    def modules(self) -> tuple[Module, ...]:
        """Every module, in the order it was added."""
        return tuple(self._modules.values())

    @property
    def references(self) -> tuple[Reference, ...]:
        """Every reference between modules."""
        return tuple(self._references)

    def add(self, module: Module) -> Module:
        """Adds a module, giving it only the files no module owns yet."""
        if module.id in self._modules:
            raise ValueError(f"Two modules are called {module.id}.")

        module.paths = {p for p in module.paths if p not in self._owners}
        self._modules[module.id] = module

        for path in module.paths:
            self._owners[path] = module.id
            self._spellings.setdefault(path.value.lower(), path)

        return module

    def owner(self, path: SourcePath) -> ModuleId | None:
        """Which module owns that file, if any does."""
        return self._owners.get(path)

    def locate(self, value: str) -> SourcePath | None:
        """The file this repository holds under that name, whatever its case.

        A repository written on Windows refers to its own files without
        regard to case: a project lists `StdAfx.h` and an include asks for
        `stdafx.h`, and to that compiler they are one file. Read on a system
        where they are two, every such include points at nothing, and a
        third of the edges of a large repository simply vanish — quietly,
        which is the part that matters.
        """
        return self._spellings.get(value.lower())

    def refer(self, reference: Reference) -> None:
        """Records a reference, refusing one that leaves the repository."""
        if reference.source not in self._modules:
            raise ValueError(f"No such module: {reference.source}")
        if reference.target not in self._modules:
            raise ValueError(f"No such module: {reference.target}")

        self._references.append(reference)

    def declare(self, path: SourcePath, declarations: list[Declaration]) -> None:
        """Files what was declared in a file under the module that owns it."""
        owner = self._owners.get(path)

        if owner is not None:
            self._modules[owner].declarations.extend(declarations)

    @property
    def paths(self) -> tuple[SourcePath, ...]:
        """Every file owned by a module."""
        return tuple(self._owners)
