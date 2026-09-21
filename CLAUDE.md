# Working in this repository

Conventions for `arch-viewer`. They apply to everyone, human or assistant.
Read [README.md](README.md) first for what the tool is, and
[docs/spec.md](docs/spec.md) for what it is supposed to do.

## Order of work: spec, then plan, then code

A milestone starts with a written spec — what changes and how it will be
checked. Then a plan with the actual code to be written. Only then the code.

This is not ceremony. The tool's value is that it shows structure honestly,
and structure decided while typing is structure nobody decided.

A milestone is complete when its **check** passes, not when the code is
written. The plan for a milestone names that check before any code exists.

## The model file is the contract

The extractor writes a model; the viewer reads it. That file is the only
thing they share, and its shape is specified in
[docs/spec.md](docs/spec.md).

Two consequences, both binding:

- **The viewer never learns a language.** No `.csproj`, no assembly, no
  Python import, no file extension may be mentioned in viewer code. If the
  viewer needs to know, the model is missing a field — add the field.
- **`kind` is opaque to the viewer.** An extractor may emit any container
  kind it likes. The viewer nests and expands; it does not interpret.

When a second ecosystem renders without touching the viewer, the split
worked. When it doesn't, that is a defect in this repository, not a quirk of
the new ecosystem.

## Never display what was not measured

Colour and badges may carry only values that were computed or measured:
a rule violation, a count of children, a real coverage figure from a real
snapshot.

A hardcoded metric is not a placeholder to fill in later. It is worse than
an empty field, because this tool exists to be trusted in place of reading
the code. If a metric has no source, it is absent from the model and absent
from the screen.

## Read metadata, not source text

Structure comes from `.csproj` XML, assembly metadata (`MetadataLoadContext`,
which reads without executing) and portable PDBs. Not from regular
expressions over source files.

Regex "parsers" mishandle `partial` types, generics, nested types,
file-scoped namespaces and `global using`, and they fail silently — the
graph simply comes out wrong, and nothing says so. An extractor for a
language without usable metadata must use that language's real parser.

## Dependencies

This tool inspects other people's repositories, so what it drags in matters
more than usual.

Before adding a package, say out loud in the change description: what it
does that a small amount of obvious code would not, how much it pulls in
transitively, and what happens if it is abandoned. Small and easily written
by hand — write it by hand.

The extractor has exactly one dependency — `System.Reflection.MetadataLoadContext`,
from Microsoft — and the viewer has none: no framework, no charting library,
no build step. Keep both that way unless there is a stated reason.

## Authorship of commits

Commits, tags, pull requests and releases here name the person who made the
decision. They do **not** carry `Co-Authored-By` trailers for AI assistants,
"Generated with …" lines, or any similar attribution to a tool.

The reason is accountability, not preference: a change log answers "who
decided this and can explain it". A tool cannot answer that, and listing it
as an author blurs who can.

Using an assistant is expected and fine — say so in the pull request prose
if it is relevant. Keep it out of the commit trailer.

Some assistants add such a trailer by default. Remove it before committing.

## Do not commit local state

`.claude/settings.local.json` and anything else machine-specific stays out.
It is in `.gitignore`; do not add exceptions.

Shared assistant configuration — skills, project instructions — is committed
on purpose, so everyone who clones gets the same behaviour.

## C# conventions

- Warnings are errors. Nullable reference types enabled.
- Public and internal types and members carry XML documentation, in the
  multi-line form.
- Private fields `_camelCase`; types and members `PascalCase`.
- Prefer `int`/`string` over `Int32`/`String`. No `this.` qualification.

## Examples

`examples/` holds a small invented .NET solution used by the checks. It
contains one deliberate dependency violation — that is the point of it, and
it must not be "fixed".
