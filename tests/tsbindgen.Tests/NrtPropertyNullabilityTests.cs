using System.Diagnostics;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class NrtPropertyNullabilityTests
{
    [Fact]
    public void NullableProperties_DoNotGetNormalizedAcrossUnrelatedTypes_AndSetterAcceptsNullable()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "nrt-property-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "NrtPropertyFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "NrtPropertyFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"tsbindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var nsDir = Path.Combine(emitOut, "NrtPropertyFixture");
        var internalIndex = Path.Combine(nsDir, "internal", "index.d.ts");
        Assert.True(File.Exists(internalIndex), $"Missing internal index: {internalIndex}");

        var dts = File.ReadAllText(internalIndex);

        // BuildRequest.baseURL is nullable and MUST stay nullable even though SiteConfig.baseURL is non-nullable.
        Assert.Contains("export interface BuildRequest$instance", dts);
        Assert.Contains("get baseURL(): string | null;", dts);
        Assert.Contains("set baseURL(value: string | null);", dts);

        // themesDir: also nullable (and setter should accept nullable too).
        Assert.Contains("get themesDir(): string | null;", dts);
        Assert.Contains("set themesDir(value: string | null);", dts);

        // SiteConfig.baseURL is non-nullable.
        Assert.Contains("export interface SiteConfig$instance", dts);
        Assert.Contains("baseURL: string;", dts);
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
