using System.Diagnostics;
using DotnetBindgen;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class PropertyOverrideUnknownTests
{
    [Fact]
    public void UnknownParticipatesIn_PropertyOverrideUnion_AndAvoidsOutOfScopeTypes()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "property-override-unknown-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "PropertyOverrideUnknownFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "dotnet-bindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "PropertyOverrideUnknownFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"dotnet-bindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var nsDir = Path.Combine(emitOut, "NamespaceA");
        var internalIndex = Path.Combine(nsDir, "internal", "index.d.ts");
        Assert.True(File.Exists(internalIndex), $"Missing internal index: {internalIndex}");

        var dts = File.ReadAllText(internalIndex);

        Assert.Contains("export interface BaseType$instance", dts);
        Assert.Contains("import type { DerivedEvents }", dts);
        Assert.Contains("Events: DerivedEvents | unknown;", dts);

        // MUST not reference derived-only types in the base namespace module.
        Assert.Contains("DerivedEvents", dts);

        Assert.DoesNotContain("Events: JsValue;", dts);
        Assert.DoesNotContain(" | JsValue", dts);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotnetBindgen.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Failed to locate dotnet-bindgen repo root from test base directory.");
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
