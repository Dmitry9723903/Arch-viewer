# PHP extractor

Reads a PHP repository into the model file the viewer expects.

```
php archview-php.php <repository> --out arch.html
```

## Two readers, and which one ran is printed

PHP has no parser in the standard library, so this extractor carries both
answers and takes the better one available.

**`nikic/PHP-Parser`** builds a real syntax tree. It is a dependency, so it is
never required: if composer has installed it in the target repository's
`vendor/` or beside this script, it is used. Install it here with
`composer require nikic/php-parser`.

**`token_get_all`** otherwise — the tokeniser of PHP itself. Weaker than a
tree and honest about it, but it is the language's own reader: it will not
mistake a word inside a string or a comment for a declaration, which is where
a pattern over text fails first.

`--tokens` forces the second, which is how the two were compared.

The run prints which reader it used, and so does nothing else: a map read two
ways is two maps, and the reader belongs beside the numbers.

## What the difference costs

Measured on the example in `examples/php`, both readers over the same four
files:

| | `token_get_all` | `nikic/PHP-Parser` |
|---|---|---|
| declarations found | 4 | 4 |
| line ranges | identical | identical |
| edges | 4 | 4 |
| **members of a type** | **none** | 2, 2, 1, 2 |

So the structure — what exists, where it starts and ends, what depends on what
— comes out the same. What the tokeniser does not give is the inside of a
type: its methods and properties. If a map is for boundaries, the tokeniser
suffices; if it is for reading types, install the parser.

## What it reads

| On the map | From |
|---|---|
| namespaces, nested | `namespace` declarations |
| files | files |
| declarations | `class`, `interface`, `trait`, `enum` |
| `dependency` edges | `use` statements naming something declared here |
| `inheritance` / `implements` edges | `extends` and `implements` |

A fragment starts at the documentation above the declaration and ends at the
brace that closes it — counted over tokens, so a brace inside a string or a
comment is not counted.

## What it does not read

**Anonymous classes.** `new class { … }` declares no name, and the example
contains one to prove it stays out.

**Names resolved through aliases.** `use Foo\Bar as Baz` is recorded as the
import; a base class written as `Baz` is matched by its simple name, and a
simple name belonging to two declarations is dropped rather than guessed at.

**Dynamic anything.** `new $class` names its target at run time.

**Functions outside classes.** Files and declarations are the containers.
