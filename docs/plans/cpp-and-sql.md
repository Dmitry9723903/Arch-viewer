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

A milestone is complete when these pass. Marked as they stand.

1. **Passes.** `examples/cpp` — four modules in three folders, one
   deliberate include that crosses a boundary the policy forbids. Read by
   one command: 7 types, 4 references between modules, and exactly one
   crossing, drawn red and named `adapters-serve-their-own-module`.
2. **Not run.** The two readers compared on one example. clang is not
   installed on the machine this was written on, and installing a compiler
   front-end is a decision to take deliberately rather than in passing. The
   clang reader is written and is not claimed to have been verified.
3. **Passes.** A legacy repository of 1,885 C/C++ files: 52 modules from 37
   Visual C++ projects, 5,054 declarations, 61 references between modules,
   in 53 seconds. It states what it did not read: 642 of 5,222 includes name
   a file outside the repository, 73,140 preprocessor lines were passed
   over, 608 source files belong to no project, and 94 files are named in a
   project in a different case than the disk uses.
4. **Passes.** The viewer was not modified by one line. One field of the
   model changed: a type's `visibility` became optional, because C++ gives a
   type none and writing "public" would have put on the screen a fact nobody
   measured.

## Order of work

1. Model and domain, with the checks' example written first.
2. `.vcproj` / `.vcxproj` / `.sln` reading — modules and their files.
3. `#include` reading — edges between modules.
4. The tokeniser — declarations, spans, stereotypes.
5. The clang reader — the same declarations, with members and base classes.
6. SQL, which is its own small extractor and shares none of the above.
7. Registration in the one command, and the README.

## What was learned on the way, and is now written in the code

**A repository written on Windows names its own files without regard to
case.** A project lists `StdAfx.h`, an include asks for `stdafx.h`, and to
that compiler they are one file. Read on a system where they are two, 320
files would not open and a third of the includes pointed at nothing —
quietly, which is the part that matters.

**Skipping ahead to the next brace is not parsing.** `struct soap *soap` in
a parameter list looks like a class head to a reader willing to ignore the
`*`, and the brace it reaches is the function's body. That cost nine
thousand phantom types, named after whatever identifier stood last before
the body: `type`, `a`, `n`. Only what a class head may hold is now allowed
between the keyword and the brace.

**An invariant in the domain found a real defect before any map was drawn.**
Two projects of one name — a `Test` beside every library — were refused by
the repository rather than silently merged. The name is the author's; the
identity gained the directory that tells them apart.
