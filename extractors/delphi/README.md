# Reading Delphi and Object Pascal

```bash
python3 archview_delphi.py <repository> --out arch.html [--title name] [--no-source]
```

Needs Python 3.10 or newer. Nothing has to be compiled.

Pascal states its structure in the language itself, which few languages do:
a file is a `unit`, and a unit says what it uses. The modules and the edges
between them are not inferred from directories or from a project file —
they are read from the first two declarations of every file.

Reads classes, records, interfaces and enumerations, with their fields,
methods, properties and values, and the class each one derives from.

The text is tokenised, not matched with expressions. Pascal has three kinds
of comment — `{ }`, `(* *)` and `//` — and a compiler directive looks
exactly like the first with a `$` after the brace. A reader that did not
know the difference would take `{$IFDEF WIN32}` for a comment, and `end`
inside a string for the end of a class.

What it does not see it says: a declaration written inside a compiler
directive is invisible to it, and the run counts the directives it passed
over. A unit used but not held by the repository — `SysUtils`, `Classes`,
or a third-party one — is counted and not drawn, because a box for
something this repository does not contain is a box nobody can open.

An alias, `TKode = String[41]`, is a name for a type and not a type; it is
not drawn either, for the same reason.
