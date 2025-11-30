using System.CommandLine;

namespace tsbindgen.Cli;

/// <summary>
/// CLI command for generating TypeScript declarations from .NET assemblies.
/// </summary>
public static class GenerateCommand
{
    public static Command Create()
    {
        var command = new Command("generate", "Generate TypeScript declarations from .NET assemblies");

        // Assembly input options
        var assemblyOption = new Option<string[]>(
            aliases: new[] { "--assembly", "-a" },
            description: "Path to a .NET assembly (.dll) to process (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var assemblyDirOption = new Option<string?>(
            aliases: new[] { "--assembly-dir", "-d" },
            description: "Directory containing assemblies to process");

        // Output option
        var outDirOption = new Option<string>(
            aliases: new[] { "--out-dir", "-o" },
            getDefaultValue: () => "out",
            description: "Output directory (default: out/)");

        // Filter options
        var namespacesOption = new Option<string[]>(
            aliases: new[] { "--namespaces", "-n" },
            description: "Comma-separated list of namespaces to include")
        {
            AllowMultipleArgumentsPerToken = true
        };

        // Naming style option
        var namingOption = new Option<string?>(
            name: "--naming",
            description: "Member naming style: 'js' (JavaScript-style lowerFirst) or 'clr' (preserve C# names). Default: clr");

        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            getDefaultValue: () => false,
            description: "Show detailed generation progress");

        var logsOption = new Option<string[]>(
            aliases: new[] { "--logs" },
            description: "Enable logging for specific categories (e.g., --logs ViewPlanner PhaseGate)")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var strictOption = new Option<bool>(
            aliases: new[] { "--strict" },
            getDefaultValue: () => false,
            description: "Enable strict mode validation (zero non-whitelisted warnings)");

        var libOption = new Option<string?>(
            aliases: new[] { "--lib" },
            description: "Path to existing tsbindgen package (library mode - emit only what's in the library contract)");

        command.AddOption(assemblyOption);
        command.AddOption(assemblyDirOption);
        command.AddOption(outDirOption);
        command.AddOption(namespacesOption);
        command.AddOption(namingOption);
        command.AddOption(verboseOption);
        command.AddOption(logsOption);
        command.AddOption(strictOption);
        command.AddOption(libOption);

        command.SetHandler(async (context) =>
        {
            var assemblies = context.ParseResult.GetValueForOption(assemblyOption) ?? Array.Empty<string>();
            var assemblyDir = context.ParseResult.GetValueForOption(assemblyDirOption);
            var outDir = context.ParseResult.GetValueForOption(outDirOption) ?? "out";
            var namespaces = context.ParseResult.GetValueForOption(namespacesOption) ?? Array.Empty<string>();
            var naming = context.ParseResult.GetValueForOption(namingOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var logs = context.ParseResult.GetValueForOption(logsOption) ?? Array.Empty<string>();
            var strict = context.ParseResult.GetValueForOption(strictOption);
            var lib = context.ParseResult.GetValueForOption(libOption);

            await ExecuteAsync(
                assemblies,
                assemblyDir,
                outDir,
                namespaces,
                naming,
                verbose,
                logs,
                strict,
                lib);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string[] assemblyPaths,
        string? assemblyDir,
        string outDir,
        string[] namespaceFilter,
        string? naming,
        bool verbose,
        string[] logs,
        bool strict,
        string? lib)
    {
        try
        {
            // Collect all assemblies to process
            var allAssemblies = new List<string>(assemblyPaths);

            if (assemblyDir != null)
            {
                if (!Directory.Exists(assemblyDir))
                {
                    Console.Error.WriteLine($"Error: Assembly directory not found: {assemblyDir}");
                    Environment.Exit(3);
                }

                var dllFiles = Directory.GetFiles(assemblyDir, "*.dll", SearchOption.TopDirectoryOnly);
                allAssemblies.AddRange(dllFiles);
            }

            if (allAssemblies.Count == 0)
            {
                Console.Error.WriteLine("Error: No assemblies specified. Use --assembly or --assembly-dir");
                Environment.Exit(2);
            }

            // Build policy from CLI options
            var policy = Core.Policy.PolicyDefaults.Create();

            // Apply naming style if specified
            if (!string.IsNullOrWhiteSpace(naming))
            {
                var namingStyle = naming.ToLowerInvariant() switch
                {
                    "js" => Core.Policy.NamingStyle.Js,
                    "clr" => Core.Policy.NamingStyle.Clr,
                    _ => throw new ArgumentException($"Unknown naming style: '{naming}'. Use 'js' or 'clr'.")
                };

                policy = policy with
                {
                    Emission = policy.Emission with
                    {
                        Naming = namingStyle
                    }
                };
            }

            // Create logger - only if verbose or specific log categories requested
            Action<string>? logger = (verbose || logs.Length > 0) ? Console.WriteLine : null;

            // Parse log categories
            HashSet<string>? logCategories = logs.Length > 0 ? new HashSet<string>(logs) : null;

            // Run pipeline
            var result = Builder.Build(
                allAssemblies,
                outDir,
                policy,
                logger,
                verbose,
                logCategories,
                strict,
                lib);

            // Report results
            Console.WriteLine();
            if (result.Success)
            {
                Console.WriteLine("✓ Generation complete");
                Console.WriteLine($"  Output directory: {Path.GetFullPath(outDir)}");
                Console.WriteLine($"  Namespaces: {result.Statistics.NamespaceCount}");
                Console.WriteLine($"  Types: {result.Statistics.TypeCount}");
                Console.WriteLine($"  Members: {result.Statistics.TotalMembers}");
            }
            else
            {
                Console.Error.WriteLine("✗ Generation failed");
                Console.Error.WriteLine($"  Errors: {result.Diagnostics.Count(d => d.Severity == Core.Diagnostics.DiagnosticSeverity.Error)}");

                foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == Core.Diagnostics.DiagnosticSeverity.Error))
                {
                    Console.Error.WriteLine($"    {diagnostic.Code}: {diagnostic.Message}");
                }

                Environment.Exit(1);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine($"Stack trace:\n{ex.StackTrace}");
            }
            Environment.Exit(1);
        }
    }
}
