using System.Diagnostics;
using System.Text.Json;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class ProtectedVirtualMembersTests
{
    [Fact]
    public void ProtectedVirtuals_AppearOnInstanceSurface_WithoutNumericRenames()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "protected-virtual-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "ProtectedVirtualFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "ProtectedVirtualFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"tsbindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        var nsDir = Path.Combine(emitOut, "ProtectedVirtualFixture");
        var internalIndex = Path.Combine(nsDir, "internal", "index.d.ts");
        var bindingsJson = Path.Combine(nsDir, "bindings.json");

        Assert.True(File.Exists(internalIndex), $"Missing internal index: {internalIndex}");
        Assert.True(File.Exists(bindingsJson), $"Missing bindings.json: {bindingsJson}");

        var dts = File.ReadAllText(internalIndex);

        // No separate $protected surface; we include protected virtual/abstract/override members
        // directly on the instance surface to avoid unstable numeric renames (Dispose2, ...).
        Assert.DoesNotContain("$protected", dts);

        // Instance surface should expose protected virtual/abstract/override members for override typing.
        var instanceStart = dts.IndexOf("export interface Base$instance", StringComparison.Ordinal);
        Assert.True(instanceStart >= 0, "Expected Base$instance interface in output.");
        var instanceEnd = dts.IndexOf("}\n", instanceStart, StringComparison.Ordinal);
        Assert.True(instanceEnd > instanceStart, "Failed to extract Base$instance interface block from output.");
        var instanceBlock = dts.Substring(instanceStart, instanceEnd - instanceStart);

        // Interfaces cannot encode access modifiers; the surface is callable, but C# enforces accessibility.
        Assert.DoesNotContain("\n    protected ", instanceBlock);

        // Includes protected virtual members.
        Assert.Contains("Foo(", instanceBlock);
        Assert.Contains("Bar(", instanceBlock);
        Assert.Contains("Prop", instanceBlock);
        Assert.Contains("Prop2", instanceBlock);

        // Includes overload family without unstable renames.
        Assert.Contains("Dispose(", instanceBlock);
        Assert.DoesNotContain("Dispose2", instanceBlock);
        var disposeDeclCount = instanceBlock.Split("Dispose(", StringSplitOptions.None).Length - 1;
        Assert.True(disposeDeclCount >= 2, $"Expected at least 2 Dispose overload declarations, found {disposeDeclCount}.");

        // Excludes non-virtual protected members.
        Assert.DoesNotContain("ProtectedNonVirtual", instanceBlock);

        // Excludes internal/private-protected members.
        Assert.DoesNotContain("InternalVirt", instanceBlock);
        Assert.DoesNotContain("PrivateProtectedVirt", instanceBlock);

        // bindings.json includes visibility + base type and interface heritage.
        using var doc = JsonDocument.Parse(File.ReadAllText(bindingsJson));
        var types = doc.RootElement
            .GetProperty("targetSurface")
            .GetProperty("types")
            .EnumerateArray()
            .ToList();

        var baseType = types.Single(t => t.GetProperty("targetName").GetString() == "ProtectedVirtualFixture.Base");
        Assert.True(baseType.TryGetProperty("baseType", out _), "Expected baseType in bindings.json");

        var methods = baseType.GetProperty("methods").EnumerateArray().ToList();
        var foo = methods.Single(m => m.GetProperty("targetName").GetString() == "Foo");
        Assert.Equal("Protected", foo.GetProperty("visibility").GetString());

        var bar = methods.Single(m => m.GetProperty("targetName").GetString() == "Bar");
        Assert.Equal("ProtectedInternal", bar.GetProperty("visibility").GetString());

        Assert.DoesNotContain(methods, m => m.GetProperty("targetName").GetString() == "InternalVirt");
        Assert.DoesNotContain(methods, m => m.GetProperty("targetName").GetString() == "PrivateProtectedVirt");
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
