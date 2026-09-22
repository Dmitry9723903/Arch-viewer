#!/usr/bin/env python3
"""Reads SQL scripts into the arch-viewer model.

    python3 archview_sql.py <repository> [--out arch.html] [--title name]
                            [--no-source]

Kept as one file on purpose. The C/C++ extractor beside it is written in
layers because it has a domain worth layering — modules, ownership of files,
resolution of includes. This has three concepts and one pass, and dividing
it into a domain, an application and adapters would add folders without
adding a decision to make in them.

What it reads: the objects a script creates, and what each of them names.
A view that selects from a table depends on that table; a procedure that
calls a procedure depends on it; a foreign key is a table depending on a
table. Those are the edges, and they are read from the text as tokens —
never as lines, because a table name inside a comment or a string is not a
dependency.

What it does not do is understand SQL. It recognises the statements that
declare something and the places where a name can only be an object's name.
A dialect it has not met declares its objects some other way, and then this
says how many statements it could not place rather than reporting none.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

SUFFIXES = (".sql", ".ddl", ".pks", ".pkb")

SKIP = {
    ".git", ".svn", "bin", "obj", "debug", "release", "node_modules",
    "vendor", "packages", "__pycache__",
}

# What a statement creates, and what to call it on screen.
CREATES = {
    ("create", "table"): "table",
    ("create", "view"): "view",
    ("alter", "view"): "view",
    ("create", "procedure"): "procedure",
    ("create", "proc"): "procedure",
    ("create", "function"): "function",
    ("create", "trigger"): "trigger",
}

# Where the next name can only be the name of an object.
REFERS = {
    "from", "join", "into", "update", "references", "exec", "execute", "call",
}

NOT_A_NAME = {
    "select", "where", "and", "or", "not", "null", "as", "on", "set", "values",
    "inner", "left", "right", "outer", "cross", "full", "group", "order", "by",
    "having", "union", "all", "distinct", "top", "with", "case", "when", "then",
    "else", "end", "begin", "if", "exists", "in", "is", "like", "between",
    "insert", "delete", "declare", "table", "view", "procedure", "function",
}


@dataclass(frozen=True)
class Token:
    """One SQL token, and the line it began on."""

    text: str
    line: int
    quoted: bool = False


@dataclass
class Declared:
    """An object a script creates."""

    name: str
    kind: str
    line: int
    end_line: int
    source: str | None = None
    refers: list[str] = field(default_factory=list)


@dataclass
class Script:
    """One file, and what it declares."""

    path: str
    objects: list[Declared] = field(default_factory=list)
    unplaced: int = 0


def tokenise(text: str) -> list[Token]:
    """Reads SQL into tokens: comments, strings and quoted names respected."""
    tokens: list[Token] = []
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

        if character == "-" and index + 1 < length and text[index + 1] == "-":
            while index < length and text[index] != "\n":
                index += 1
            continue

        if character == "/" and index + 1 < length and text[index + 1] == "*":
            end = text.find("*/", index + 2)

            if end < 0:
                break

            line += text.count("\n", index, end)
            index = end + 2
            continue

        # A string literal. Doubling the quote escapes it.
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

            continue

        # A quoted name: [dbo].[Table] or "Table" or `table`.
        if character in '["`':
            closing = {"[": "]", '"': '"', "`": "`"}[character]
            end = text.find(closing, index + 1)

            if end < 0:
                break

            tokens.append(Token(text[index + 1 : end], line, quoted=True))
            index = end + 1
            continue

        if character.isalpha() or character == "_" or character == "#":
            start = index

            while index < length and (
                text[index].isalnum() or text[index] in "_#$"
            ):
                index += 1

            tokens.append(Token(text[start:index], line))
            continue

        if character.isdigit():
            while index < length and (text[index].isalnum() or text[index] == "."):
                index += 1
            continue

        tokens.append(Token(character, line))
        index += 1

    return tokens


def read(path: Path, relative: str, with_source: bool) -> Script:
    """Reads one script: what it creates, and what each object names."""
    raw = path.read_bytes()
    text = None

    for encoding in ("utf-8-sig", "utf-8", "cp1251", "cp1252", "latin-1"):
        try:
            text = raw.decode(encoding)
            break
        except UnicodeDecodeError:
            continue

    if text is None:
        return Script(relative)

    tokens = tokenise(text)
    lines = text.splitlines()
    script = Script(relative)
    current: Declared | None = None
    index = 0

    while index < len(tokens):
        token = tokens[index]
        word = token.text.lower()

        following = tokens[index + 1] if index + 1 < len(tokens) else None
        pair = (word, following.text.lower()) if following else None

        if pair in CREATES:
            name, after = _name(tokens, index + 2)

            if name is None:
                script.unplaced += 1
                index += 2
                continue

            if current is not None:
                current.end_line = max(current.line, token.line - 1)
                _finish(current, lines, with_source)

            current = Declared(name, CREATES[pair], token.line, token.line)
            script.objects.append(current)
            index = after
            continue

        if word in REFERS and current is not None:
            name, after = _name(tokens, index + 1)

            if name is not None and name.lower() != current.name.lower():
                if name not in current.refers:
                    current.refers.append(name)

            index = after if after > index else index + 1
            continue

        index += 1

    if current is not None:
        current.end_line = max(current.line, len(lines))
        _finish(current, lines, with_source)

    return script


def _finish(declared: Declared, lines: list[str], with_source: bool) -> None:
    """Attaches the text of a declaration, cut when nobody would read it."""
    if not with_source:
        return

    limit = 400
    first, last = declared.line, declared.end_line
    taken = lines[first - 1 : first - 1 + min(last - first + 1, limit)]
    declared.source = "\n".join(taken)

    if last - first + 1 > limit:
        declared.source += f"\n… {last - first + 1 - limit} more lines"


def _name(tokens: list[Token], index: int) -> tuple[str | None, int]:
    """The object name at that position: `dbo.Thing`, `[dbo].[Thing]`, `Thing`.

    The last part is kept. A schema is not a different object, and treating
    `dbo.Orders` and `Orders` as two would split a table from its own uses.
    """
    parts: list[str] = []
    scan = index

    while scan < len(tokens):
        token = tokens[scan]

        if not token.quoted and token.text.lower() in NOT_A_NAME:
            break

        if token.quoted or token.text[0].isalpha() or token.text[0] in "_#":
            parts.append(token.text)
            scan += 1

            if scan < len(tokens) and tokens[scan].text == ".":
                scan += 1
                continue

            break

        break

    if not parts:
        return None, index

    return parts[-1], scan


def build(root: Path, scripts: list[Script], title: str, with_source: bool) -> dict:
    """Folders holding scripts holding the objects they declare."""
    declared: dict[str, list[str]] = {}

    for script in scripts:
        for obj in script.objects:
            declared.setdefault(obj.name.lower(), []).append(
                f"{script.path}::{obj.name}"
            )

    folders: dict[str, dict] = {}
    loose: list[dict] = []
    owner: dict[str, str] = {}

    for script in sorted(scripts, key=lambda s: s.path):
        node = {
            "id": script.path,
            "label": script.path.rsplit("/", 1)[-1],
            "kind": "script",
            "project": script.path,
            "children": [],
            "types": [
                {
                    "id": f"{script.path}::{o.name}",
                    "name": o.name,
                    "stereotype": o.kind,
                    "file": script.path,
                    "line": o.line,
                    "endLine": o.end_line,
                    "source": o.source if with_source else None,
                    "members": [],
                }
                for o in script.objects
            ],
        }

        for obj in script.objects:
            owner[f"{script.path}::{obj.name}"] = script.path

        directory = script.path.rsplit("/", 1)[0] if "/" in script.path else ""

        if directory:
            folder = folders.setdefault(
                directory,
                {
                    "id": directory,
                    "label": directory,
                    "kind": "folder",
                    "children": [],
                    "types": [],
                },
            )
            folder["children"].append(node)
        else:
            loose.append(node)

    edges: list[dict] = []
    seen: set[tuple[str, str, str]] = set()

    def add(source: str, target: str, kind: str) -> None:
        key = (source, target, kind)

        if source != target and key not in seen:
            seen.add(key)
            edges.append({"from": source, "to": target, "kind": kind})

    for script in scripts:
        for obj in script.objects:
            source = f"{script.path}::{obj.name}"

            for referenced in obj.refers:
                candidates = declared.get(referenced.lower(), [])

                # A name declared twice — the same table created in two
                # scripts — is dropped rather than guessed at.
                if len(candidates) != 1:
                    continue

                target = candidates[0]
                add(source, target, "dependency")

                if owner[source] != owner[target]:
                    add(owner[source], owner[target], "dependency")

    return {
        "title": title,
        "root": str(root),
        "nodes": [folders[k] for k in sorted(folders)] + loose,
        "edges": edges,
    }


def main() -> int:
    """Reads the repository named on the command line."""
    parser = argparse.ArgumentParser(
        description="Reads SQL scripts into the arch-viewer model."
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
        and not any(part in SKIP or part.startswith(".") for part in p.relative_to(root).parts)
    ]

    scripts = []
    unreadable = 0

    for path in sorted(files):
        try:
            scripts.append(read(path, str(path.relative_to(root)).replace("\\", "/"), with_source))
        except OSError:
            unreadable += 1

    model = build(root, scripts, arguments.title or root.name, with_source)
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

    objects = sum(len(s.objects) for s in scripts)
    unplaced = sum(s.unplaced for s in scripts)

    print(f"files      {len(files)}")
    print(f"scripts    {len(scripts)} read")
    print(f"types      {objects}")
    print(f"edges      {len(model['edges'])}")
    print(f"written    {arguments.out}")

    if unreadable:
        print(f"\n{unreadable} files would not open.")

    if unplaced:
        print(
            f"\n{unplaced} CREATE statements name something this reader "
            "could not take for an object name."
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
