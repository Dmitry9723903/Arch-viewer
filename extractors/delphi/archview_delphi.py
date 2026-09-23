#!/usr/bin/env python3
"""Reads Delphi and Object Pascal into the arch-viewer model.

    python3 archview_delphi.py <repository> [--out arch.html] [--title name]
                               [--no-source]

Needs Python 3.10 or newer. Nothing has to be compiled.

Pascal states its structure in the language itself, which few languages do:
a file is a `unit`, and a unit says what it uses. So the modules and the
edges between them are not inferred from directories or from a project file
— they are read from the first two declarations of every file.

The text is tokenised, not matched with expressions. Pascal has three kinds
of comment — `{ }`, `(* *)` and `//` — and a compiler directive looks
exactly like the first of them with a `$` after the brace. A reader that did
not know the difference would take `{$IFDEF WIN32}` for a comment and the
word `end` inside a string for the end of a class.

Kept as one file, like the SQL extractor and unlike the C++ one: its whole
domain is a unit, a use and a declaration, and folders around that would add
no decision to make.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

SUFFIXES = (".pas", ".dpr", ".dpk", ".inc")

SKIP = {
    ".git", ".svn", "__history", "__recovery", "backup", "bin", "obj",
    "dcu", "lib", "node_modules", "__pycache__",
}

# Words that open a block and so must be closed by `end`.
OPENS = {"begin", "case", "record", "try", "asm", "object", "class", "interface", "dispinterface"}

# What a `type` declaration can be.
KINDS = {
    "class": "class",
    "record": "record",
    "interface": "interface",
    "dispinterface": "interface",
    "object": "object",
}

MEMBER_WORDS = {"procedure", "function", "constructor", "destructor", "property"}

# A routine written at unit level belongs to the unit, not to a type. Pascal
# units are largely made of these, and a unit read without them is a unit
# read as empty.
ROUTINE_WORDS = {"procedure", "function"}


@dataclass(frozen=True)
class Token:
    """One token, and the line it began on."""

    text: str
    line: int
    string: bool = False

    @property
    def word(self) -> str:
        """The token folded for comparison: Pascal ignores case."""
        return self.text.lower()


@dataclass
class Declared:
    """A type a unit declares."""

    name: str
    kind: str
    line: int
    end_line: int
    bases: list[str] = field(default_factory=list)
    members: list[dict] = field(default_factory=list)
    source: str | None = None


@dataclass
class Unit:
    """One file: what it is called, what it uses, what it declares."""

    path: str
    name: str
    uses: list[str] = field(default_factory=list)
    types: list[Declared] = field(default_factory=list)
    directives: int = 0


def tokenise(text: str) -> tuple[list[Token], int]:
    """Reads Pascal into tokens. Returns them with the directives passed over."""
    tokens: list[Token] = []
    directives = 0
    index = 0
    line = 1
    length = len(text)

    while index < length:
        character = text[index]

        if character == "\n":
            line += 1
            index += 1
            continue

        if character in " \t\r\f\v":
            index += 1
            continue

        # `{ … }` is a comment; `{$ … }` is a compiler directive, which is
        # not a comment and may hold anything at all.
        if character == "{":
            end = text.find("}", index + 1)

            if end < 0:
                break

            if index + 1 < length and text[index + 1] == "$":
                directives += 1

            line += text.count("\n", index, end)
            index = end + 1
            continue

        if character == "(" and index + 1 < length and text[index + 1] == "*":
            end = text.find("*)", index + 2)

            if end < 0:
                break

            line += text.count("\n", index, end)
            index = end + 2
            continue

        if character == "/" and index + 1 < length and text[index + 1] == "/":
            while index < length and text[index] != "\n":
                index += 1
            continue

        # A string. Two quotes in a row are one quote, not an end.
        if character == "'":
            start, index = index, index + 1

            while index < length:
                if text[index] == "'":
                    if index + 1 < length and text[index + 1] == "'":
                        index += 2
                        continue
                    index += 1
                    break

                if text[index] == "\n":
                    line += 1

                index += 1

            tokens.append(Token(text[start:index], line, string=True))
            continue

        if character.isalpha() or character == "_" or character == "&":
            start = index

            while index < length and (text[index].isalnum() or text[index] == "_"):
                index += 1

            tokens.append(Token(text[start:index], line))
            continue

        if character.isdigit() or character == "$" or character == "#":
            index += 1

            while index < length and (text[index].isalnum() or text[index] == "."):
                index += 1

            continue

        tokens.append(Token(character, line))
        index += 1

    return tokens, directives


def read(path: Path, relative: str, with_source: bool) -> Unit | None:
    """Reads one unit: its name, what it uses and what it declares."""
    raw = path.read_bytes()
    text = None

    for encoding in ("utf-8-sig", "utf-8", "cp1251", "cp1252", "latin-1"):
        try:
            text = raw.decode(encoding)
            break
        except UnicodeDecodeError:
            continue

    if text is None:
        return None

    tokens, directives = tokenise(text)
    lines = text.splitlines()
    unit = Unit(relative, Path(relative).stem, directives=directives)
    index = 0
    in_type = False

    while index < len(tokens):
        token = tokens[index]
        word = token.word

        if token.string:
            index += 1
            continue

        if word in ("unit", "program", "library", "package") and index + 1 < len(tokens):
            unit.name = tokens[index + 1].text
            index += 2
            continue

        if word == "uses":
            index = _uses(tokens, index + 1, unit)
            continue

        if word == "type":
            in_type = True
            index += 1
            continue

        if word in ROUTINE_WORDS:
            index = _routine(tokens, index, unit, lines, with_source)
            in_type = False
            continue

        if word in ("var", "const", "implementation", "begin", "resourcestring"):
            in_type = word == "implementation" and in_type
            index += 1
            continue

        # `TFoo = class(TBar)` — a name, an equals sign, and what it is.
        if (
            in_type
            and token.text
            and (token.text[0].isalpha() or token.text[0] == "_")
            and index + 2 < len(tokens)
            and tokens[index + 1].text == "="
        ):
            index = _declaration(tokens, index, unit, lines, with_source)
            continue

        index += 1

    return unit


def _routine(
    tokens: list[Token], index: int, unit: Unit, lines: list[str], with_source: bool
) -> int:
    """`procedure Draw(x: Integer);` or `function Sum(…): Integer; begin … end;`

    A unit declares its routines twice — once in the interface and once where
    they are written — and they are one routine, not two. The definition wins
    when there is one, because it is the thing with a body to show.
    """
    word = tokens[index].word
    first = tokens[index].line
    scan = index + 1

    if scan >= len(tokens) or not tokens[scan].text[:1].isalpha():
        return scan

    name = tokens[scan].text

    # `procedure TForm1.Draw;` defines a method of a type, which the type
    # itself already lists. Recording it here would put it on the map twice.
    qualified = scan + 1 < len(tokens) and tokens[scan + 1].text == "."

    if qualified:
        return _past_routine(tokens, scan + 1)[0]

    arguments: list[str] = []
    depth = 0
    scan += 1

    while scan < len(tokens):
        token = tokens[scan]

        if token.text == "(":
            depth += 1
        elif token.text == ")":
            depth -= 1
        elif token.text == ";" and depth == 0:
            scan += 1
            break
        elif depth == 1 and not token.string and token.text[:1].isalpha():
            if token.word not in ("var", "const", "out", "array", "of"):
                arguments.append(token.text)

        scan += 1

    after, last, bodied = _past_routine(tokens, scan)

    existing = next((t for t in unit.types if t.name.lower() == name.lower()), None)

    if existing is not None and not bodied:
        return after

    declared = Declared(
        name=name,
        kind="function" if word == "function" else "procedure",
        line=first,
        end_line=max(first, last),
    )
    declared.members = [{"text": a, "line": first} for a in arguments[:20]]
    _finish(declared, lines, with_source)

    if existing is not None:
        unit.types.remove(existing)

    unit.types.append(declared)
    return after


def _past_routine(tokens: list[Token], index: int) -> tuple[int, int, bool]:
    """Past a routine: its body when it has one, its semicolon when it does not."""
    scan = index
    last = tokens[min(index, len(tokens) - 1)].line

    while scan < len(tokens):
        token = tokens[scan]
        word = token.word

        if token.string:
            scan += 1
            continue

        # Another routine begins: this one was a declaration with no body.
        if word in ROUTINE_WORDS or word in ("implementation", "initialization", "end"):
            return scan, last, False

        if word == "begin":
            depth = 0

            while scan < len(tokens):
                inner = tokens[scan]

                if inner.string:
                    scan += 1
                    continue

                if inner.word in OPENS or inner.word == "begin":
                    depth += 1
                elif inner.word == "end":
                    depth -= 1

                    if depth == 0:
                        return scan + 1, inner.line, True

                scan += 1

            return scan, last, True

        last = token.line
        scan += 1

    return scan, last, False


def _uses(tokens: list[Token], index: int, unit: Unit) -> int:
    """`uses A, B in 'B.pas', C;` — the names, not the file names."""
    expecting = True

    while index < len(tokens):
        token = tokens[index]

        if token.text == ";":
            return index + 1

        if token.text == ",":
            expecting = True
            index += 1
            continue

        if token.word == "in":
            # `B in 'B.pas'` names the same unit twice; the file name is the
            # build's business and not another dependency.
            expecting = False
            index += 2
            continue

        if expecting and not token.string and token.text[0].isalpha():
            if token.text not in unit.uses:
                unit.uses.append(token.text)

            expecting = False

        index += 1

    return index


def _declaration(
    tokens: list[Token], index: int, unit: Unit, lines: list[str], with_source: bool
) -> int:
    """One `Name = …` declaration, with its body when it has one."""
    name = tokens[index].text
    first = tokens[index].line
    scan = index + 2

    if scan >= len(tokens):
        return scan

    word = tokens[scan].word

    # `TFoo = class of TBar;` and `TFoo = class;` declare no body.
    if word == "class" and scan + 1 < len(tokens) and tokens[scan + 1].word == "of":
        return _to_semicolon(tokens, scan)

    # `TCallback = function (x: Integer): Boolean;` — a procedural type, which
    # is Pascal's delegate: a first-class function with a name of its own.
    if word in ROUTINE_WORDS:
        declared = Declared(
            name,
            "function type" if word == "function" else "procedure type",
            first,
            first,
        )
        scan = _to_semicolon(tokens, scan)
        declared.end_line = tokens[min(scan, len(tokens) - 1)].line
        _finish(declared, lines, with_source)
        unit.types.append(declared)
        return scan

    if word not in KINDS:
        # An enumeration: `TStyle = (asLeft, asRight);`
        if tokens[scan].text == "(":
            declared = Declared(name, "enum", first, first)
            scan = _enumeration(tokens, scan + 1, declared)
            declared.end_line = tokens[min(scan, len(tokens) - 1)].line
            _finish(declared, lines, with_source)
            unit.types.append(declared)
            return scan

        # An alias — `TKode = String[41];` — is a name for a type, not a
        # type of its own, and putting one on the map as a box would say
        # there is something there to open.
        return _to_semicolon(tokens, scan)

    declared = Declared(name, KINDS[word], first, first)
    scan += 1

    if scan < len(tokens) and tokens[scan].text == "(":
        scan = _bases(tokens, scan + 1, declared)

    if scan < len(tokens) and tokens[scan].text == ";":
        # A forward declaration: `TFoo = class;`
        return scan + 1

    depth = 1

    while scan < len(tokens) and depth > 0:
        token = tokens[scan]
        word = token.word

        if token.string:
            scan += 1
            continue

        if word == "end":
            depth -= 1

            if depth == 0:
                declared.end_line = token.line
                break

            scan += 1
            continue

        if word in OPENS:
            # `class procedure`, `class function`, `class var` continue the
            # same block; only a nested type opens another.
            if word == "class" and scan + 1 < len(tokens) and tokens[scan + 1].word in (
                "procedure", "function", "var", "operator", "constructor", "destructor",
            ):
                scan += 1
                continue

            depth += 1
            scan += 1
            continue

        if word in MEMBER_WORDS and scan + 1 < len(tokens):
            following = tokens[scan + 1]

            if following.text and following.text[0].isalpha():
                declared.members.append(
                    {"text": f"{following.text}(…)", "line": token.line}
                )

            scan += 2
            continue

        # `Field: Type;`
        if (
            token.text
            and token.text[0].isalpha()
            and scan + 1 < len(tokens)
            and tokens[scan + 1].text == ":"
            and len(declared.members) < 60
        ):
            written = tokens[scan + 2].text if scan + 2 < len(tokens) else ""
            declared.members.append(
                {"text": f"{written} {token.text}".strip(), "line": token.line}
            )
            scan += 2
            continue

        scan += 1

    _finish(declared, lines, with_source)
    unit.types.append(declared)
    return scan + 1


def _bases(tokens: list[Token], index: int, declared: Declared) -> int:
    """`class(TBase, IThing)` — the names it derives from."""
    while index < len(tokens):
        token = tokens[index]

        if token.text == ")":
            return index + 1

        if token.text and token.text[0].isalpha() and not token.string:
            if token.text not in declared.bases:
                declared.bases.append(token.text)

        index += 1

    return index


def _enumeration(tokens: list[Token], index: int, declared: Declared) -> int:
    """`(asLeft, asRight, asNone)` — the values are the members."""
    while index < len(tokens):
        token = tokens[index]

        if token.text == ")":
            return index + 1

        if token.text and token.text[0].isalpha() and len(declared.members) < 60:
            declared.members.append({"text": token.text, "line": token.line})

        index += 1

    return index


def _to_semicolon(tokens: list[Token], index: int) -> int:
    """Past the end of a declaration that has no body.

    Parentheses are counted, because a procedural type separates its own
    parameters with semicolons — `= function (a: Pointer; b: Integer)` — and
    stopping at the first of those leaves the reader standing in the middle
    of a declaration. Everything after it is then read as if it were top
    level: a class becomes invisible and its methods become routines of the
    unit.
    """
    depth = 0

    while index < len(tokens):
        text = tokens[index].text

        if text == "(":
            depth += 1
        elif text == ")":
            depth -= 1
        elif text == ";" and depth <= 0:
            return index + 1

        index += 1

    return index


def _finish(declared: Declared, lines: list[str], with_source: bool) -> None:
    """Attaches the text of a declaration, cut when nobody would read it."""
    if not with_source:
        return

    limit = 400
    first, last = declared.line, max(declared.line, declared.end_line)
    declared.source = "\n".join(
        lines[first - 1 : first - 1 + min(last - first + 1, limit)]
    )

    if last - first + 1 > limit:
        declared.source += f"\n… {last - first + 1 - limit} more lines"


def build(root: Path, units: list[Unit], title: str, with_source: bool) -> dict:
    """Folders holding units holding the types they declare."""
    by_name = {u.name.lower(): u for u in units}
    folders: dict[str, dict] = {}
    loose: list[dict] = []
    type_ids: dict[str, list[str]] = {}

    for unit in sorted(units, key=lambda u: u.path):
        for declared in unit.types:
            type_ids.setdefault(declared.name.lower(), []).append(
                f"{unit.path}::{declared.name}"
            )

    for unit in sorted(units, key=lambda u: u.path):
        node = {
            "id": unit.path,
            "label": unit.name,
            "kind": "unit",
            "project": unit.path,
            "children": [],
            "types": [
                {
                    "id": f"{unit.path}::{d.name}",
                    "name": d.name,
                    "stereotype": d.kind,
                    "file": unit.path,
                    "line": d.line,
                    "endLine": max(d.line, d.end_line),
                    "source": d.source if with_source else None,
                    "members": d.members,
                }
                for d in unit.types
            ],
        }

        directory = unit.path.rsplit("/", 1)[0] if "/" in unit.path else ""

        if directory:
            folders.setdefault(
                directory,
                {
                    "id": directory,
                    "label": directory,
                    "kind": "folder",
                    "children": [],
                    "types": [],
                },
            )["children"].append(node)
        else:
            loose.append(node)

    edges: list[dict] = []
    seen: set[tuple[str, str, str]] = set()

    def add(source: str, target: str, kind: str) -> None:
        key = (source, target, kind)

        if source != target and key not in seen:
            seen.add(key)
            edges.append({"from": source, "to": target, "kind": kind})

    for unit in units:
        for used in unit.uses:
            target = by_name.get(used.lower())

            # A unit this repository does not hold is one of Delphi's own —
            # Windows, SysUtils, Classes. Not an edge inside the repository,
            # and not an error either.
            if target is not None:
                add(unit.path, target.path, "dependency")

        for declared in unit.types:
            source = f"{unit.path}::{declared.name}"

            for base in declared.bases:
                candidates = type_ids.get(base.lower(), [])

                # A name declared twice is dropped rather than guessed at.
                if len(candidates) == 1:
                    add(source, candidates[0], "inheritance")

    return {
        "title": title,
        "root": str(root),
        "nodes": [folders[k] for k in sorted(folders)] + loose,
        "edges": edges,
    }


def main() -> int:
    """Reads the repository named on the command line."""
    parser = argparse.ArgumentParser(
        description="Reads Delphi / Object Pascal into the arch-viewer model."
    )
    parser.add_argument("repository", help="Repository root.")
    parser.add_argument("--out", default="arch.html", help="Where to write the page.")
    parser.add_argument("--title", default=None, help="Name shown on screen.")
    parser.add_argument("--no-source", action="store_true", help="Leave the text out.")
    parser.add_argument("--viewer", default=None, help="The viewer page to embed into.")
    arguments = parser.parse_args()

    root = Path(arguments.repository).resolve()

    if not root.is_dir():
        print(f"No such directory: {root}", file=sys.stderr)
        return 1

    with_source = not arguments.no_source
    files = [
        p
        for p in root.rglob("*")
        if p.suffix.lower() in SUFFIXES
        and p.is_file()
        and not any(
            part.lower() in SKIP or part.startswith(".")
            for part in p.relative_to(root).parts
        )
    ]

    units: list[Unit] = []
    unreadable = 0

    for path in sorted(files):
        relative = str(path.relative_to(root)).replace("\\", "/")

        try:
            unit = read(path, relative, with_source)
        except OSError:
            unit = None

        if unit is None:
            unreadable += 1
            continue

        units.append(unit)

    model = build(root, units, arguments.title or root.name, with_source)
    out = Path(arguments.out)
    text = json.dumps(model, ensure_ascii=False, indent=2)
    out.with_suffix(".json").write_text(text, encoding="utf-8")

    viewer = (
        Path(arguments.viewer)
        if arguments.viewer
        else Path(__file__).resolve().parents[2] / "viewer" / "index.html"
    )

    if out.suffix == ".html":
        if not viewer.is_file():
            print(f"No viewer at {viewer}; wrote the model only.", file=sys.stderr)
        else:
            page = viewer.read_text(encoding="utf-8")
            out.write_text(page.replace("/*MODEL*/null", text, 1), encoding="utf-8")

    declared = sum(len(u.types) for u in units)
    outside = sum(
        1
        for u in units
        for used in u.uses
        if used.lower() not in {x.name.lower() for x in units}
    )

    print(f"files      {len(files)}")
    print(f"units      {len(units)} read")
    print(f"types      {declared}")
    print(f"edges      {len(model['edges'])}")
    print(f"written    {arguments.out}")

    if unreadable:
        print(f"\n{unreadable} files would not open or would not decode.")

    if outside:
        print(
            f"\n{outside} used units are not in this repository — Delphi's own "
            "and third-party ones. They are not drawn."
        )

    directives = sum(u.directives for u in units)

    if directives:
        print(
            f"{directives} compiler directives were passed over; a declaration "
            "inside one is not visible to this reader."
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
