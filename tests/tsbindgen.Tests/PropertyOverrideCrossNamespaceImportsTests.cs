using System.Diagnostics;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class PropertyOverrideCrossNamespaceImportsTests
{
    [Fact]
    public void CrossNamespacePropertyOverrideUnion_EmitsRequiredImports()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "property-override-cross-namespace-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "PropertyOverrideCrossNamespaceFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "PropertyOverrideCrossNamespaceFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"tsbindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var nsA = Path.Combine(emitOut, "NamespaceA", "internal", "index.d.ts");
        var nsB = Path.Combine(emitOut, "NamespaceB", "internal", "index.d.ts");
        Assert.True(File.Exists(nsA), $"Missing internal index: {nsA}");
        Assert.True(File.Exists(nsB), $"Missing internal index: {nsB}");

        var dtsA = File.ReadAllText(nsA);
        var dtsB = File.ReadAllText(nsB);

        // Each side must import the other namespace's event type for the unified union override.
        Assert.Contains("import type { DerivedEventType", dtsA);
        Assert.Contains("from \"../../NamespaceB/internal/index.js\";", dtsA);
        Assert.Contains("import type { BaseEventType", dtsB);
        Assert.Contains("from \"../../NamespaceA/internal/index.js\";", dtsB);

        // And the unified union should be emitted in both declarations.
        Assert.Contains("Event: BaseEventType | DerivedEventType;", dtsA);
        Assert.Contains("Event: BaseEventType | DerivedEventType;", dtsB);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tsbindgen.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Failed to locate tsbindgen repo root from test base directory.");
    }

    private static void DotnetBuild(string projectFile, string outputDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectFile}\" -c Release -o \"{outputDir}\" /nodeReuse:false",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        proc.WaitForExit();

        Task.WaitAll(stdoutTask, stderrTask);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        Assert.True(proc.ExitCode == 0, $"dotnet build failed (exit {proc.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }
}
