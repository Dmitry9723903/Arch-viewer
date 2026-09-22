# Specification

What `arch-viewer` does, what it deliberately does not do, and the shape of
the files it reads and writes.

---

## Scope

A tool that reads a .NET repository and renders it as a graph the reader can
drill into: container → nested container → type → member → source text.
Dependencies that violate a declared rule are marked, and named.

Other ecosystems are served by writing another extractor. The viewer is not
modified for them — see [Planned order](#planned-order).

## Three parts

| Part | Reads | Writes | Knows |
|---|---|---|---|
| extractor | a repository | model file | one ecosystem |
| policy | — | — | one architecture, hand-written |
| viewer | model file | screen | neither |

The model file is the only contract between extractor and viewer.

### The one command is a fourth part, and knows nothing either

`archview all <repository>` finds which ecosystems a repository holds, builds
its .NET code, runs the extractors that apply and merges the models. It is
deliberately thin: it decides *which* extractors to run, never *how* any of
them reads anything, and it gains nothing by knowing a language.

Three properties it must keep:

- **It never invents coverage.** An ecosystem present but unreadable — a
  missing interpreter, an extractor that failed — is printed with the reason.
  Silence about a gap is the failure mode this whole tool exists to avoid.
- **It never contradicts itself.** The .NET pass reports the areas it did not
  read; when Python or PHP is about to be read by another pass, those areas
  are not named as unread. A report that disagrees with its own run teaches
  the reader to stop reading reports.
- **Each pass runs in its own process.** Not for tidiness: a stack overflow
  cannot be caught, and the first repository this command met crashed the
  .NET pass. In one process that fault took the Python and PHP maps with it.
  Now the pass is named, the signal that killed it is printed, and the rest
  of the repository is still drawn.
- **It is not the only way in.** Each extractor stays runnable alone, which is
  what keeps this part from accumulating language knowledge.

### Which copy of an assembly wins, and why it is not a preference

The .NET extractor resolves names against every assembly it can find: the
runtime's, the shared frameworks', and whatever lies in the repository. For a
name that occurs in more than one of those, **the runtime's copy wins**.

That order is a bug fix. A repository built years ago carries the facades of
its era — a `System.ComponentModel.Primitives.dll` that declares no type and
forwards them all to `System`. The runtime carries the facades of this era:
its `System.dll` forwards those same types back to
`System.ComponentModel.Primitives`. Prefer the repository's copy and the two
assemblies point at each other; chasing one forwarded type then recurses
until the stack ends.

Neither assembly is wrong. The pairing is, and the pairing is the tool's
choice. A repository copy is therefore used only for a name the runtime does
not have — the repository's own projects, and the packages it brought.

---

## Decisions

### 1. Metadata, not source text

Containers and edges come from `.csproj` XML via `System.Xml.Linq`. Types and
members come from assembly metadata via `MetadataLoadContext`, which loads
compiled assemblies for inspection without executing them. File and line come
from portable PDBs via `System.Reflection.Metadata`.

`System.Xml.Linq` and `System.Reflection.Metadata` are in the BCL.
`MetadataLoadContext` is one package, `System.Reflection.MetadataLoadContext`,
published by Microsoft as part of dotnet/runtime. It is the extractor's only
dependency; the alternative is several hundred lines of metadata table
parsing for the same result.

Regular expressions over source are rejected: they mishandle `partial`,
generics, nested types, file-scoped namespaces and `global using`, and they
fail without saying so.

### 2. Rules are two-dimensional

A node carries both a **container path** and a **role**. A model where each
component holds only a rank on one axis can say "this layer must not
reference that layer"; it cannot say "an adapter may reference *its own*
application layer and no other", because "its own" is a second axis.

Real layouts need the second axis, so it is in the model from the start
rather than retrofitted.

### 3. Nothing unmeasured is shown

Colour carries rule violations, which are computed from the model and the
policy.

Coverage, cyclomatic complexity and mutation results are shown **only** when
a snapshot that measured them is supplied, and are absent otherwise — absent
from the model, not zero in the model.

A constant presented as a measurement is worse than a blank, because this
tool is used in place of reading the code, and a number in that position is
trusted.

### 4. A page, not a desktop window

The viewer is a page in a browser. A desktop window would mean a UI
framework dependency for the same result.

---

## Model file

One JSON document: a tree of containers, types inside them, edges listed
separately.

```json
{
  "title": "Example",
  "root": "/path/to/repository",
  "nodes": [
    { "id": "core", "label": "Core", "kind": "group",
      "children": [
        { "id": "core/payroll", "label": "Payroll", "kind": "module",
          "children": [
            { "id": "Example.Payroll.Domain", "label": "Domain", "kind": "assembly",
              "role": "domain",
              "project": "src/Payroll/Example.Payroll.Domain/Example.Payroll.Domain.csproj",
              "types": [
                { "id": "Example.Payroll.Domain.Invoice", "name": "Invoice",
                  "stereotype": "record",
                  "file": "src/Payroll/Example.Payroll.Domain/Invoice.cs", "line": 14,
                  "members": [ { "text": "Total : Money", "line": 22 } ] }
              ] } ] } ] }
  ],
  "edges": [
    { "from": "Example.Payroll.Adapters", "to": "Example.Payroll.Application",
      "kind": "dependency", "violates": null },
    { "from": "Example.Payroll.Domain", "to": "Example.Timesheets.Domain",
      "kind": "dependency", "violates": "domain-is-isolated" }
  ]
}
```

Rules of the format:

- **`kind` is opaque to the viewer.** Any word an extractor likes. The viewer
  nests and expands; it does not interpret.
- **`role`** is the second axis: what the container is *within* its parent.
  Policy rules may match on it.
- **`visibility`** on a type: public, internal, protected or private. Every
  type declared in an assembly is in the model. A map of structure shows what
  is there; internal is a property of a type, not a reason to leave it out —
  and for an application project the internal types are the whole of it.
- **`violates` carries the rule's id**, not a boolean. A red arrow that
  cannot say which rule it broke is not actionable.
- **The same crossing may be recorded more than once.** An adapter reaching
  into another module appears as an edge between the projects and again as an
  edge between the two types that make the reference. Both are true; they are
  one boundary broken. Counts shown to the reader are crossings — a pair of
  containers and the rule between them — while the panel lists every reference
  that realises it, because that is what gets fixed. The terminal reports the
  same number as the screen, with the reference count beside it: two places
  counting differently is one of them lying.
- **`line` is optional.** Without PDBs there is no jump to source; everything
  else still works.
- **An edge names any container, not only a childless one.** An edge between
  projects stays an edge between projects when those projects grow namespace
  containers inside them. Rolling edges up to the visible boxes must therefore
  consider a node and everything under it, not only the leaves — assuming the
  ends of an edge are leaves is an assumption that stops being true the moment
  a project holds anything.
- Containers without a declared group are collected into one explicit
  container rather than dropped or attached to an arbitrary parent.

---

## Policy

A hand-written file beside the repository being read. It declares how to
group containers and which dependencies are forbidden.

### One property per level

A level of grouping answers one question, and every container on that level
answers the same one. Mixing them — some boxes on a row saying where a project
lives, others saying what it is — leaves the reader unable to tell which is
which, and nothing on the screen says there are two kinds of thing there.

This is easy to get wrong through a fallback. Group by a declared property,
fall back to the directory when it is absent, and the row now holds both. The
fix is not to push the fallback down a level: then the *next* row holds
modules under one parent and directories under another, which is the same
fault one floor lower. Group by the physical property first and the declared
one beneath it, so each level stays one question.

The cost is real and worth stating: whatever is on the first level is what the
reader sees first, and a level whose containers mostly have one child each is
a click that shows nothing new. Choose which of the two matters more for the
repository at hand; the tool does not choose.

### Where the structure lives

Two repositories can both be .NET and keep their structure in different
places. Where one boundary means one project, grouping projects is enough and
a type list per project is short. Where a handful of projects hold hundreds of
types, the structure is in the namespaces, and a project's flat list of two
hundred types tells the reader nothing.

`types` arranges a project's own types:

```json
"types": { "by": "namespace", "kind": "namespace", "trimAssemblyPrefix": true }
```

`trimAssemblyPrefix` drops the leading part of a namespace that merely repeats
the assembly's name: nesting six levels to reach the one that differs helps
nobody. Types declared in the assembly's root namespace stay on the project
itself.

Left out, types stay flat — which is right for a repository whose projects are
already the modules.

### A repository that ships itself as packages

A `PackageReference` naming a project of this same repository is an internal
dependency wearing a package's clothes. Some repositories build their platform
into a local feed and have their products consume it that way; reading only
`ProjectReference` there shows products depending on nothing, which is the
opposite of true. Such edges are recorded with kind `package`.

A package that is not a project here is left alone: it is not a boundary of
this repository.

### References between types

`"edges": true` inside `types` records what each type inherits, implements and
holds in its fields and properties. Without them the only edges are between
projects, and a repository of five projects has seven arrows — a sentence
anyone can recite, and nothing a rule can usefully constrain.

**Method parameters and return types are left out on purpose.** They would
multiply the edges several times over while adding the least: a type that
merely passes another one through is coupled to it far more loosely than one
that stores it. Generic arguments and array elements *are* followed, because
holding a `List<Invoice>` is holding an `Invoice`.

Only references between types the model already knows are recorded. A type
from a framework or a package is not a boundary of this repository and does
not become a node.

**References that stay inside a container are recorded but never judged.** A
type referring to itself, or to something nested within its own container,
crosses no boundary, and a rule about boundaries has nothing to say about it.
The edge is kept because "who sits on this base class" is worth answering and
is usually answered within one namespace.

### Borrowing what metadata cannot see

`"useGraphify": true` reads a graph another tool left in `graphify-out/` beside
the repository, and takes from it the references metadata cannot reach: what a
method accepts and returns, and what it calls. Off unless asked.

Such edges carry their own kinds — `signature`, `call` — and an `origin`
naming where they came from. They are drawn dashed and counted separately, and
a rule can decline them through `kinds`. Evidence of a different kind is
labelled as such rather than mixed in.

Only what that tool marked as extracted is taken; what it inferred is a guess,
and a guess does not belong beside a measurement. A reference whose target
name belongs to two types is dropped rather than attributed to one of them.

On one repository this turned a layer's 23 known references into 85: its
controllers were coupled to 58 request and response types that no reading of
metadata could have found, because they appear only in method signatures.

**It does not close the composition root.** That tool records the parameters
of a registration method and not the generic arguments of the registrations
inside it, so `AddScoped<IThing, Thing>()` remains invisible to both.

#### Areas it cannot read at all

This extractor knows .NET projects. A directory holding Python, TypeScript or
C++ contains no `.csproj` and is therefore absent from the map entirely — not
empty on it, absent from it.

A reader who sees such a directory in the file listing and not on the screen
cannot tell whether it was skipped, lost, or never there. The run therefore
names them: which areas hold code, in what language, and how much. Areas the
policy excludes are listed separately, because that is a decision someone made
rather than a limit of the tool.

Supporting such an area means writing an extractor for it that emits the same
model file. The viewer needs no changes: it has never known a language.

Models from several extractors are joined by `archview merge`, each becoming a
container named by its title. Joining is not blending: the parts keep their
own kinds, because a namespace and a folder are different things and a row
that mixes them unlabelled tells the reader nothing.

An extractor is written in the language it reads, with that language's own
parser — `ast` for Python, the compiler API for TypeScript. This is not a
preference: a pattern over text mishandles the ordinary constructs of every
language and fails silently, which is how a map comes to be confidently wrong.
`extractors/` holds them; the Python one is there.

#### What this does not see

Named here because an assumption left unwritten is how the reader is misled.

**Bodies of methods are read for one thing only: the types a call names as
its generic arguments.** `"readRegistrations": true` records them, with kind
`wires`.

That is how a composition root states its wiring. `AddScoped<IThing, Thing>()`
puts both types nowhere in the shape of the registering class — not in a
field, not in a base list — and only in the instruction that registers them.
Without this, a repository whose assembly lives in such calls looks far less
connected than it is, and the rule "nothing but the composition root may
reference the implementations" cannot be checked at all.

This is not interpreting behaviour. A generic argument is a type named in
metadata; the instruction is merely where it is named. Nothing else about the
method is read — not its calls' targets, not its logic, not its data flow.

Every instruction is stepped over by its true length. Guessing at the stride
would read an operand as an opcode and invent calls nobody wrote, which is why
the operand table is stated outright rather than approximated.

On one repository this found 61 registrations in its composition root — 28
interfaces and 27 implementations — where the map had shown none.

**The newest copy of an assembly is read, and older ones are counted aloud.**
A repository holds the same assembly once per configuration and again in every
project that references it. Which copy a directory walk meets first is an
accident, and reading a week-old build in silence is how a map comes to
describe code that no longer exists.

**Source is not shown for a file edited since the build.** Line numbers come
from the PDB of a build; the text comes from disk now. Where they disagree,
the panel would put a stale declaration next to a current body. No text is
better than contradictory text, and the run says how many types are affected.

Each file is compared against **the assembly that declares its type**, not
against the repository's oldest one. A build rewrites only the projects that
changed, so the oldest assembly anywhere is always some project nobody has
touched for months; measuring against it would suppress the text of every
file newer than that, which is most of them, all of them correct.

**Only portable PDBs are read.** A repository built on .NET Framework writes
Windows PDBs, and no source appears at all — the graph, the types and their
members are still there, the text is not.

**A reference obtained transitively looks the same as a direct one.** A
project declaring one project reference may still use a type that arrives
through it, and the edge is recorded against the type's real owner. This is a
feature — it is how the tool sees what project-level checks cannot — but it
means an edge here does not imply a `ProjectReference` there.

**Rules are applied to the containers, not the types.** A rule speaks about
boundaries, and a type's boundary is the container it sits in — so
`"subject": { "name": "Controllers" }` constrains every type in that
namespace, and the violating edge is reported between the two types that
actually make the reference.

`name` matches a label wherever it occurs. A repository with `Controllers`
under both its API and its tests has two containers by that name, and a rule
written about one silently governs the other. Where that is not intended,
`under` or `id` names the one meant.

```json
{
  "title": "Example",
  "discover": { "projects": "**/*.csproj", "exclude": ["artifacts/**"] },
  "group": [
    { "kind": "module", "from": "path-segment", "index": 1 },
    { "kind": "assembly", "from": "project", "role": "name-suffix" }
  ],
  "rules": [
    { "id": "domain-is-isolated",
      "subject": { "role": "domain" },
      "may-reference": [ { "role": "kernel" } ] },
    { "id": "adapters-serve-their-own-module",
      "subject": { "role": "adapters" },
      "may-reference": [ { "role": "application", "same": "module" } ] }
  ]
}
```

`same: "module"` is the second axis in use: the target must sit in the same
module container as the subject. Without it, the second rule cannot be
written at all.

Grouping sources available to `group`: `path-segment`, `name-part`,
`assembly-attribute` (attribute and constructor argument named), and
`literal` (the name given outright).

A level may carry a `fallback` — another source, used when the first yields
nothing. **A fallback must not substitute a different property for the one
that was asked for.** Falling back from "the block this assembly declares" to
"the directory it sits in" puts declared and physical grouping side by side on
one row, and the reader cannot tell which is which. Fall back to a `literal`
that says the declaration is missing; that is information, whereas a
substituted directory is a quiet lie.

---

## Screen

One page: tree on the left, graph in the middle, node panel on the right.

- **Drill-down.** Clicking a container expands its children in place. A
  breadcrumb shows the path and walks back up.
- **Anchors.** A reference whose other end is not on this level is drawn at
  the edge, named, with the number of references behind it, and leads there
  when clicked. Without them a level of value types looks unconnected when in
  truth everything above it depends on those types; drawn small, dashed and
  dim, so an anchor is never mistaken for a thing that lives here.
- **Edges.** Drawn between visible nodes; edges of collapsed children roll up
  into an edge between their containers.
- **Violations.** Red arrow plus the rule id, and a separate list so they can
  be read without hovering.
- **Type.** Declaration and members, read from metadata. A list, available
  without PDBs.
- **Source.** Lines of the file from the declaration to the end of the type,
  plus a `vscode://file/<path>:<line>` link that opens an editor there.

---

## Planned order

Capabilities arrive in this order, each usable on its own:

1. **Extractor** — containers and edges written to a model file.
2. **Viewer** — tree and edges, expanding on click.
3. **Policy** — rules applied, violations marked and named.
4. **Types** — types and members of each assembly.
5. **Source** — fragment in the panel, link into an editor.
6. **A second layout** — a differently organised repository, rendered
   without modifying the viewer. **Done.**

Step 6 was the real test of the design rather than a feature, and it passed:
arranging types by namespace for a repository whose structure lives below the
project level required no change to the viewer at all. A namespace container
is a container like any other, and the viewer never learned the word.

---

## Out of scope for now

Named, not forgotten:

- **metrics** — coverage, cyclomatic complexity, mutation results; each needs
  a tool that measures it, and until then they are absent (decision 3);
- **what-if** — proposing a regrouping before changing code; a good idea, but
  it is about changing structure, not showing it;
- **agent mailbox** — a file protocol for driving an assistant from the
  screen;
- **non-.NET extractors** — once the model file has been proven by two
  different .NET layouts;
- **concentric layout** — the ring drawing suits a strictly layered
  architecture and misrepresents anything else.
