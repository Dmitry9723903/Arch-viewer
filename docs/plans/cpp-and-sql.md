# Plan: reading C, C++ and SQL

Written before the code, as this repository requires. The check is named
first; the milestone is complete when it passes, not when the code exists.

## Why these two, and in this order

A legacy repository of 2,000 source files was read with the extractors that
exist. They found 53 C# files, 13 Python and 4 JavaScript — and named 1,879
files of C and C++ as unread, which was honest and useless. The structure of
that repository is C++, and until it is read the map of it is a map of its
margins.

SQL is 22 files in the same repository. It is included because it is small
and because leaving it out would mean naming it unread for a second time.

## What C++ structure is made of, and where it already exists

The important finding, from measuring rather than assuming:

| What | Where it already is | Exact? |
|---|---|---|
| modules | 37 `.vcproj` / `.vcxproj` files, 19 of them in one `.sln` | yes — XML, a list of files |
| dependencies between modules | 2,066 `#include "…"` directives | yes — a file naming a file |
| types | ~5,700 declarations in the source text | no — needs a parser |
| the role of a type | base class, MFC macros (284 files) | from the text, so as exact as the parse |

So two thirds of the map — the part the screen exists for — comes from
metadata that is already written down. Only the inside of a module needs the
language parsed. That order is the plan.

## Two readers, as in PHP, and what the second one must be

`clang` when its Python bindings are installed; a reader of this repository's
own otherwise. This repository forbids regex "parsers" for a stated reason:
they mishandle the hard cases and fail **silently**, so the graph is simply
wrong and nothing says so.

The fallback is therefore a real tokeniser — one pass over characters that
knows comments, string, character and raw-string literals, and preprocessor
lines — and declarations are recognised from the token stream, not from
lines of text. The same shape as `SourceText.cs` uses for C# spans, and as
PHP's `token_get_all` path uses.

It will still miss declarations hidden inside macros, and it will not resolve
templates. **That is stated in the run, with a count**, in the manner this
tool already states what it did not read.

## Layout

Written to the strict DDD/Clean profile, by decision.

```
extractors/cpp/
  archview_cpp.py            entry point and composition root
  archview_cpp/
    domain/                  concepts and rules; imports nothing below
    application/             the use case, and the ports it needs
    adapters/                vcproj, sln, clang, tokeniser, model file
```

Dependency direction inward: adapters know the application's ports, the
application knows the domain, the domain knows neither. The composition root
is the only place that names a concrete adapter.

## The checks

A milestone is complete when these pass.

1. `examples/cpp` — a small invented solution, two modules, one deliberate
   include that crosses a boundary the policy forbids. Read it: the crossing
   is drawn red and named. This mirrors `examples/solution` exactly.
2. The same example read **without** clang installed gives the same modules,
   the same edges, and the same count of declarations. The two readers are
   compared, and a difference is a defect in one of them.
3. The legacy repository of 1,879 C/C++ files is read without crashing, and
   the run states how many declarations each reader found and how many files
   it could not read.
4. The viewer is **not modified by one line**. If it must be, the model is
   missing a field.

## Order of work

1. Model and domain, with the checks' example written first.
2. `.vcproj` / `.vcxproj` / `.sln` reading — modules and their files.
3. `#include` reading — edges between modules.
4. The tokeniser — declarations, spans, stereotypes.
5. The clang reader — the same declarations, with members and base classes.
6. SQL, which is its own small extractor and shares none of the above.
7. Registration in the one command, and the README.
