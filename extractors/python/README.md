# Python extractor

Reads a Python repository into the model file the viewer expects.

```
python3 archview_python.py <repository> --out arch.html
```

Writes `arch.html` — the shared viewer with the model inside — and `arch.json`
beside it. No dependencies: structure is read with `ast` from the standard
library.

## What it reads

| On the map | From |
|---|---|
| packages, nested | directories holding `__init__.py` |
| modules | files |
| classes | `class` statements, with their decorators and docstring |
| `dependency` edges | `import` and `from … import`, resolved to modules of this repository |
| `inheritance` edges | base classes, when the name belongs to exactly one class here |

Stereotypes are read from the declaration: a class deriving from `Enum` is an
enum, one deriving from `Protocol` or `ABC` an interface, one decorated
`@dataclass` a record. Visibility follows the language's convention —
`_name` is internal, `__name` private.

## What it does not read

**Function-level structure.** Modules and classes are the containers; a
module of free functions shows as a module with no types, which is true.

**Dynamic imports.** `importlib.import_module(name)` computes its target at
run time, and no reading of the text can know it.

**Names shared by two classes.** A base class whose simple name belongs to
more than one class here is dropped rather than attributed to one of them: a
wrong arrow is worse than a missing one.

**Migrations and virtual environments** are skipped by name. Django
migrations are generated, and a hundred of them say nothing about design.

## Why `ast` and not a pattern

A regular expression over text mishandles decorators, conditional imports,
aliases and nested classes, and it fails without saying so — the graph simply
comes out wrong. `ast` is the language's own parser and ships with it.
