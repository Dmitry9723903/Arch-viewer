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
- **`violates` carries the rule's id**, not a boolean. A red arrow that
  cannot say which rule it broke is not actionable.
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

#### What this does not see

Named here because an assumption left unwritten is how the reader is misled.

**Bodies of methods are not read.** Only what a type inherits, implements and
holds. A composition root that registers thirty implementations produces no
edges at all: it names them inside a method. So a rule of the form "nothing
but the composition root may reference the implementations" cannot be checked
by this model, and a repository whose wiring lives in `AddScoped` calls will
look less connected than it is.

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
