using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using tsbindgen;
using tsbindgen.Core.Diagnostics;
using tsbindgen.Core.Policy;
using tsbindgen.Load;

namespace tsbindgen.Cli;

/// <summary>
/// Resolve the transitive dependency closure for a set of seed assemblies.
/// Emits machine-readable JSON to stdout.
/// </summary>
public static class ResolveClosureCommand
{
    public static Command Create()
    {
        var command = new Command(
            "resolve-closure",
            "Resolve transitive closure of assembly references (JSON)");

        var assemblyOption = new Option<string[]>(
            aliases: new[] { "--assembly", "-a" },
            description: "Path to a .NET assembly (.dll) to resolve (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var refDirOption = new Option<string[]>(
            name: "--ref-dir",
            description: "Additional directory to search for referenced assemblies (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var strictVersionsOption = new Option<bool>(
            name: "--strict-versions",
            getDefaultValue: () => true,
            description: "Error on major version drift for the same assembly name (default: true)");

        command.AddOption(assemblyOption);
        command.AddOption(refDirOption);
        command.AddOption(strictVersionsOption);

        command.SetHandler((context) =>
        {
            var assemblies = context.ParseResult.GetValueForOption(assemblyOption) ?? Array.Empty<string>();
            var refDirs = context.ParseResult.GetValueForOption(refDirOption) ?? Array.Empty<string>();
            var strictVersions = context.ParseResult.GetValueForOption(strictVersionsOption);

            Execute(assemblies, refDirs, strictVersions);
        });

        return command;
    }

    private static void Execute(
        IReadOnlyList<string> assemblyPaths,
        IReadOnlyList<string> refDirs,
        bool strictVersions)
    {
        var policy = PolicyDefaults.Create();
        var ctx = BuildContext.Create(policy, logger: null, verboseLogging: false);

        // Normalize and validate inputs early to keep failures crisp.
        var seeds = assemblyPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .ToList();

        if (seeds.Count == 0)
        {
            WriteJson(new ResolveClosureOutput
            {
                Seeds = Array.Empty<string>(),
                ReferenceDirectories = Array.Empty<string>(),
                ResolvedAssemblies = Array.Empty<ResolvedAssembly>(),
                Diagnostics = new[]
                {
                    new ResolvedDiagnostic
                    {
                        Code = "TSB_CLOSURE_001",
                        Severity = DiagnosticSeverity.Error,
                        Message = "No assemblies specified. Use --assembly (-a) at least once."
                    }
                }
            });
            Environment.Exit(2);
        }

        var seedDirs = seeds
            .Select(Path.GetDirectoryName)
            .Where(d => d != null)
            .Cast<string>();

        var allRefDirs = seedDirs
            .Concat(refDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LoadClosureResult? closure = null;
        try
        {
            var loader = new AssemblyLoader(ctx);
            closure = loader.LoadClosure(seeds, allRefDirs, strictVersions);
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Error(
                "TSB_CLOSURE_002",
                $"Failed to resolve assembly closure: {ex.Message}");
        }

        var diagnostics = ctx.Diagnostics.GetAll()
            .Select(d => new ResolvedDiagnostic
            {
                Code = d.Code,
                Severity = d.Severity,
                Message = d.Message,
                Location = d.Location
            })
            .ToArray();

        var resolvedAssemblies = closure is null
            ? Array.Empty<ResolvedAssembly>()
            : closure.ResolvedPaths
                .Select(kvp => new ResolvedAssembly
                {
                    Name = kvp.Key.Name,
                    PublicKeyToken = kvp.Key.PublicKeyToken,
                    Culture = kvp.Key.Culture,
                    Version = kvp.Key.Version,
                    Path = kvp.Value,
                    References = closure.References.TryGetValue(kvp.Key, out var refs)
                        ? refs.Select(r => new ResolvedAssemblyRef
                            {
                                Name = r.Name,
                                PublicKeyToken = r.PublicKeyToken,
                                Culture = r.Culture,
                                Version = r.Version
                            })
                            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : Array.Empty<ResolvedAssemblyRef>()
                })
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        WriteJson(new ResolveClosureOutput
        {
            Seeds = seeds.ToArray(),
            ReferenceDirectories = allRefDirs,
            ResolvedAssemblies = resolvedAssemblies,
            Diagnostics = diagnostics
        });

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            Environment.Exit(1);
        }
    }

    private static void WriteJson(ResolveClosureOutput output)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());

        Console.Out.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    }

    private sealed record ResolveClosureOutput
    {
        public required string[] Seeds { get; init; }
        public required string[] ReferenceDirectories { get; init; }
        public required ResolvedAssembly[] ResolvedAssemblies { get; init; }
        public required ResolvedDiagnostic[] Diagnostics { get; init; }
    }

    private sealed record ResolvedAssembly
    {
        public required string Name { get; init; }
        public required string PublicKeyToken { get; init; }
        public required string Culture { get; init; }
        public required string Version { get; init; }
        public required string Path { get; init; }
        public required ResolvedAssemblyRef[] References { get; init; }
    }

    private sealed record ResolvedAssemblyRef
    {
        public required string Name { get; init; }
        public required string PublicKeyToken { get; init; }
        public required string Culture { get; init; }
        public required string Version { get; init; }
    }

    private sealed record ResolvedDiagnostic
    {
        public required string Code { get; init; }
        public required DiagnosticSeverity Severity { get; init; }
        public required string Message { get; init; }
        public string? Location { get; init; }
    }
}
