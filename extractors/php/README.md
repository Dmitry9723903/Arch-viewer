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

Measured twice: on the four-file example here, and on a legacy repository of
2991 files.

| | `token_get_all` | `nikic/PHP-Parser` |
|---|---|---|
| declarations, example | 4 | 4 |
| line ranges, example | identical | identical |
| **members of a type** | **none** | 2, 2, 1, 2 |
| files read, 2991-file repository | **2991** | 2973 |
| declarations found there | **3079** | 2877 |
| time | **1.7 s** | 10.2 s |

The structure comes out the same where both can read. What the tokeniser does
not give is the inside of a type: its methods and properties.

**On legacy the weaker reader found more, and that was not expected.** The
parser refuses a file whose syntax a modern PHP rejects — eighteen of them in
that repository, Zend Framework 1 and a captcha library, holding 201 real
declarations between them. The tokeniser does not build a tree, so it does not
need the whole file to be valid; it reads the declarations it meets. Every
declaration the parser found, the tokeniser found too; the reverse is not
true.

So: for a modern codebase, install the parser and read the inside of types.
For old code that no longer compiles, the tokeniser sees more of it. That is
why both are here.

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

## Weight

A fragment is cut at 400 lines — one repository had classes of seven thousand
— and `--no-source` drops the text entirely. On that 2991-file repository the
page went from 15 MB to 4 MB and from not opening to opening. When the model
passes 8 MB the run says so and names the flag, rather than leaving the reader
to wait at a blank page.

Files without a `namespace` are grouped by directory, and such a container is
labelled `folder` rather than `namespace`. Legacy PHP is mostly such files:
leaving them at the root put 2690 boxes on one screen.
