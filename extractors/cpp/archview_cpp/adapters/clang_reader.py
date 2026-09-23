"""Declarations read by clang, when its Python bindings are installed.

The same declarations the tokeniser produces, and a few things only a real
parser knows: a base class resolved through a typedef, a member's type, a
nested declaration inside a template. It is used when it is available and
never required, because a tool that reads other people's repositories has
no business insisting they install a compiler first.

What it costs is stated plainly: clang parses each file for real, so it is
much slower, and without the build's include paths it parses a legacy
Windows repository with many errors. Those errors do not stop it — clang
recovers and reports what it did understand — but they are counted and said
out loud, because a declaration lost to a parse error is exactly the kind of
absence this tool refuses to leave silent.
"""

from __future__ import annotations

import ctypes.util
import glob
import os

from ..domain.model import Declaration, DeclarationKind, SourcePath, Span
from .scanning import _MFC_STEREOTYPES

try:  # pragma: no cover - the whole point is that it may be missing
    from clang import cindex
except ImportError:  # pragma: no cover
    cindex = None  # type: ignore[assignment]


_KINDS: dict[str, DeclarationKind] = {
    "CLASS_DECL": DeclarationKind.CLASS,
    "STRUCT_DECL": DeclarationKind.STRUCT,
    "UNION_DECL": DeclarationKind.UNION,
    "ENUM_DECL": DeclarationKind.ENUM,
    "CLASS_TEMPLATE": DeclarationKind.CLASS,
    "FUNCTION_DECL": DeclarationKind.FUNCTION,
}


class ClangDeclarationReader:
    """Reads declarations with clang."""

    def __init__(self, root: str, with_source: bool = True) -> None:
        self._root = root
        self._with_source = with_source
        self._index = None
        self.errors = 0
        self.files_with_errors = 0

        if cindex is not None:
            library = _libclang()

            if library is not None:
                try:
                    cindex.Config.set_library_file(library)
                except Exception:
                    # Already configured by something else in this process.
                    pass

            try:
                self._index = cindex.Index.create()
            except Exception:
                # No libclang on this machine. Not a failure: the caller
                # falls back to the tokeniser and says which it used.
                self._index = None

    @property
    def available(self) -> bool:
        """Whether clang can actually be used here."""
        return self._index is not None

    @property
    def describes(self) -> str:
        """Which reader this is."""
        return "clang"

    def read(self, path: SourcePath, text: str) -> list[Declaration]:
        """Every type defined in the file, as clang understands it."""
        if self._index is None:
            return []

        # The absolute path, not the repository-relative one. A relative
        # name is resolved against this process's working directory, which
        # is wherever the tool was started; the file itself still arrives
        # through unsaved_files, so it parses — and every `#include "…"`
        # beside it is then looked for in the wrong place and not found.
        #
        # clang recovers from that instead of failing: an unresolved base
        # class simply disappears, and a field of an unknown type becomes
        # `int`. The map then shows a member type nobody measured, which is
        # worse than showing none.
        full = os.path.join(self._root, path.value)

        arguments = [
            "-x",
            "c++" if not path.value.lower().endswith(".c") else "c",
            "-std=c++14",
            "-ferror-limit=0",
            "-Wno-everything",
        ]

        try:
            unit = self._index.parse(
                full,
                args=arguments,
                unsaved_files=[(full, text)],
                # Bodies are parsed, though skipping them would be faster.
                # A function with its body skipped cannot be told from a
                # prototype, and this tool draws definitions only: with the
                # bodies gone, every free function vanished and every header
                # promise would have taken its place.
                options=cindex.TranslationUnit.PARSE_INCOMPLETE,
            )
        except Exception:
            self.files_with_errors += 1
            return []

        errors = sum(
            1 for d in unit.diagnostics if d.severity >= cindex.Diagnostic.Error
        )

        if errors:
            self.errors += errors
            self.files_with_errors += 1

        lines = text.splitlines()
        found: list[Declaration] = []
        self._collect(unit.cursor, path, full, lines, found)
        return found

    def _collect(
        self, cursor, path: SourcePath, full: str, lines: list[str], into: list
    ) -> None:
        """Walks the tree, keeping definitions written in this very file."""
        for child in cursor.get_children():
            location = child.location

            # A header pulled in by this file is that header's business, and
            # counting its declarations here would file them under whichever
            # module happened to include it first.
            if location.file is None or location.file.name != full:
                continue

            kind = _KINDS.get(child.kind.name)

            # A member defined out of line — `Sensor::read(…) { … }` — belongs
            # to its type, which lists it already. Drawn again beside the type
            # it would put one member on the map twice.
            if kind is DeclarationKind.FUNCTION and _belongs_to_a_type(child):
                continue

            if kind is not None and child.is_definition() and child.spelling:
                into.append(self._declaration(child, kind, path, lines))
                continue

            self._collect(child, path, full, lines, into)

    def _declaration(self, cursor, kind, path: SourcePath, lines: list[str]):
        """One declaration, with its bases and members."""
        extent = cursor.extent
        first = max(1, extent.start.line)
        last = max(first, extent.end.line)

        bases: list[str] = []
        members: list[str] = []

        for child in cursor.get_children():
            name = child.kind.name

            if name == "CXX_BASE_SPECIFIER":
                bases.append(child.type.spelling.rsplit("::", 1)[-1])
            elif name in ("FIELD_DECL", "VAR_DECL"):
                members.append(f"{child.type.spelling} {child.spelling}")
            elif name in ("CXX_METHOD", "CONSTRUCTOR", "DESTRUCTOR", "FUNCTION_TEMPLATE"):
                members.append(f"{child.spelling}(…)")
            elif name == "ENUM_CONSTANT_DECL":
                members.append(child.spelling)

            if len(members) >= 60:
                break

        stereotype = next(
            (_MFC_STEREOTYPES[b] for b in bases if b in _MFC_STEREOTYPES),
            kind.value,
        )

        qualifier = _qualifier(cursor)

        return Declaration(
            name=cursor.spelling,
            kind=kind,
            path=path,
            span=Span(first, last),
            qualifier=qualifier,
            bases=tuple(bases),
            members=tuple(members),
            stereotype=stereotype,
            source=self._text(lines, first, last),
        )

    def _text(self, lines: list[str], first: int, last: int) -> str | None:
        """The declaration as written, cut when longer than anyone reads."""
        if not self._with_source:
            return None

        limit = 400
        taken = lines[first - 1 : first - 1 + min(last - first + 1, limit)]
        text = "\n".join(taken)

        if last - first + 1 > limit:
            text += f"\n… {last - first + 1 - limit} more lines"

        return text


def _belongs_to_a_type(cursor) -> bool:
    """Whether a function is a member declared outside its type's body."""
    parent = cursor.semantic_parent

    return parent is not None and parent.kind.name in (
        "CLASS_DECL", "STRUCT_DECL", "UNION_DECL", "CLASS_TEMPLATE",
    )


def _libclang() -> str | None:
    """Where libclang is, when the bindings will not find it themselves.

    The bindings load `libclang.so`, which is the developer package's name.
    A machine that merely runs LLVM has `libclang.so.1` and nothing else, so
    the bindings report no clang on a machine that has it — and this tool
    would quietly fall back to its own reader and say clang was unavailable.
    A stated path, then the loader's own answer, then the usual places.
    """
    named = os.environ.get("ARCHVIEW_LIBCLANG")

    if named and os.path.exists(named):
        return named

    found = ctypes.util.find_library("clang")

    if found:
        return found

    patterns = (
        "/usr/lib/llvm-*/lib/libclang.so*",
        "/usr/lib/x86_64-linux-gnu/libclang*.so*",
        "/usr/lib64/libclang.so*",
        "/usr/local/lib/libclang.so*",
        "/opt/homebrew/opt/llvm/lib/libclang.dylib",
        "/usr/lib/llvm-*/lib/libclang.dylib",
    )

    for pattern in patterns:
        matches = sorted(glob.glob(pattern))

        if matches:
            return matches[-1]

    return None


def _qualifier(cursor) -> str:
    """The namespaces and types a declaration sits inside."""
    parts: list[str] = []
    parent = cursor.semantic_parent

    while parent is not None and parent.kind.name not in ("TRANSLATION_UNIT",):
        if parent.spelling:
            parts.append(parent.spelling)

        parent = parent.semantic_parent

    return "::".join(reversed(parts))
