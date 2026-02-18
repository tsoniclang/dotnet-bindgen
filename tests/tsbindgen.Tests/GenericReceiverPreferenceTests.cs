using System.Diagnostics;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class GenericReceiverPreferenceTests
{
    [Fact]
    public void ExtensionIndex_PrefersGenericReceiverBucketsOverArity0Bases()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "extension-scopes-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "ExtensionScopesFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
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

        Assert.True(result.Success, $"tsbindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var extensionIndex = Path.Combine(emitOut, "__internal", "extensions", "index.d.ts");
        Assert.True(File.Exists(extensionIndex), $"Missing extension index: {extensionIndex}");

        var dts = File.ReadAllText(extensionIndex);

        // Arity-0 base buckets must be merged into arity-1 buckets using __TsonicPreferExt
        // so overlapping members (e.g. AsParallel) prefer the generic receiver return types.
        //
        // Fixture: ISeq (arity 0) + ISeq<T> : ISeq (arity 1) both define AsParallel.
        Assert.Contains("__TsonicPreferExt<__Ext_ExtensionScopesFixture_ISeq, __Ext_ExtensionScopesFixture_ISeq_1", dts);
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
