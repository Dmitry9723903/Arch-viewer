# Running on Windows

Everything here works on Windows, with one caveat stated first: **it has been
built and run on Linux**. The path handling was written for both and checked
by reading rather than by running, so if something breaks, it is worth an
issue — the fault is likely a path.

## What you need

| For | Install |
|---|---|
| the .NET extractor | [.NET SDK 10](https://dotnet.microsoft.com/download) — `dotnet --list-sdks` must show 10.x |
| the TypeScript extractor | [Node.js](https://nodejs.org) 18 or newer |
| the Python extractor | [Python](https://python.org) 3.10 or newer |
| the PHP extractor | [PHP](https://windows.php.net/download) 8.1 or newer |

You need only the ones for the languages you intend to read.

## Getting it

```powershell
git clone https://github.com/Dmitry9723903/Arch-viewer.git
cd Arch-viewer
dotnet build src\ArchViewer.Extract
```

That builds `archview.dll`. Everything below runs it with `dotnet`.

## Reading a .NET repository

```powershell
dotnet build C:\path\to\YourSolution.sln
dotnet src\ArchViewer.Extract\bin\Debug\net10.0\archview.dll C:\path\to\repo --out arch.html
```

Open `arch.html` in a browser. **Not in an editor** — it is a web page, and an
editor shows you its source.

Build the solution first. Types, members and jump-to-source come from compiled
assemblies and their PDBs; without a build you get the graph and nothing
inside it.

## Reading the other languages

```powershell
python  extractors\python\archview_python.py C:\path\to\repo --out arch.html
node    extractors\typescript\archview-ts.mjs C:\path\to\repo --out arch.html
php     extractors\php\archview-php.php C:\path\to\repo --out arch.html
```

The TypeScript one uses the compiler from the repository you point it at, so
run `npm install` there first. The PHP one uses `nikic/PHP-Parser` if composer
has installed it, and the language's own tokeniser otherwise.

## One repository, one map

A solution with a TypeScript client is two extractions; `merge` makes them one
page:

```powershell
$av = "src\ArchViewer.Extract\bin\Debug\net10.0\archview.dll"
dotnet $av C:\path\to\repo --out dotnet.html
node extractors\typescript\archview-ts.mjs C:\path\to\repo\client --out client.html --title "client"
dotnet $av merge dotnet.json client.json --out arch.html --title "MyRepo"
```

## If something looks wrong

**The page is blank.** The model may be too large — the run says so and names
`--no-source`, which drops the text and keeps the structure.

**A directory is missing.** The run names what it could not read and why. An
area of Python or TypeScript needs that language's extractor; the .NET one
reads `.csproj` and nothing else.

**No types, only boxes.** The repository was not built, or was built without
PDBs. The run says how many types lost their source and why.

**Everything is red on the first run.** The policy is probably describing
namespaces while judging project references. `kinds` on a rule limits it to
the kinds of reference it is actually about.
