# Reading C and C++

```bash
python3 archview_cpp.py <repository> --out arch.html [--title name]
                        [--no-source] [--tokens]
```

Needs Python 3.10 or newer. Nothing has to be built first.

## Where the structure comes from

Not from the source. A Visual C++ repository states its modules exactly —
`.vcxproj` and the older `.vcproj` list the files each one compiles — and
states its dependencies exactly too, as `#include "…"`. Both are read as
what they are.

Only the inside of a module needs C++ parsed, and that is the one part where
a reader can be wrong.

A repository with no project files still has modules; they are simply not
written down. Then directories stand in for them, and the run says that is
what happened.

## Two readers

| When | Reader |
|---|---|
| `clang` bindings installed | clang: resolved bases, member types, declarations inside templates |
| otherwise, or with `--tokens` | this tool's own C++ tokeniser |

The tokeniser is not a regular expression over lines. It reads comments,
string, character and raw-string literals and preprocessor lines as the
language does, and declarations are recognised from the token stream. A
brace inside a string is not a brace; a keyword inside a comment is not a
keyword.

What it cannot see it says: a declaration written inside a macro is
invisible to it, and the run prints how many preprocessor lines it passed
over rather than implying they held nothing.

## What the run tells you

Counts for everything it could not do: includes that name a file outside the
repository, files belonging to no project, files a project names in a case
the disk does not use, files that would not decode. None of it is passed
over in silence.

## Rules

This extractor does not judge. A policy is applied afterwards, by the one
implementation of the rules:

```bash
archview judge arch.json --policy .arch-viewer/policy.json --out arch.html
```

`archview all` does both for you.
