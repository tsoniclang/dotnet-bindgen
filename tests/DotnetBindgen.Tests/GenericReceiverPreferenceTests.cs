using System.Diagnostics;
using System.Text.RegularExpressions;
using DotnetBindgen;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class GenericReceiverPreferenceTests
{
    [Fact]
    public void ExtensionIndex_PrefersGenericReceiverBucketsOverArity0Bases()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "extension-scopes-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "ExtensionScopesFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "dotnet-bindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "ExtensionScopesFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"dotnet-bindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var extensionIndex = Path.Combine(emitOut, "__internal", "extensions", "index.d.ts");
        Assert.True(File.Exists(extensionIndex), $"Missing extension index: {extensionIndex}");

        var dts = File.ReadAllText(extensionIndex);

        // Fixture: ISeq (arity 0) + ISeq<T> : ISeq (arity 1) both define AsParallel.
        //
        // Airplane-grade: the more specific receiver overload (ISeq<T>) must appear BEFORE
        // the base receiver overload (ISeq). TS overload resolution picks the first
        // matching signature, so order is semantic.
        var methodsTable = Regex.Match(
            dts,
            @"interface __TsonicExtMethods_ExtensionScopesFixture\s*\{([\s\S]*?)\n\}",
            RegexOptions.Singleline);
        Assert.True(methodsTable.Success, "Failed to locate method-table interface for ExtensionScopesFixture in extension index output.");

        var body = methodsTable.Groups[1].Value;

        Assert.Contains("BaseOnly(this: ExtensionScopesFixture.ISeq)", body);
        Assert.Contains("AsParallel<T extends unknown>(this: ExtensionScopesFixture.ISeq_1<T>)", body);
        Assert.Contains("AsParallel(this: ExtensionScopesFixture.ISeq)", body);

        var genericIdx = body.IndexOf("AsParallel<T extends unknown>(this: ExtensionScopesFixture.ISeq_1<T>)", StringComparison.Ordinal);
        var baseIdx = body.IndexOf("AsParallel(this: ExtensionScopesFixture.ISeq)", StringComparison.Ordinal);
        Assert.True(genericIdx >= 0 && baseIdx >= 0, "Failed to locate both AsParallel overloads in method table.");
        Assert.True(genericIdx < baseIdx, "Expected generic receiver overload to appear before base receiver overload.");

        Assert.Contains("AsParallel<T extends unknown>(this: ExtensionScopesFixture.ISeq_1<T>): Rewrap<this, ExtensionScopesFixture.ISeq_1<T>>;", body);
        Assert.Contains("AsParallel(this: ExtensionScopesFixture.ISeq): Rewrap<this, ExtensionScopesFixture.ISeq>;", body);
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
            // Avoid hangs in CI/redirected output scenarios due to MSBuild node reuse keeping stdout/stderr handles open.
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
