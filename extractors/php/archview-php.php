#!/usr/bin/env php
<?php
declare(strict_types=1);

/**
 * Reads a PHP repository into the model file the viewer expects.
 *
 * The viewer knows no language: it nests containers, expands them and draws
 * arrows between whatever the model names. Supporting PHP therefore means
 * writing this file and nothing else.
 *
 * Two readers, and the better one is used when it is there.
 *
 * `nikic/PHP-Parser` builds a real syntax tree and is exact. It is a
 * dependency, so it is never required: if composer has put it in the target
 * repository or beside this script, it is used.
 *
 * Otherwise `token_get_all` — the tokeniser of PHP itself. It is weaker than a
 * tree: it sees words, not structure, so it cannot resolve a name through
 * `use` aliases as reliably and it stops at the shape of a declaration. But it
 * is the language's own tokeniser rather than a pattern over text, and it does
 * not invent declarations inside strings and comments, which is where
 * regular expressions go wrong first.
 *
 * Which one ran is printed and recorded, because a map read two ways is two
 * maps.
 */

const SKIP = [
    'vendor', 'node_modules', '.git', 'storage', 'cache', 'var',
    'tests/fixtures', 'build', 'dist',
];

/**
 * The lines of a declaration, cut when it is longer than anyone will read.
 *
 * A class of seven thousand lines is a fact about the repository, not a thing
 * to put in a page: eighty-five per cent of one map's weight was source text,
 * and the page would not open. The cut is stated in the text rather than done
 * quietly.
 */
function fragment(array $lines, int $from, int $to, int $limit = 400): string
{
    $count = $to - $from + 1;
    $taken = implode("\n", array_slice($lines, $from - 1, min($count, $limit)));

    if ($count <= $limit) {
        return $taken;
    }

    $rest = $count - $limit;
    return $taken . "\n\n… {$rest} more lines; open the file to read them.";
}

/**
 * A path in one shape. Windows gives back backslashes, and the rest of this
 * file compares, splits and prints paths with forward ones.
 */
function slashes(string $path): string
{
    return str_replace('\\', '/', $path);
}

/** A file's path relative to the repository root, in that same shape. */
function relative(string $root, string $file): string
{
    return ltrim(substr(slashes($file), strlen(slashes($root))), '/');
}

/** Files worth reading. */
function files(string $root): array
{
    $found = [];
    $walk = new RecursiveIteratorIterator(
        new RecursiveCallbackFilterIterator(
            new RecursiveDirectoryIterator($root, FilesystemIterator::SKIP_DOTS),
            static function ($file) {
                $name = $file->getFilename();
                return $name[0] !== '.' && !in_array($name, SKIP, true);
            }
        )
    );

    foreach ($walk as $file) {
        if ($file->isFile() && strtolower($file->getExtension()) === 'php') {
            $found[] = $file->getPathname();
        }
    }

    sort($found);
    return $found;
}

/** Locates PHP-Parser, if composer has installed it anywhere useful. */
function findParser(string $root): ?string
{
    $candidates = [
        $root . '/vendor/autoload.php',
        __DIR__ . '/vendor/autoload.php',
    ];

    foreach ($candidates as $autoload) {
        if (is_file($autoload)) {
            require_once $autoload;
            if (class_exists('PhpParser\\ParserFactory')) {
                return $autoload;
            }
        }
    }

    return null;
}

/**
 * Reads one file with PHP-Parser: exact, because it works on a syntax tree.
 */
function readWithParser(string $root, string $file): array
{
    $text = (string) file_get_contents($file);
    $factory = new PhpParser\ParserFactory();
    $parser = method_exists($factory, 'createForNewestSupportedVersion')
        ? $factory->createForNewestSupportedVersion()
        : $factory->create(PhpParser\ParserFactory::PREFER_PHP7);

    try {
        $ast = $parser->parse($text);
    } catch (Throwable $problem) {
        fwrite(STDERR, "  will not parse: {$file}: {$problem->getMessage()}\n");
        return [];
    }

    $lines = explode("\n", $text);
    $types = [];
    $imports = [];
    $namespace = '';

    $visit = function (array $nodes, string $namespace) use (&$visit, &$types, &$imports, $lines, $root, $file) {
        foreach ($nodes as $node) {
            if ($node instanceof PhpParser\Node\Stmt\Namespace_) {
                $namespace = $node->name ? $node->name->toString() : '';
                $visit($node->stmts, $namespace);
                continue;
            }

            if ($node instanceof PhpParser\Node\Stmt\Use_) {
                foreach ($node->uses as $use) {
                    $imports[] = $use->name->toString();
                }
                continue;
            }

            if (!($node instanceof PhpParser\Node\Stmt\ClassLike) || $node->name === null) {
                continue;
            }

            $name = $node->name->toString();
            $stereotype = match (true) {
                $node instanceof PhpParser\Node\Stmt\Interface_ => 'interface',
                $node instanceof PhpParser\Node\Stmt\Trait_ => 'trait',
                $node instanceof PhpParser\Node\Stmt\Enum_ => 'enum',
                default => ($node->isAbstract() ?? false) ? 'abstract' : 'class',
            };

            // A declaration begins at its documentation: the comment above it
            // was written for it.
            $start = $node->getStartLine();
            if ($doc = $node->getDocComment()) {
                $start = $doc->getStartLine();
            }
            $end = $node->getEndLine();

            $bases = [];
            if (isset($node->extends)) {
                // A class extends one name; an interface may extend several,
                // so the field is a name in one case and a list in the other.
                // Casting either to an array is wrong: on an object it yields
                // that object's properties.
                $parents = is_array($node->extends) ? $node->extends : [$node->extends];
                foreach ($parents as $parent) {
                    if ($parent instanceof PhpParser\Node\Name) {
                        $bases[] = ['name' => $parent->toString(), 'kind' => 'inheritance'];
                    }
                }
            }
            foreach ($node->implements ?? [] as $contract) {
                if ($contract instanceof PhpParser\Node\Name) {
                    $bases[] = ['name' => $contract->toString(), 'kind' => 'implements'];
                }
            }

            $members = [];
            foreach ($node->getMethods() as $method) {
                $args = implode(', ', array_map(
                    static fn($p) => is_string($p->var->name) ? '$' . $p->var->name : '$?',
                    $method->params
                ));
                $members[] = ['text' => $method->name->toString() . "({$args})", 'line' => $method->getStartLine()];
            }

            $relative = relative($root, $file);
            $types[] = [
                'id' => ($namespace !== '' ? $namespace . '\\' : '') . $name,
                'name' => $name,
                'stereotype' => $stereotype,
                'visibility' => 'public',
                'file' => $relative,
                'line' => $start,
                'endLine' => $end,
                'source' => fragment($lines, $start, $end),
                'members' => $members,
                'bases' => $bases,
            ];
        }
    };

    $visit($ast ?? [], $namespace);
    return ['types' => $types, 'imports' => $imports];
}

/**
 * Reads one file with the tokeniser of PHP itself.
 *
 * Weaker than a tree and honest about it: it follows the words of a
 * declaration and stops there. It will not resolve a name through an alias as
 * a tree would, and it reads only what a declaration line states. What it will
 * not do is mistake a word inside a string or a comment for a declaration,
 * which is where a pattern over text fails first.
 */
function readWithTokens(string $root, string $file): array
{
    $text = (string) file_get_contents($file);

    try {
        $tokens = token_get_all($text, TOKEN_PARSE);
    } catch (Throwable $problem) {
        fwrite(STDERR, "  will not tokenise: {$file}: {$problem->getMessage()}\n");
        return [];
    }

    $lines = explode("\n", $text);
    $relative = relative($root, $file);
    $types = [];
    $imports = [];
    $namespace = '';
    $count = count($tokens);

    $word = static function (array $tokens, int $from, int $count): array {
        // The next name, and where it ended.
        for ($i = $from; $i < $count; $i++) {
            $token = $tokens[$i];
            if (is_array($token) && in_array($token[0], [T_STRING, T_NAME_QUALIFIED, T_NAME_FULLY_QUALIFIED], true)) {
                return [$token[1], $i];
            }
            if (is_array($token) && in_array($token[0], [T_WHITESPACE, T_COMMENT, T_DOC_COMMENT], true)) {
                continue;
            }
            if (is_string($token) && trim($token) === '') {
                continue;
            }
            break;
        }
        return ['', $from];
    };

    for ($i = 0; $i < $count; $i++) {
        $token = $tokens[$i];

        if (!is_array($token)) {
            continue;
        }

        if ($token[0] === T_NAMESPACE) {
            [$name] = $word($tokens, $i + 1, $count);
            $namespace = $name;
            continue;
        }

        if ($token[0] === T_USE) {
            [$name] = $word($tokens, $i + 1, $count);
            if ($name !== '') {
                $imports[] = $name;
            }
            continue;
        }

        if (!in_array($token[0], [T_CLASS, T_INTERFACE, T_TRAIT, T_ENUM], true)) {
            continue;
        }

        [$name, $at] = $word($tokens, $i + 1, $count);

        if ($name === '') {
            // An anonymous class: "new class { … }" declares nothing to name.
            continue;
        }

        $stereotype = match ($token[0]) {
            T_INTERFACE => 'interface',
            T_TRAIT => 'trait',
            T_ENUM => 'enum',
            default => 'class',
        };

        $bases = [];
        for ($j = $at + 1; $j < $count; $j++) {
            $next = $tokens[$j];
            if (is_string($next) && $next === '{') {
                break;
            }
            if (is_array($next) && in_array($next[0], [T_EXTENDS, T_IMPLEMENTS], true)) {
                $kind = $next[0] === T_EXTENDS ? 'inheritance' : 'implements';
                for ($k = $j + 1; $k < $count; $k++) {
                    $base = $tokens[$k];
                    if (is_string($base) && $base === '{') {
                        break 2;
                    }
                    if (is_array($base) && in_array($base[0], [T_STRING, T_NAME_QUALIFIED, T_NAME_FULLY_QUALIFIED], true)) {
                        $bases[] = ['name' => $base[1], 'kind' => $kind];
                    }
                }
            }
        }

        // Without a tree the end of a declaration is found by counting braces,
        // and only the tokeniser makes that safe: a brace inside a string or a
        // comment is not a brace, and it never reports one.
        $start = $token[2];
        $end = $start;
        $depth = 0;
        $opened = false;

        for ($j = $at; $j < $count; $j++) {
            $body = $tokens[$j];
            if (is_string($body)) {
                if ($body === '{') { $depth++; $opened = true; }
                elseif ($body === '}') {
                    $depth--;
                    if ($opened && $depth === 0) {
                        $end = $j + 1 < $count && is_array($tokens[$j + 1]) ? $tokens[$j + 1][2] : $start;
                        break;
                    }
                }
            } elseif (is_array($body)) {
                $end = $body[2];
            }
        }

        // The documentation above it belongs with it.
        $above = $start - 1;
        while ($above > 1 && trim($lines[$above - 2] ?? '') !== ''
               && preg_match('/^\s*(\*|\/\*|\/\/|#\[)/', $lines[$above - 2])) {
            $above--;
        }

        $types[] = [
            'id' => ($namespace !== '' ? $namespace . '\\' : '') . $name,
            'name' => $name,
            'stereotype' => $stereotype,
            'visibility' => 'public',
            'file' => $relative,
            'line' => $above,
            'endLine' => max($end, $start),
            'source' => fragment($lines, $above, max($end, $start)),
            'members' => [],
            'bases' => $bases,
        ];
    }

    return ['types' => $types, 'imports' => $imports];
}

/** Assembles the model: namespaces nested, files inside, declarations in them. */
function build(string $root, array $modules, string $title, int $depth): array
{
    $roots = [];
    $byName = [];

    foreach ($modules as $module) {
        foreach ($module['types'] as $type) {
            $byName[$type['name']][] = $type['id'];
        }
    }

    $container = static function (array $parts, array &$roots): ?array {
        $here = &$roots;
        $at = '';
        $node = null;

        foreach ($parts as $part) {
            $at = $at === '' ? $part : $at . '\\' . $part;
            if (!isset($here[$at])) {
                $here[$at] = [
                    'id' => $at,
                    'label' => $part,
                    'kind' => 'namespace',
                    'children' => [],
                    'types' => [],
                ];
            }
            $node = &$here[$at];
            $here = &$node['children'];
        }

        return $node;
    };

    foreach ($modules as $module) {
        $namespace = '';
        foreach ($module['types'] as $type) {
            $at = strrpos($type['id'], '\\');
            if ($at !== false) {
                $namespace = substr($type['id'], 0, $at);
                break;
            }
        }

        // A file without a namespace has no container of its own, and legacy
        // PHP is mostly such files: leaving them at the root put two and a
        // half thousand boxes on one screen. They are grouped by directory
        // instead, and the container says which it is — a namespace and a
        // folder are different things and must not sit unlabelled together.
        if ($namespace === '') {
            $directory = trim(slashes(dirname($module['relative'])), '.');
            $parts = $directory === '' ? [] : array_slice(explode('/', $directory), 0, $depth);
            $kind = 'folder';
        } else {
            $parts = array_slice(explode('\\', $namespace), 0, $depth);
            $kind = 'namespace';
        }
        $leaf = [
            'id' => $module['id'],
            'label' => basename($module['id'], '.php'),
            'kind' => 'file',
            'role' => strtolower(basename($module['id'], '.php')),
            'project' => $module['relative'],
            'children' => [],
            'types' => array_map(
                static function (array $type): array {
                    unset($type['bases']);
                    return $type;
                },
                $module['types']
            ),
        ];

        if ($parts === []) {
            $roots[$leaf['id']] = $leaf;
        } else {
            $parent = &$roots;
            $at = '';
            $separator = $kind === 'folder' ? '/' : '\\';
            foreach ($parts as $part) {
                $at = $at === '' ? $part : $at . $separator . $part;
                if (!isset($parent[$at])) {
                    $parent[$at] = ['id' => $at, 'label' => $part, 'kind' => $kind,
                                    'children' => [], 'types' => []];
                }
                $parent = &$parent[$at]['children'];
            }
            $parent[$leaf['id']] = $leaf;
            unset($parent);
        }
    }

    $edges = [];
    $seen = [];
    $add = static function (string $from, string $to, string $kind) use (&$edges, &$seen): void {
        $key = "{$from}\0{$to}\0{$kind}";
        if ($from !== $to && !isset($seen[$key])) {
            $seen[$key] = true;
            $edges[] = ['from' => $from, 'to' => $to, 'kind' => $kind];
        }
    };

    $known = [];
    foreach ($modules as $module) {
        foreach ($module['types'] as $type) {
            $known[$type['id']] = $module['id'];
        }
    }

    foreach ($modules as $module) {
        foreach ($module['imports'] as $imported) {
            if (isset($known[$imported])) {
                $add($module['id'], $known[$imported], 'dependency');
            }
        }

        foreach ($module['types'] as $type) {
            foreach ($type['bases'] as $base) {
                $simple = basename(str_replace('\\', '/', $base['name']));
                $candidates = $byName[$simple] ?? [];
                // A name shared by two declarations is dropped rather than
                // guessed at: a wrong arrow is worse than a missing one.
                if (count($candidates) === 1) {
                    $add($type['id'], $candidates[0], $base['kind']);
                }
            }
        }
    }

    $freeze = static function (array $node) use (&$freeze): array {
        $node['children'] = array_values(array_map($freeze, $node['children']));
        return $node;
    };

    return [
        'title' => $title,
        'root' => $root,
        'nodes' => array_values(array_map($freeze, $roots)),
        'edges' => $edges,
    ];
}

$arguments = $argv;
array_shift($arguments);
$repository = $arguments[0] ?? null;

if ($repository === null || !is_dir($repository)) {
    fwrite(STDERR, "usage: archview-php.php <repository> [--out arch.html] [--title name] [--depth n] [--tokens]\n");
    exit(1);
}

$option = static function (string $name, ?string $fallback) use ($arguments): ?string {
    $at = array_search($name, $arguments, true);
    return $at !== false && isset($arguments[$at + 1]) ? $arguments[$at + 1] : $fallback;
};

$root = realpath($repository);
$out = $option('--out', 'arch.html');
$title = $option('--title', basename($root));
$depth = (int) $option('--depth', '2');
$forceTokens = in_array('--tokens', $arguments, true);
$withoutSource = in_array('--no-source', $arguments, true);

$autoload = $forceTokens ? null : findParser($root);
$reader = $autoload === null ? 'token_get_all' : 'nikic/PHP-Parser';

$found = files($root);
$modules = [];
$unreadable = 0;

foreach ($found as $file) {
    $read = $autoload === null ? readWithTokens($root, $file) : readWithParser($root, $file);

    if ($read === []) {
        $unreadable++;
        continue;
    }

    $modules[] = [
        'id' => substr(relative($root, $file), 0, -4),
        'relative' => relative($root, $file),
        'types' => $read['types'],
        'imports' => $read['imports'],
    ];
}

if ($withoutSource) {
    foreach ($modules as &$module) {
        foreach ($module['types'] as &$type) {
            $type['source'] = null;
        }
        unset($type);
    }
    unset($module);
}

$model = build($root, $modules, $title, $depth);
$json = json_encode($model, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
file_put_contents(preg_replace('/\.html$/', '.json', $out), $json);

if (str_ends_with($out, '.html')) {
    $viewer = __DIR__ . '/../../viewer/index.html';
    if (is_file($viewer)) {
        file_put_contents($out, str_replace('/*MODEL*/null', $json, (string) file_get_contents($viewer)));
    } else {
        fwrite(STDERR, "No viewer at {$viewer}; wrote the model only.\n");
    }
}

$types = array_sum(array_map(static fn(array $m): int => count($m['types']), $modules));

echo "reader     {$reader}\n";
echo 'files      ' . count($found) . "\n";
echo 'modules    ' . count($modules) . " read\n";
echo "types      {$types}\n";
echo 'edges      ' . count($model['edges']) . "\n";
echo "written    {$out}\n";

if ($unreadable > 0) {
    echo "\n{$unreadable} files were not read; they are named above.\n";
}

// A page is opened in a browser, and one of many megabytes is opened by
// nobody. Source text is most of that weight, so the way out is named here
// rather than left for the reader to discover by waiting.
$weight = strlen($json);

if ($weight > 8_000_000 && !$withoutSource) {
    printf(
        "\nThe model is %.1f MB, most of it source text. A page this size may not open.\n",
        $weight / 1_000_000
    );
    echo "Run again with --no-source for the structure alone.\n";
}
