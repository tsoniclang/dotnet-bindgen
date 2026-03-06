using System.CommandLine;
using tsbindgen.Surface;

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

        var refDirOption = new Option<string[]>(
            name: "--ref-dir",
            description: "Additional directory to search for referenced assemblies (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

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

        var allowConstructorConstraintLossOption = new Option<bool>(
            name: "--allow-constructor-constraint-loss",
            getDefaultValue: () => false,
            description: "Allow generic new() constraint loss (downgrade to warning)");

        var libOption = new Option<string[]>(
            name: "--lib",
            description: "Path to existing tsbindgen package (library mode - emit only what's in the library contract, repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var libTypeOverrideOption = new Option<string[]>(
            name: "--lib-type-override",
            description: "Override owning package for a CLR type when merging --lib contracts (format: ClrFullName=packageName, repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var namespaceMapOption = new Option<string[]>(
            name: "--namespace-map",
            description: "Map CLR namespace to output name (format: Namespace=outputName, repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var flattenClassOption = new Option<string[]>(
            name: "--flatten-class",
            description: "Flatten static class to top-level function exports (format: Namespace.ClassName, repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var surfacePackageOption = new Option<string[]>(
            name: "--surface-package",
            description: "Path to tsbindgen surface package config (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        command.AddOption(assemblyOption);
        command.AddOption(assemblyDirOption);
        command.AddOption(refDirOption);
        command.AddOption(outDirOption);
        command.AddOption(namespacesOption);
        command.AddOption(verboseOption);
        command.AddOption(logsOption);
        command.AddOption(strictOption);
        command.AddOption(allowConstructorConstraintLossOption);
        command.AddOption(libOption);
        command.AddOption(libTypeOverrideOption);
        command.AddOption(namespaceMapOption);
        command.AddOption(flattenClassOption);
        command.AddOption(surfacePackageOption);

        command.SetHandler(async (context) =>
        {
            var assemblies = context.ParseResult.GetValueForOption(assemblyOption) ?? Array.Empty<string>();
            var assemblyDir = context.ParseResult.GetValueForOption(assemblyDirOption);
            var refDirs = context.ParseResult.GetValueForOption(refDirOption) ?? Array.Empty<string>();
            var outDir = context.ParseResult.GetValueForOption(outDirOption) ?? "out";
            var namespaces = context.ParseResult.GetValueForOption(namespacesOption) ?? Array.Empty<string>();
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var logs = context.ParseResult.GetValueForOption(logsOption) ?? Array.Empty<string>();
            var strict = context.ParseResult.GetValueForOption(strictOption);
            var allowConstructorConstraintLoss = context.ParseResult.GetValueForOption(allowConstructorConstraintLossOption);
            var libs = context.ParseResult.GetValueForOption(libOption) ?? Array.Empty<string>();
            var libTypeOverrides = context.ParseResult.GetValueForOption(libTypeOverrideOption) ?? Array.Empty<string>();
            var namespaceMaps = context.ParseResult.GetValueForOption(namespaceMapOption) ?? Array.Empty<string>();
            var flattenClasses = context.ParseResult.GetValueForOption(flattenClassOption) ?? Array.Empty<string>();
            var surfacePackages = context.ParseResult.GetValueForOption(surfacePackageOption) ?? Array.Empty<string>();

            await ExecuteAsync(
                assemblies,
                assemblyDir,
                refDirs,
                outDir,
                namespaces,
                verbose,
                logs,
                strict,
                allowConstructorConstraintLoss,
                libs,
                libTypeOverrides,
                namespaceMaps,
                flattenClasses,
                surfacePackages);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string[] assemblyPaths,
        string? assemblyDir,
        string[] refDirs,
        string outDir,
        string[] namespaceFilter,
        bool verbose,
        string[] logs,
        bool strict,
        bool allowConstructorConstraintLoss,
        string[] libs,
        string[] libTypeOverrides,
        string[] namespaceMaps,
        string[] flattenClasses,
        string[] surfacePackagePaths)
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

            if (allAssemblies.Count == 0 && surfacePackagePaths.Length == 0)
            {
                Console.Error.WriteLine("Error: No assemblies specified. Use --assembly/--assembly-dir or provide --surface-package");
                Environment.Exit(2);
            }

            // Build policy from CLI options
            var policy = Core.Policy.PolicyDefaults.Create();

            if (allowConstructorConstraintLoss)
            {
                policy = policy with
                {
                    Constraints = policy.Constraints with
                    {
                        AllowConstructorConstraintLoss = true
                    }
                };
            }

            // Apply namespace mappings if specified
            if (namespaceMaps.Length > 0)
            {
                var mappings = new Dictionary<string, string>();
                foreach (var map in namespaceMaps)
                {
                    var parts = map.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        mappings[parts[0].Trim()] = parts[1].Trim();
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: Invalid namespace-map format: '{map}'. Expected 'Namespace=outputName'.");
                    }
                }
                policy = policy with
                {
                    Emission = policy.Emission with
                    {
                        NamespaceMappings = mappings
                    }
                };
            }

            // Apply flatten-class if specified
            if (flattenClasses.Length > 0)
            {
                policy = policy with
                {
                    Emission = policy.Emission with
                    {
                        FlattenedClasses = new HashSet<string>(flattenClasses)
                    }
                };
            }

            // Create logger - only if verbose or specific log categories requested
            Action<string>? logger = (verbose || logs.Length > 0) ? Console.WriteLine : null;

            // Parse log categories
            HashSet<string>? logCategories = logs.Length > 0 ? new HashSet<string>(logs) : null;

            // Parse per-type library ownership overrides (for split namespaces / ambiguous packages).
            // Format: "ClrFullName=packageName" (repeatable).
            // Example: "Microsoft.EntityFrameworkCore.DbContext=@tsonic/efcore-types"
            Dictionary<string, string>? overrides = null;
            if (libTypeOverrides.Length > 0)
            {
                overrides = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var ov in libTypeOverrides)
                {
                    var parts = ov.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        Console.Error.WriteLine($"Warning: Invalid --lib-type-override format: '{ov}'. Expected 'ClrFullName=packageName'.");
                        continue;
                    }

                    var clr = parts[0].Trim();
                    var pkg = parts[1].Trim();
                    if (clr.Length == 0 || pkg.Length == 0)
                    {
                        Console.Error.WriteLine($"Warning: Invalid --lib-type-override: '{ov}'. CLR full name and package name must be non-empty.");
                        continue;
                    }

                    overrides[clr] = pkg;
                }
            }

            var surfacePackages = surfacePackagePaths.Length > 0
                ? SurfacePackageLoader.LoadMany(surfacePackagePaths)
                : [];

            // Run pipeline
            var result = Builder.Build(
                allAssemblies,
                outDir,
                policy,
                logger,
                verbose,
                logCategories,
                strict,
                libs,
                refDirs,
                libraryClrTypePackageOverrides: overrides,
                surfacePackages: surfacePackages);

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
