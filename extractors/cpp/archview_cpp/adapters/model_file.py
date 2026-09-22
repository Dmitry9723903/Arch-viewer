"""Writing what was read into the model file every extractor here shares.

The model is the only thing the extractor and the viewer have in common, so
this is the one place that knows its shape. Nothing above it does.
"""

from __future__ import annotations

import json
from pathlib import Path

from ..application.read_repository import Reading
from ..domain.model import Declaration, Module


class ModelFile:
    """Turns a reading into the shared model, and into a page."""

    def __init__(self, title: str, with_source: bool = True) -> None:
        self._title = title
        self._with_source = with_source

    def model(self, reading: Reading) -> dict:
        """The model: folders holding modules holding types."""
        repository = reading.repository
        folders: dict[str, dict] = {}
        loose: list[dict] = []
        type_ids: dict[str, list[str]] = {}

        for module in sorted(repository.modules, key=lambda m: m.name):
            node = self._module(module, type_ids)

            if module.folder:
                folder = folders.setdefault(
                    module.folder,
                    {
                        "id": module.folder,
                        "label": module.folder,
                        "kind": "folder",
                        "children": [],
                        "types": [],
                    },
                )
                folder["children"].append(node)
            else:
                loose.append(node)

        edges = [
            {
                "from": str(reference.source),
                "to": str(reference.target),
                "kind": reference.kind,
            }
            for reference in repository.references
        ]

        edges.extend(self._inheritance(repository.modules, type_ids))

        return {
            "title": self._title,
            "root": repository.root,
            "nodes": [folders[k] for k in sorted(folders)] + loose,
            "edges": edges,
        }

    def _module(self, module: Module, type_ids: dict[str, list[str]]) -> dict:
        """One module, with the types declared in the files it owns."""
        types = []

        for declaration in sorted(
            module.declarations, key=lambda d: (d.path.value, d.span.first)
        ):
            identifier = f"{module.id}::{declaration.full_name}"
            types.append(self._type(identifier, declaration))
            type_ids.setdefault(declaration.name, []).append(identifier)

        return {
            "id": str(module.id),
            "label": module.name,
            "kind": "module",
            "role": module.role,
            "project": str(module.project) if module.project else None,
            "children": [],
            "types": types,
            # Why the box is empty, said by the reader that knows. Nothing
            # here needs building: the files were read and declared no type,
            # which happens in C — functions and no struct at all.
            "note": None
            if types
            else "This module was read; its files declare no type.",
        }

    def _type(self, identifier: str, declaration: Declaration) -> dict:
        """One declared type, as the model records a type."""
        return {
            "id": identifier,
            "name": declaration.full_name,
            "stereotype": declaration.stereotype,
            "file": str(declaration.path),
            "line": declaration.span.first,
            "endLine": declaration.span.last,
            "source": declaration.source if self._with_source else None,
            "members": [{"text": m, "line": declaration.span.first} for m in declaration.members],
        }

    def _inheritance(
        self, modules: tuple[Module, ...], type_ids: dict[str, list[str]]
    ) -> list[dict]:
        """An edge per base class this repository itself declares.

        A base whose name belongs to two types is dropped rather than
        guessed at: a wrong arrow is worse than a missing one, because a
        wrong one is acted upon.
        """
        edges: list[dict] = []
        seen: set[tuple[str, str]] = set()

        for module in modules:
            for declaration in module.declarations:
                source = f"{module.id}::{declaration.full_name}"

                for base in declaration.bases:
                    candidates = type_ids.get(base.rsplit("::", 1)[-1], [])

                    if len(candidates) != 1:
                        continue

                    target = candidates[0]
                    key = (source, target)

                    if source == target or key in seen:
                        continue

                    seen.add(key)
                    edges.append({"from": source, "to": target, "kind": "inheritance"})

        return edges

    def write(self, model: dict, out: Path, viewer: Path | None) -> None:
        """Writes the model, and the self-contained page beside it."""
        text = json.dumps(model, ensure_ascii=False, indent=2)
        out.with_suffix(".json").write_text(text, encoding="utf-8")

        if out.suffix != ".html":
            return

        if viewer is None or not viewer.is_file():
            raise FileNotFoundError(f"No viewer at {viewer}")

        page = viewer.read_text(encoding="utf-8")
        out.write_text(page.replace("/*MODEL*/null", text, 1), encoding="utf-8")
