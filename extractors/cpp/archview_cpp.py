#!/usr/bin/env python3
"""Reads a C or C++ repository into the arch-viewer model.

This file is the composition root: the only place that names a concrete
adapter and decides which one to use. Everything below it knows interfaces.

    python3 archview_cpp.py <repository> [--out arch.html] [--title name]
                            [--no-source] [--tokens]

Structure comes from Visual C++ project files and from `#include`, both of
which state it exactly. Declarations come from clang when its Python
bindings are installed, and from this tool's own tokeniser otherwise; the
run says which was used, and what it could not read.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from archview_cpp.adapters.files import DiskFiles
from archview_cpp.adapters.model_file import ModelFile
from archview_cpp.adapters.projects import VisualStudioProjects
from archview_cpp.adapters.scanning import TokenDeclarationReader, TokenIncludeReader
from archview_cpp.application.read_repository import ReadRepository


def declaration_reader(forced_tokens: bool, with_source: bool):
    """clang when it is there and wanted, this tool's tokeniser otherwise."""
    if not forced_tokens:
        try:
            from archview_cpp.adapters.clang_reader import ClangDeclarationReader

            reader = ClangDeclarationReader(with_source)

            if reader.available:
                return reader
        except ImportError:
            pass

    return TokenDeclarationReader(with_source)


def main() -> int:
    """Reads the repository named on the command line."""
    parser = argparse.ArgumentParser(
        description="Reads a C/C++ repository into the arch-viewer model."
    )
    parser.add_argument("repository", help="Repository root.")
    parser.add_argument("--out", default="arch.html", help="Where to write the page.")
    parser.add_argument("--title", default=None, help="Name shown on screen.")
    parser.add_argument(
        "--no-source",
        action="store_true",
        help="Leave the source text out; keep the structure and the lines.",
    )
    parser.add_argument(
        "--tokens",
        action="store_true",
        help="Use this tool's tokeniser even where clang is installed.",
    )
    parser.add_argument(
        "--viewer", default=None, help="The viewer page to embed the model into."
    )
    arguments = parser.parse_args()

    root = Path(arguments.repository).resolve()

    if not root.is_dir():
        print(f"No such directory: {root}", file=sys.stderr)
        return 1

    with_source = not arguments.no_source
    projects = VisualStudioProjects(str(root))
    reader = declaration_reader(arguments.tokens, with_source)

    use_case = ReadRepository(
        files=DiskFiles(str(root)),
        projects=projects,
        declarations=reader,
        includes=TokenIncludeReader(),
    )

    reading = use_case.run(str(root), arguments.title or root.name)
    writer = ModelFile(arguments.title or root.name, with_source)
    out = Path(arguments.out)

    viewer = (
        Path(arguments.viewer)
        if arguments.viewer
        else Path(__file__).resolve().parents[2] / "viewer" / "index.html"
    )

    try:
        writer.write(writer.model(reading), out, viewer)
    except FileNotFoundError as problem:
        print(problem, file=sys.stderr)
        return 1

    report(reading, projects, reader, arguments.out)
    return 0


def report(reading, projects, reader, out: str) -> None:
    """Says what was read, and names what was not."""
    print(f"reader     {reader.describes}")
    print(f"modules    {len(reading.repository.modules)} from {projects.describes}")
    print(f"files      {reading.files_read} read")
    print(f"types      {reading.declarations}")
    print(f"edges      {len(reading.repository.references)} between modules")
    print(f"written    {out}")

    if projects.loose_files:
        print()
        print(
            f"{projects.loose_files} source files belong to no project; "
            "they are grouped by their top directory and labelled with it."
        )

    if getattr(projects, "recased", 0):
        print(
            f"{projects.recased} files are named in a project in a different "
            "case than on disk; they were matched to the file on disk."
        )

    if reading.files_unreadable:
        print(f"{reading.files_unreadable} files would not open or would not decode.")

    if reading.includes_unresolved:
        print()
        print(
            f"{reading.includes_unresolved} of {reading.includes_seen} includes "
            "name a file this tool could not place."
        )
        print(
            "An include is resolved against the including file's own directory "
            "and this repository's files, not against the build's include paths."
        )

        for example in reading.unresolved_examples:
            print(f"  {example}")

    directives = getattr(reader, "directives", 0)

    if directives:
        print()
        print(
            f"{directives} preprocessor lines were passed over. A declaration "
            "written inside a macro is not visible to this reader."
        )


if __name__ == "__main__":
    raise SystemExit(main())
