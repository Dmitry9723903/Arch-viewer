using System.Diagnostics;

namespace ArchViewer.Extract;

/// <summary>
/// Reads a whole repository — every ecosystem in it — and joins the result
/// into one map.
/// <para>
/// Each language is read by its own extractor, which is right, and left the
/// person running it to find out which languages are present, call three or
/// four programs by hand and merge the outputs. That is a thing to do once
/// and resent thereafter. This finds what is there, runs what is needed and
/// joins the parts.
/// </para>
/// <para>
/// What it will not do is hide a gap. An ecosystem present but unreadable —
/// no interpreter, no compiler in the project — is named with the reason, not
/// silently dropped.
/// </para>
/// </summary>
internal static class Everything
{
    /// <summary>One ecosystem found in a repository.</summary>
    private sealed record Part(string Name, string Extractor, string[] Runners, string Argument);

    /// <summary>
    /// Finds every ecosystem, reads each, and writes one page.
    /// </summary>
    /// <param name="root">Repository to read.</param>
    /// <param name="outPath">Page to write.</param>
    /// <param name="title">Name for the whole.</param>
    /// <param name="policy">Policy for the .NET part, when there is one.</param>
    /// <returns>Zero when a page was written.</returns>
    public static int Read(
        string root,
        string outPath,
        string? title,
        string? policy,
        bool build,
        bool withoutSource)
    {
        var tools = Path.GetDirectoryName(typeof(Everything).Assembly.Location);
        var home = Home(tools);

        if (home is null)
        {
            Console.Error.WriteLine("Cannot find the extractors directory beside this tool.");
            return 1;
        }

        // A policy is the architecture's rules, and an architecture does not
        // belong to a language. Found once here and applied to every part.
        var beside = Path.Combine(root, ".arch-viewer", "policy.json");
        var rules = policy ?? (File.Exists(beside) ? beside : null);

        var work = Directory.CreateTempSubdirectory("archview");
        var models = new List<string>();
        var skipped = new List<string>();

        try
        {
            // Found before the .NET pass runs, so that pass can be told which
            // languages are covered and not name them as unread.
            var others = Others(root, home).ToList();

            if (Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Any())
            {
                var model = Path.Combine(work.FullName, "dotnet.json");
                Console.WriteLine("== .NET ==");

                // The .NET extractor reads compiled assemblies, so a
                // repository that has never been built holds nothing for it
                // to read. Leaving the build to the person turns one command
                // back into two, and the second one is the one they forget —
                // after which the map is empty for a reason nothing states.
                if (build)
                {
                    Build(root);
                }

                var arguments = new List<string>
                {
                    root, "--out", Path.ChangeExtension(model, ".html"), "--title", ".NET",
                };

                if (withoutSource)
                {
                    arguments.Add("--no-source");
                }

                if (others.Count > 0)
                {
                    var covered = others.Select(part => part.Name).ToList();

                    // One extractor reads both languages, and the report
                    // names them separately. Without this, a repository of C
                    // is announced as unread and then read a moment later.
                    if (covered.Contains("C++"))
                    {
                        covered.Add("C");
                    }

                    arguments.Add("--covered");
                    arguments.Add(string.Join(',', covered));
                }

                if (policy is not null)
                {
                    arguments.Add("--policy");
                    arguments.Add(policy);
                }

                // Run in a child process rather than in this one. A crash
                // that cannot be caught — a stack overflow while resolving
                // types is the one already met here — would otherwise take
                // the whole run with it, and the Python and PHP parts would
                // be lost to a fault in the .NET part.
                var self = Self();

                var code = Run(self.Program, self.Before.Concat(arguments).ToList());

                if (code == 0 && File.Exists(model))
                {
                    models.Add(model);
                }
                else
                {
                    skipped.Add($".NET — {Ended(code)}");
                    Console.WriteLine($"  {Ended(code)}, going on with the rest");
                }
            }

            foreach (var part in others)
            {
                Console.WriteLine();
                Console.WriteLine($"== {part.Name} ==");

                var runner = part.Runners.FirstOrDefault(Available);

                if (runner is null)
                {
                    var names = string.Join(" or ", part.Runners);
                    skipped.Add($"{part.Name} — {names} is not installed");
                    Console.WriteLine($"  skipped: {names} is not installed");
                    continue;
                }

                var page = Path.Combine(work.FullName, $"{part.Name.ToLowerInvariant()}.html");
                var model = Path.ChangeExtension(page, ".json");

                var forPart = new List<string>
                {
                    part.Extractor, part.Argument, "--out", page, "--title", part.Name,
                };

                if (withoutSource)
                {
                    forPart.Add("--no-source");
                }

                var code = Run(runner, forPart);

                if (code == 0 && File.Exists(model))
                {
                    // The .NET extractor applies a policy while it builds;
                    // every other one produces structure and leaves the
                    // judging to the one implementation of the rules.
                    if (rules is not null)
                    {
                        Program.Main(new[] { "judge", model, "--policy", rules, "--out", page });
                    }

                    models.Add(model);
                }
                else
                {
                    skipped.Add($"{part.Name} — {Ended(code)}");
                    Console.WriteLine($"  {Ended(code)}, going on with the rest");
                }
            }

            if (models.Count == 0)
            {
                Console.Error.WriteLine("Nothing could be read.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine(models.Count == 1 ? "== one part, no merge needed ==" : "== joining ==");

            var merged = models.Count == 1
                ? models[0]
                : null;

            if (merged is not null)
            {
                File.Copy(Path.ChangeExtension(merged, ".html"), outPath, overwrite: true);
                File.Copy(merged, Path.ChangeExtension(outPath, ".json"), overwrite: true);
            }
            else
            {
                var joined = models.ToList();
                joined.Insert(0, "merge");
                joined.Add("--out");
                joined.Add(outPath);

                if (title is not null)
                {
                    joined.Add("--title");
                    joined.Add(title);
                }

                if (Program.Main(joined.ToArray()) != 0)
                {
                    return 1;
                }
            }

            if (skipped.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Left out:");

                foreach (var line in skipped)
                {
                    Console.WriteLine($"  {line}");
                }
            }

            Weight(outPath, withoutSource);

            Console.WriteLine();
            Console.WriteLine($"Open {outPath} in a browser.");
            return 0;
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Says so when the page is large enough that a browser may refuse it.
    /// <para>
    /// Each extractor watches its own part, and none of them sees the whole:
    /// three parts that each pass for reasonable join into a page nothing
    /// opens. Advising a flag the caller cannot reach would be worse than
    /// silence, so this is said only where --no-source can still be used.
    /// </para>
    /// </summary>
    private static void Weight(string outPath, bool withoutSource)
    {
        const long large = 12L * 1024 * 1024;

        try
        {
            var size = new FileInfo(outPath).Length;

            if (size < large)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"The page is {size / (1024.0 * 1024.0):0.#} MB, and one this size may not open.");

            Console.WriteLine(withoutSource
                ? "It holds no source text already; narrow the repository instead."
                : "Run again with --no-source for the structure without the text.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The directory holding the extractors: beside the tool, or above its
    /// build output when run from a clone.
    /// </summary>
    private static string? Home(string? from)
    {
        var directory = from;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory, "extractors")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// Ecosystems other than .NET that this repository holds, in the order
    /// they are worth reading.
    /// </summary>
    private static IEnumerable<Part> Others(string root, string home)
    {
        var parts = new List<Part>();

        if (Holds(root, "*.py"))
        {
            parts.Add(new Part(
                "Python",
                Path.Combine(home, "extractors", "python", "archview_python.py"),
                new[] { "python3", "python" },
                root));
        }

        if (Holds(root, "*.ts") || Holds(root, "*.tsx") || Holds(root, "*.jsx"))
        {
            parts.Add(new Part(
                "TypeScript",
                Path.Combine(home, "extractors", "typescript", "archview-ts.mjs"),
                new[] { "node" },
                root));
        }

        if (Holds(root, "*.php"))
        {
            parts.Add(new Part(
                "PHP",
                Path.Combine(home, "extractors", "php", "archview-php.php"),
                new[] { "php" },
                root));
        }

        if (new[] { "*.c", "*.cpp", "*.cc", "*.cxx", "*.h", "*.hpp", "*.hxx" }
            .Any(pattern => Holds(root, pattern)))
        {
            parts.Add(new Part(
                "C++",
                Path.Combine(home, "extractors", "cpp", "archview_cpp.py"),
                new[] { "python3", "python" },
                root));
        }

        if (Holds(root, "*.sql"))
        {
            parts.Add(new Part(
                "SQL",
                Path.Combine(home, "extractors", "sql", "archview_sql.py"),
                new[] { "python3", "python" },
                root));
        }

        return parts.Where(part => File.Exists(part.Extractor));
    }

    /// <summary>
    /// Whether the repository holds files of a kind that are its own, rather
    /// than somebody else's library vendored into it.
    /// </summary>
    private static bool Holds(string root, string pattern)
    {
        string[] theirs =
        {
            "/node_modules/", "/__pycache__/", "/.venv/", "/venv/", "/env/",
            "/site-packages/", "/vendor/", "/dist/", "/build/", "/obj/", "/bin/",
        };

        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Select(path => path.Replace('\\', '/'))
                .Any(path => !theirs.Any(place => path.Contains(place, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the repository's .NET code: its solution when it has one,
    /// otherwise each project. A build that fails is reported and the reading
    /// goes on with whatever output already exists — an old assembly read and
    /// named as old beats no map at all.
    /// </summary>
    private static void Build(string root)
    {
        if (!Available("dotnet"))
        {
            Console.WriteLine("  not built: dotnet is not installed, reading whatever output is there");
            return;
        }

        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.AllDirectories))
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ToList();

        var targets = solutions.Count > 0
            ? new List<string> { solutions[0] }
            : Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).ToList();

        foreach (var target in targets)
        {
            var name = Path.GetRelativePath(root, target).Replace('\\', '/');
            Console.WriteLine($"  building {name}");

            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };

            info.ArgumentList.Add("build");
            info.ArgumentList.Add(target);
            info.ArgumentList.Add("--nologo");
            info.ArgumentList.Add("-v");
            info.ArgumentList.Add("q");

            try
            {
                using var process = Process.Start(info);

                if (process is null)
                {
                    continue;
                }

                var errors = process.StandardOutput.ReadToEnd()
                    .Split('\n')
                    .Where(line => line.Contains(" error ", StringComparison.Ordinal))
                    .Take(3)
                    .ToList();

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    continue;
                }

                Console.WriteLine($"  build failed ({name}), reading whatever output is there:");

                foreach (var line in errors)
                {
                    Console.WriteLine($"    {line.Trim()}");
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.WriteLine($"  could not run the build for {name}");
            }
        }
    }

    private static bool Available(string runner)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(runner, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// How to start this program again: the file to run, and whatever has to
    /// precede its own arguments. Run through the muxer — "dotnet
    /// archview.dll" — the muxer is the program and the assembly is its first
    /// argument; installed as an executable, there is nothing to prepend.
    /// </summary>
    private static (string Program, IReadOnlyList<string> Before) Self()
    {
        var process = Environment.ProcessPath;
        var assembly = typeof(Everything).Assembly.Location;

        if (process is null)
        {
            return ("dotnet", new[] { assembly });
        }

        var name = Path.GetFileNameWithoutExtension(process);

        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (process, new[] { assembly })
            : (process, Array.Empty<string>());
    }

    /// <summary>
    /// What became of a pass, said plainly. A pass killed by the operating
    /// system is not the same event as one that reported an error and stopped,
    /// and calling both "did not finish" hides the more serious of the two.
    /// </summary>
    private static string Ended(int code) => code switch
    {
        -1 => "its extractor could not be started",
        > 128 => $"its extractor was killed (signal {code - 128}) — likely a crash",
        _ => $"its extractor stopped with an error (exit {code})",
    };

    /// <summary>
    /// Runs a program to completion and returns its exit code, or -1 when it
    /// could not be started at all.
    /// </summary>
    private static int Run(string runner, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(runner)
        {
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);

            if (process is null)
            {
                return -1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
