# TypeScript and JavaScript extractor

Reads a TypeScript or JavaScript repository into the model file the viewer
expects.

```
node archview-ts.mjs <repository> --out arch.html
```

Writes `arch.html` — the shared viewer with the model inside — and `arch.json`
beside it.

## The compiler is not a dependency of this repository

Structure is read with the TypeScript compiler, and the copy used is **the
target repository's own**: a TypeScript project already has one in its
`node_modules`, and borrowing it means this tool adds no dependency and always
parses with the same version the project itself compiles with.

Looked for in `node_modules/typescript`, then `client/`, then `frontend/`, then
wherever this extractor can resolve one. If none is found, the run says so and
stops rather than falling back to guessing.

## What it reads

| On the map | From |
|---|---|
| folders | directories |
| modules | files |
| declarations | `class`, `interface`, `enum`, `type` |
| `dependency` edges | `import` and re-`export`, resolved to files of this repository |
| `inheritance` / `implements` edges | `extends` and `implements` clauses |

A declaration's fragment starts at its documentation comment, which was
written for it. Visibility is `public` when exported and `internal` otherwise —
the nearest thing this language has to the distinction.

## What it does not read

**Imports of packages.** Only relative imports are resolved: a package is not
a boundary of this repository.

**Dynamic imports.** `import(name)` computes its target at run time.

**Functions.** Modules and declarations are the containers; a module of
exported functions shows as a module with no types, which is true.

**Names shared by two declarations.** An `extends` clause naming something
declared twice is dropped rather than attributed to one of them.

**`.d.ts` files**, which describe other people's code rather than this
repository's.
