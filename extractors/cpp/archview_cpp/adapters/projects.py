"""Where the modules of a C or C++ repository are already written down.

Visual C++ states its modules exactly: a project file lists the files it
compiles, and nothing has to be inferred from directory names. Both formats
are read — the MSBuild `.vcxproj` and the older `.vcproj`, which a
repository of this age is more likely to hold.

A repository with no project files at all still has modules; they are just
not written down. Then, and only then, directories stand in for them, and
the run says that is what happened.
"""

from __future__ import annotations

import os
import xml.etree.ElementTree as ET

from ..domain.model import Module, ModuleId, SourcePath

SOURCE_SUFFIXES = (".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".inl")

_SKIP_DIRECTORIES = {
    ".git", ".svn", ".hg", "bin", "obj", "debug", "release", "x64", "ipch",
    "node_modules", "vendor", "third_party", "packages", "__pycache__",
}


class VisualStudioProjects:
    """Modules from `.vcxproj` and `.vcproj` files, and the files they own."""

    def __init__(self, root: str) -> None:
        self._root = root
        self._projects: list[str] = []
        self._loose = 0
        self._recased = 0

    @property
    def describes(self) -> str:
        """What this source read."""
        if not self._projects:
            return "directories, because the repository has no project files"

        return f"{len(self._projects)} Visual C++ project files"

    @property
    def loose_files(self) -> int:
        """Source files no project claimed."""
        return self._loose

    @property
    def recased(self) -> int:
        """Files a project named in a case the disk does not use."""
        return self._recased

    def modules(self) -> list[Module]:
        """Every module, with the files it owns."""
        found: list[Module] = []
        claimed: set[SourcePath] = set()
        read: list[tuple[str, str, list[SourcePath]]] = []

        # What is actually on the disk, indexed without regard to case: a
        # project written on Windows lists `StdAfx.h` for a file saved as
        # `stdafx.h` and is right to, and a reader that insists on the
        # spelling loses the file and every edge through it.
        on_disk = {
            _relative(self._root, p).lower(): _relative(self._root, p)
            for p in _walk(self._root, SOURCE_SUFFIXES)
        }

        for project in sorted(_walk(self._root, (".vcxproj", ".vcproj"))):
            relative = _relative(self._root, project)
            paths = _files_of(self._root, project)
            kept: list[SourcePath] = []

            for path in paths:
                actual = on_disk.get(path.value.lower())

                if actual is None:
                    continue

                if actual != path.value:
                    self._recased += 1

                kept.append(SourcePath(actual))

            if not kept:
                continue

            self._projects.append(relative)
            read.append((_project_name(project), relative, kept))

        # Two projects of one name are ordinary in a repository this old —
        # a "Test" beside every library. The name stays what its author
        # wrote; the identity gains the directory that tells them apart,
        # because an identity two things share is not one.
        times: dict[str, int] = {}

        for name, _, _ in read:
            times[name] = times.get(name, 0) + 1

        for name, relative, paths in read:
            directory = relative.rsplit("/", 1)[0] if "/" in relative else "."
            identity = name if times[name] == 1 else f"{name} ({directory})"

            found.append(
                Module(
                    id=ModuleId(identity),
                    name=name,
                    project=SourcePath(relative),
                    folder=relative.split("/")[0],
                    role=_role(name),
                    paths=set(paths),
                )
            )
            claimed.update(paths)

        everything = {SourcePath(actual) for actual in on_disk.values()}
        loose = everything - claimed
        self._loose = len(loose)

        # Source that belongs to no project is real code and has to live
        # somewhere, or every include through it stops being an edge. It is
        # grouped by its top directory and labelled as what it is, so that a
        # container nobody declared is never mistaken for one that was.
        by_directory: dict[str, set[SourcePath]] = {}

        for path in loose:
            by_directory.setdefault(path.value.split("/")[0] or ".", set()).add(path)

        for directory, paths in sorted(by_directory.items()):
            found.append(
                Module(
                    id=ModuleId(f"{directory}/"),
                    name=f"{directory}/",
                    project=None,
                    folder=directory,
                    role=None,
                    paths=paths,
                )
            )

        return found


def _project_name(path: str) -> str:
    """What the project calls itself, or what its file is called."""
    try:
        tree = ET.parse(path)
    except (ET.ParseError, OSError):
        return os.path.splitext(os.path.basename(path))[0]

    root = tree.getroot()
    name = root.get("Name")

    if name:
        return name

    for element in root.iter():
        if element.tag.endswith("}ProjectName") or element.tag == "ProjectName":
            if element.text:
                return element.text.strip()

    return os.path.splitext(os.path.basename(path))[0]


def _files_of(root: str, project: str) -> list[SourcePath]:
    """The source files a project lists, resolved against its own directory."""
    try:
        tree = ET.parse(project)
    except (ET.ParseError, OSError):
        return []

    directory = os.path.dirname(project)
    found: list[SourcePath] = []

    for element in tree.getroot().iter():
        tag = element.tag.rsplit("}", 1)[-1]
        raw = None

        if tag == "File":
            raw = element.get("RelativePath")
        elif tag in ("ClCompile", "ClInclude", "None"):
            raw = element.get("Include")

        if not raw:
            continue

        cleaned = raw.replace("\\", "/").lstrip("./")

        if not cleaned.lower().endswith(SOURCE_SUFFIXES):
            continue

        absolute = os.path.normpath(os.path.join(directory, cleaned))

        if not absolute.startswith(os.path.abspath(root)) and not os.path.isabs(root):
            pass

        relative = _relative(root, absolute)

        if relative.startswith(".."):
            continue

        found.append(SourcePath(relative))

    return found


def _role(name: str) -> str | None:
    """The last part of a dotted project name, which is what it is for."""
    parts = [p for p in name.replace("_", ".").split(".") if p]
    return parts[-1].lower() if len(parts) > 1 else None


def _relative(root: str, path: str) -> str:
    """A path as this repository refers to it."""
    return os.path.relpath(path, root).replace(os.sep, "/")


def _walk(root: str, suffixes: tuple[str, ...]):
    """Every file under the root with one of those suffixes."""
    for directory, folders, names in os.walk(root):
        folders[:] = [
            f for f in folders
            if f.lower() not in _SKIP_DIRECTORIES and not f.startswith(".")
        ]

        for name in names:
            if name.lower().endswith(suffixes):
                yield os.path.join(directory, name)
