#!/usr/bin/env python3
"""Reads a Python repository and writes the model file the viewer expects.

The viewer knows no language: it nests containers, expands them and draws
arrows between whatever the model names.  Supporting Python therefore means
writing this file and nothing else.

Structure is read with the `ast` module of the standard library rather than
with regular expressions.  A pattern over text mishandles decorators,
conditional imports, aliases and nested classes, and it fails silently — the
graph simply comes out wrong and nothing says so.
"""

from __future__ import annotations

import argparse
import ast
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

SKIP = {
    "__pycache__", ".git", ".venv", "venv", "env", "node_modules",
    "migrations", "site-packages", ".mypy_cache", ".pytest_cache",
}


@dataclass
class TypeNode:
    """One class, as the model records it."""

    id: str
    name: str
    stereotype: str
    visibility: str
    file: str
    line: int
    end_line: int
    source: str
    members: list[dict] = field(default_factory=list)

    # Base classes as written, kept out of the model file: they are how
    # inheritance edges are found, not something the viewer displays.
    bases_raw: list[str] = field(default_factory=list)


@dataclass
class Module:
    """One Python module: a file, the classes in it, and what it imports."""

    dotted: str
    path: Path
    relative: str
    package: str
    classes: list[TypeNode] = field(default_factory=list)
    imports: set[str] = field(default_factory=set)


def dotted_name(root: Path, path: Path) -> str:
    """The import name a file answers to."""
    parts = path.relative_to(root).with_suffix("").parts
    if parts and parts[-1] == "__init__":
        parts = parts[:-1]
    return ".".join(parts)


def visibility(name: str) -> str:
    """Python states visibility by convention, and the convention is the truth."""
    if name.startswith("__") and not name.endswith("__"):
        return "private"
    return "internal" if name.startswith("_") else "public"


def stereotype(node: ast.ClassDef) -> str:
    """What kind of class this is, as far as the declaration says."""
    for base in node.bases:
        rendered = ast.unparse(base)
        if rendered.endswith("Enum") or rendered.endswith("IntEnum"):
            return "enum"
        if rendered in ("Protocol", "ABC") or rendered.endswith(".Protocol"):
            return "interface"
    for keyword in node.keywords:
        if keyword.arg == "metaclass" and "ABCMeta" in ast.unparse(keyword.value):
            return "interface"
    for decorator in node.decorator_list:
        if "dataclass" in ast.unparse(decorator):
            return "record"
    return "class"


def members(node: ast.ClassDef) -> list[dict]:
    """Methods and annotated attributes, in the order they are written."""
    found: list[dict] = []
    for item in node.body:
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            args = ", ".join(
                a.arg for a in item.args.args if a.arg not in ("self", "cls")
            )
            returns = f" -> {ast.unparse(item.returns)}" if item.returns else ""
            found.append({"text": f"{item.name}({args}){returns}", "line": item.lineno})
        elif isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name):
            found.append(
                {
                    "text": f"{ast.unparse(item.annotation)} {item.target.id}",
                    "line": item.lineno,
                }
            )
        elif isinstance(item, ast.Assign):
            for target in item.targets:
                if isinstance(target, ast.Name):
                    found.append({"text": target.id, "line": item.lineno})
    return found


def fragment(lines: list[str], start: int, end: int, limit: int = 400) -> str:
    """The lines of a declaration, cut when it is longer than anyone will read.

    A class of several thousand lines is a fact about the repository, not
    something to carry inside a page — source text is most of a map's weight,
    and a page that will not open shows nothing at all. The cut is stated.
    """
    count = end - start + 1
    taken = "\n".join(lines[start - 1 : start - 1 + min(count, limit)])

    if count <= limit:
        return taken

    return f"{taken}\n\n… {count - limit} more lines; open the file to read them."


def read_module(root: Path, path: Path) -> Module | None:
    """Parses one file.  A file that will not parse is reported, not skipped."""
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as problem:
        print(f"  unreadable: {path}: {problem}", file=sys.stderr)
        return None

    try:
        tree = ast.parse(text, filename=str(path))
    except SyntaxError as problem:
        print(f"  will not parse: {path}: {problem}", file=sys.stderr)
        return None

    dotted = dotted_name(root, path)
    relative = str(path.relative_to(root)).replace("\\", "/")
    lines = text.splitlines()
    module = Module(
        dotted=dotted,
        path=path,
        relative=relative,
        package=dotted.rsplit(".", 1)[0] if "." in dotted else "",
    )

    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                module.imports.add(alias.name)
        elif isinstance(node, ast.ImportFrom):
            if node.level:
                # A relative import names a place, not a package: resolve it
                # against this module or the graph will have holes exactly
                # where a package is most tightly bound.
                base = dotted.rsplit(".", node.level) if node.level else [dotted]
                prefix = base[0] if base else ""
                target = f"{prefix}.{node.module}" if node.module else prefix
            else:
                target = node.module or ""
            if target:
                module.imports.add(target)
                for alias in node.names:
                    module.imports.add(f"{target}.{alias.name}")

    # Functions declared at module level are types of the map too. In a
    # language where most code lives outside classes — a test suite of `def
    # test_…`, a module of helpers — showing only classes shows a module as
    # empty when it is not.
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            start = min([node.lineno] + [d.lineno for d in node.decorator_list])
            end = node.end_lineno or start
            args = ", ".join(a.arg for a in node.args.args)
            returns = f" -> {ast.unparse(node.returns)}" if node.returns else ""
            module.classes.append(
                TypeNode(
                    id=f"{dotted}.{node.name}" if dotted else node.name,
                    name=node.name,
                    stereotype="function",
                    visibility=visibility(node.name),
                    file=relative,
                    line=start,
                    end_line=end,
                    source=fragment(lines, start, end),
                    members=[{"text": f"({args}){returns}", "line": node.lineno}],
                )
            )
            continue

        if not isinstance(node, ast.ClassDef):
            continue
        start = min(
            [node.lineno] + [d.lineno for d in node.decorator_list]
        )
        # A docstring above the class is written for it and belongs with it.
        while start > 1 and lines[start - 2].lstrip().startswith("#"):
            start -= 1
        end = node.end_lineno or start
        module.classes.append(
            TypeNode(
                id=f"{dotted}.{node.name}" if dotted else node.name,
                name=node.name,
                stereotype=stereotype(node),
                visibility=visibility(node.name),
                file=relative,
                line=start,
                end_line=end,
                source=fragment(lines, start, end),
                members=members(node),
                bases_raw=[ast.unparse(b) for b in node.bases],
            )
        )

    return module


def container_for(module: Module, depth: int) -> list[str]:
    """The chain of packages a module sits in, cut at the asked depth."""
    parts = module.dotted.split(".")[:-1] if "." in module.dotted else []
    return parts[:depth] if depth > 0 else parts


def build(root: Path, modules: list[Module], title: str, depth: int) -> dict:
    """Assembles the model: packages nested, modules inside, classes in them."""
    by_dotted = {m.dotted: m for m in modules}
    owner: dict[str, str] = {}
    roots: dict[str, dict] = {}

    def container(path: list[str], kind: str) -> dict:
        here, at = roots, ""
        node = None
        for part in path:
            at = f"{at}.{part}" if at else part
            if at not in here:
                here[at] = {
                    "id": at,
                    "label": part,
                    "kind": kind,
                    "children": {},
                    "types": [],
                }
            node = here[at]
            here = node["children"]
        return node

    for module in modules:
        path = container_for(module, depth)

        # A package's __init__ answers to the package's own name. It is that
        # container, not a module beside it: giving it a node of its own would
        # put two things with one name on the map.
        if module.path.name == "__init__.py" and module.dotted:
            here = container(module.dotted.split("."), "package")
            here["types"].extend(
                {k: v for k, v in vars(c).items() if k != "bases_raw"}
                for c in module.classes
            )
            for c in module.classes:
                owner[c.id] = here["id"]
            here["project"] = module.relative
            continue

        parent = container(path, "package") if path else None
        leaf = {
            "id": module.dotted or module.relative,
            "label": module.dotted.rsplit(".", 1)[-1] if module.dotted else module.relative,
            "kind": "module",
            "role": (module.dotted.rsplit(".", 1)[-1] if module.dotted else "").lower(),
            "project": module.relative,
            "children": {},
            "types": [
                {k: v for k, v in vars(c).items() if k != "bases_raw"}
                for c in module.classes
            ],
        }
        for c in module.classes:
            owner[c.id] = leaf["id"]
        if parent is None:
            roots[leaf["id"]] = leaf
        else:
            parent["children"][leaf["id"]] = leaf

    edges: list[dict] = []
    seen: set[tuple[str, str, str]] = set()

    def add(source: str, target: str, kind: str) -> None:
        key = (source, target, kind)
        if source != target and key not in seen:
            seen.add(key)
            edges.append({"from": source, "to": target, "kind": kind})

    # Imports are edges between modules; a base class is an edge between
    # types, and the two answer different questions.
    for module in modules:
        for imported in module.imports:
            target = imported
            while target and target not in by_dotted:
                target = target.rsplit(".", 1)[0] if "." in target else ""
            if target and target != module.dotted:
                add(module.dotted, target, "dependency")

    known = {c.id: c for m in modules for c in m.classes}
    by_name: dict[str, list[str]] = {}
    for identifier, node in known.items():
        by_name.setdefault(node.name, []).append(identifier)

    for module in modules:
        for node in module.classes:
            for base in node.bases_raw:
                simple = base.rsplit(".", 1)[-1]
                candidates = by_name.get(simple, [])
                # A name shared by two classes is dropped rather than guessed
                # at: a wrong arrow is worse than a missing one.
                if len(candidates) == 1:
                    add(node.id, candidates[0], "inheritance")

    def freeze(node: dict) -> dict:
        node["children"] = [freeze(c) for c in node["children"].values()]
        return node

    return {
        "title": title,
        "root": str(root),
        "nodes": [freeze(n) for n in roots.values()],
        "edges": edges,
    }


def page(model: dict, viewer: Path) -> str:
    """The self-contained page: the shared viewer with this model inside."""
    template = viewer.read_text(encoding="utf-8")
    return template.replace(
        "/*MODEL*/null", json.dumps(model, ensure_ascii=False, indent=2), 1
    )


def strip_source(nodes: list[dict]) -> None:
    """Drops the source text, keeping where each type is declared.

    The text is most of a large map's weight, and a page that will not open
    shows nothing at all. The file and the lines cost a few bytes and are what
    a reader needs to go and look at the code itself.
    """
    for node in nodes:
        for declared in node.get("types", []):
            declared.pop("source", None)
        strip_source(node.get("children", []))


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reads a Python repository into the arch-viewer model."
    )
    parser.add_argument("repository", help="Repository root.")
    parser.add_argument("--out", default="arch.json", help="Where to write the model.")
    parser.add_argument("--title", default=None, help="Name shown on screen.")
    parser.add_argument(
        "--viewer",
        default=None,
        help="The viewer page to embed the model into. Defaults to the one in this repository.",
    )
    parser.add_argument(
        "--no-source",
        action="store_true",
        help="Leave the source text out; keep the structure and the line numbers.",
    )
    parser.add_argument(
        "--depth",
        type=int,
        default=2,
        help="How many package levels to nest before listing modules.",
    )
    arguments = parser.parse_args()

    root = Path(arguments.repository).resolve()
    if not root.is_dir():
        print(f"No such directory: {root}", file=sys.stderr)
        return 1

    everything = list(root.rglob("*.py"))
    files = [
        p
        for p in everything
        if not any(part in SKIP for part in p.relative_to(root).parts)
    ]

    # What was passed over, and under which name. A repository of seven
    # hundred files read as four hundred and forty-two says nothing about
    # the other two hundred and seventy unless it is made to: they were
    # Django migrations, which is a good reason and still a reason worth
    # stating rather than assuming the reader guesses it.
    passed: dict[str, int] = {}

    for path in everything:
        for part in path.relative_to(root).parts:
            if part in SKIP:
                passed[part] = passed.get(part, 0) + 1
                break

    modules = [m for m in (read_module(root, p) for p in files) if m is not None]
    model = build(root, modules, arguments.title or root.name, arguments.depth)

    if arguments.no_source:
        strip_source(model["nodes"])

    out = Path(arguments.out)
    out.with_suffix(".json").write_text(
        json.dumps(model, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    viewer = Path(arguments.viewer) if arguments.viewer else (
        Path(__file__).resolve().parents[2] / "viewer" / "index.html"
    )

    if out.suffix == ".html" and viewer.is_file():
        out.write_text(page(model, viewer), encoding="utf-8")
    elif out.suffix == ".html":
        print(f"No viewer at {viewer}; wrote the model only.", file=sys.stderr)

    declared = sum(len(m.classes) for m in modules)
    print(f"files      {len(files)}")
    print(f"modules    {len(modules)} read")
    print(f"types      {declared}")
    print(f"edges      {len(model['edges'])}")
    print(f"written    {arguments.out}")

    if passed:
        print()
        named = ", ".join(
            f"{count} in {name}/" for name, count in sorted(passed.items(), key=lambda x: -x[1])
        )
        print(f"{sum(passed.values())} files were passed over: {named}")

    unreadable = len(files) - len(modules)
    if unreadable:
        print(f"\n{unreadable} files were not read; they are named above.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
