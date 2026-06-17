using System.CommandLine;

namespace DotnetBindgen.Cli;

/// <summary>
/// Entry point for dotnet-bindgen CLI.
/// Exposes:
/// - generate: emit bindings
/// - resolve-closure: resolve transitive assembly closure (JSON)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Generate TypeScript declarations from .NET assemblies");

        // Add the generate command
        var generateCommand = GenerateCommand.Create();
        rootCommand.AddCommand(generateCommand);

        // Resolve assembly dependency closure (machine-readable)
        var resolveClosureCommand = ResolveClosureCommand.Create();
        rootCommand.AddCommand(resolveClosureCommand);

        return await rootCommand.InvokeAsync(args);
    }
}
