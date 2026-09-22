#!/usr/bin/env node
// Reads a TypeScript or JavaScript repository into the model file the viewer
// expects.
//
// The viewer knows no language: it nests containers, expands them and draws
// arrows between whatever the model names. Supporting this ecosystem
// therefore means writing this file and nothing else.
//
// Structure comes from the TypeScript compiler, not from patterns over text.
// A pattern mishandles generics, decorators, `type` aliases, re-exports and
// JSX, and it fails silently — the graph simply comes out wrong. The compiler
// is not a dependency of this repository: a TypeScript project already has
// one, and that is the copy used.

import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const SKIP = new Set([
  'node_modules', 'dist', 'build', 'out', 'coverage', '.git', '.next',
  '.svelte-kit', '.turbo', 'vendor',
]);

const EXTENSIONS = ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs'];

/** Finds the compiler: the target repository's copy first, then ours. */
async function loadCompiler(root) {
  const candidates = [
    path.join(root, 'node_modules', 'typescript'),
    path.join(root, 'client', 'node_modules', 'typescript'),
    path.join(root, 'frontend', 'node_modules', 'typescript'),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(path.join(candidate, 'package.json'))) {
      const module = await import(pathToFileURL(path.join(candidate, 'lib', 'typescript.js')));
      return { ts: module.default ?? module, from: candidate };
    }
  }

  try {
    const require = createRequire(import.meta.url);
    return { ts: require('typescript'), from: 'this extractor' };
  } catch {
    return null;
  }
}

function walk(directory, found = []) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name.startsWith('.') || SKIP.has(entry.name)) continue;
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(full, found);
    else if (EXTENSIONS.includes(path.extname(entry.name))
             && !entry.name.endsWith('.d.ts')) found.push(full);
  }
  return found;
}

/** What a declaration is, as far as the syntax says. */
function stereotype(ts, node) {
  if (ts.isInterfaceDeclaration(node)) return 'interface';
  if (ts.isEnumDeclaration(node)) return 'enum';
  if (ts.isTypeAliasDeclaration(node)) return 'type';
  if (ts.isClassDeclaration(node)) {
    const abstract = node.modifiers?.some(m => m.kind === ts.SyntaxKind.AbstractKeyword);
    return abstract ? 'abstract' : 'class';
  }
  if (ts.isFunctionDeclaration(node)) return 'function';
  return 'class';
}

/** Exported or not: the nearest thing this language has to visibility. */
function visibility(ts, node) {
  return node.modifiers?.some(m => m.kind === ts.SyntaxKind.ExportKeyword)
    ? 'public'
    : 'internal';
}

function members(ts, node, text) {
  if (!node.members) return [];
  const found = [];
  for (const member of node.members) {
    const name = member.name?.getText?.(text);
    if (!name) continue;
    if (ts.isMethodDeclaration(member) || ts.isMethodSignature(member)) {
      const args = (member.parameters ?? []).map(p => p.name.getText(text)).join(', ');
      found.push({ text: `${name}(${args})` });
    } else if (ts.isPropertyDeclaration(member) || ts.isPropertySignature(member)) {
      const type = member.type ? member.type.getText(text) : '';
      found.push({ text: type ? `${type} ${name}` : name });
    } else if (ts.isEnumMember(member)) {
      found.push({ text: name });
    }
  }
  return found;
}

/** Reads one file: what it declares and what it imports. */
function readModule(ts, root, file) {
  const text = fs.readFileSync(file, 'utf8');
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const relative = path.relative(root, file).replaceAll('\\', '/');
  const moduleId = relative.replace(/\.[^.]+$/, '');
  const lines = text.split('\n');
  const types = [];
  const imports = new Set();

  const declares = node =>
    ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)
    || ts.isEnumDeclaration(node) || ts.isTypeAliasDeclaration(node);

  for (const node of source.statements) {
    if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node))
        && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
      imports.add(node.moduleSpecifier.text);
      continue;
    }

    if (!declares(node) || !node.name) continue;

    // A declaration starts at its documentation: the comment above it was
    // written for it, and a fragment beginning after it explains nothing.
    const start = source.getLineAndCharacterOfPosition(
      node.getStart(source, /* includeJsDocComment */ true)).line;
    const end = source.getLineAndCharacterOfPosition(node.getEnd()).line;

    const bases = [];
    for (const clause of node.heritageClauses ?? []) {
      for (const type of clause.types) {
        bases.push({
          name: type.expression.getText(source).split('.').pop(),
          kind: clause.token === ts.SyntaxKind.ExtendsKeyword ? 'inheritance' : 'implements',
        });
      }
    }

    types.push({
      id: `${moduleId}.${node.name.getText(source)}`.replaceAll('/', '.'),
      name: node.name.getText(source),
      stereotype: stereotype(ts, node),
      visibility: visibility(ts, node),
      file: relative,
      line: start + 1,
      endLine: end + 1,
      source: lines.slice(start, end + 1).join('\n'),
      members: members(ts, node, source),
      bases,
    });
  }

  return { id: moduleId, relative, dir: path.dirname(relative), types, imports };
}

/** Where a relative import points, as a module id of this repository. */
function resolveImport(specifier, fromDir, known) {
  if (!specifier.startsWith('.')) return null;

  const base = path.posix.normalize(path.posix.join(fromDir, specifier));
  const tries = [base, `${base}/index`];

  for (const candidate of tries) {
    if (known.has(candidate)) return candidate;
  }
  return null;
}

function build(root, modules, title, depth) {
  const known = new Set(modules.map(m => m.id));
  const roots = new Map();

  const container = (parts) => {
    let here = roots, node = null, at = '';
    for (const part of parts) {
      at = at ? `${at}/${part}` : part;
      if (!here.has(at)) {
        here.set(at, { id: at, label: part, kind: 'folder', children: new Map(), types: [] });
      }
      node = here.get(at);
      here = node.children;
    }
    return node;
  };

  const byName = new Map();
  for (const module of modules) {
    for (const type of module.types) {
      if (!byName.has(type.name)) byName.set(type.name, []);
      byName.get(type.name).push(type.id);
    }
  }

  for (const module of modules) {
    const parts = module.dir === '.' ? [] : module.dir.split('/').slice(0, depth);
    const parent = parts.length ? container(parts) : null;
    const leaf = {
      id: module.id,
      label: path.basename(module.id),
      kind: 'module',
      role: path.basename(module.id).toLowerCase(),
      project: module.relative,
      children: new Map(),
      types: module.types.map(({ bases, ...rest }) => rest),
    };
    if (parent) parent.children.set(leaf.id, leaf);
    else roots.set(leaf.id, leaf);
  }

  const edges = [];
  const seen = new Set();
  const add = (from, to, kind) => {
    const key = `${from}\u0000${to}\u0000${kind}`;
    if (from !== to && !seen.has(key)) { seen.add(key); edges.push({ from, to, kind }); }
  };

  for (const module of modules) {
    for (const specifier of module.imports) {
      const target = resolveImport(specifier, module.dir, known);
      if (target) add(module.id, target, 'dependency');
    }
    for (const type of module.types) {
      for (const base of type.bases) {
        const candidates = byName.get(base.name) ?? [];
        // A name shared by two declarations is dropped rather than guessed at.
        if (candidates.length === 1) add(type.id, candidates[0], base.kind);
      }
    }
  }

  const freeze = node => ({ ...node, children: [...node.children.values()].map(freeze) });
  return { title, root, nodes: [...roots.values()].map(freeze), edges };
}

const args = process.argv.slice(2);
const repository = args[0];
const out = args.includes('--out') ? args[args.indexOf('--out') + 1] : 'arch.html';
const title = args.includes('--title') ? args[args.indexOf('--title') + 1] : null;
const depth = args.includes('--depth') ? Number(args[args.indexOf('--depth') + 1]) : 2;

if (!repository || !fs.existsSync(repository)) {
  console.error('usage: archview-ts.mjs <repository> [--out arch.html] [--title name] [--depth n]');
  process.exit(1);
}

const root = path.resolve(repository);
const compiler = await loadCompiler(root);

if (!compiler) {
  console.error('No TypeScript compiler found. A TypeScript project has one in');
  console.error('its node_modules; run npm install there, or install typescript here.');
  process.exit(1);
}

const files = walk(root);
const modules = [];
let unreadable = 0;

for (const file of files) {
  try {
    modules.push(readModule(compiler.ts, root, file));
  } catch (problem) {
    unreadable++;
    console.error(`  will not parse: ${path.relative(root, file)}: ${problem.message}`);
  }
}

const model = build(root, modules, title ?? path.basename(root), depth);
const json = JSON.stringify(model, null, 2);
fs.writeFileSync(out.replace(/\.html$/, '.json'), json);

if (out.endsWith('.html')) {
  const viewer = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../viewer/index.html');
  if (fs.existsSync(viewer)) {
    fs.writeFileSync(out, fs.readFileSync(viewer, 'utf8').replace('/*MODEL*/null', json));
  } else {
    console.error(`No viewer at ${viewer}; wrote the model only.`);
  }
}

const types = modules.reduce((n, m) => n + m.types.length, 0);
console.log(`compiler   ${compiler.from}`);
console.log(`files      ${files.length}`);
console.log(`modules    ${modules.length} read`);
console.log(`types      ${types}`);
console.log(`edges      ${model.edges.length}`);
console.log(`written    ${out}`);
if (unreadable) console.log(`\n${unreadable} files were not read; they are named above.`);
