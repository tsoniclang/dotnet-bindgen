using System.Diagnostics;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class FriendlyImportAliasesTests
{
    [Fact]
    public void InternalIndex_Uses_InternalIndex_Imports_Without_Friendly_Aliasing()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "friendly-import-aliases-fixture");
        var libProj = Path.Combine(fixtureDir, "FriendlyImportsLib.csproj");
        var consumerProj = Path.Combine(fixtureDir, "FriendlyImportsConsumer.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(libProj, buildOut);
        DotnetBuild(consumerProj, buildOut);

        var libAssemblyPath = Path.Combine(buildOut, "FriendlyImportsLib.dll");
        Assert.True(File.Exists(libAssemblyPath), $"Fixture assembly not found: {libAssemblyPath}");

        var consumerAssemblyPath = Path.Combine(buildOut, "FriendlyImportsConsumer.dll");
        Assert.True(File.Exists(consumerAssemblyPath), $"Fixture assembly not found: {consumerAssemblyPath}");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        // 1) Emit a tsbindgen package for the library assembly (normal mode).
        var emitLibOut = Path.Combine(scratch, "emit-lib");
        var libResult = Builder.Build(
            assemblyPaths: new[] { libAssemblyPath },
            outputDirectory: emitLibOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(libResult.Success, $"tsbindgen build failed (lib): {string.Join("\n", libResult.Diagnostics.Select(d => d.ToString()))}");

        var libPkgDir = Path.Combine(emitLibOut, "FriendlyImportsLib");
        Assert.True(Directory.Exists(libPkgDir), $"Missing emitted lib package dir: {libPkgDir}");

        // Builder.Build emits bindings + d.ts, but packaging (package.json) is normally handled by repo scripts.
        // For test purposes, create a minimal package.json so the library contract loader has a stable package name.
        var libPkgJson = Path.Combine(libPkgDir, "package.json");
        if (!File.Exists(libPkgJson))
        {
            File.WriteAllText(libPkgJson, "{ \"name\": \"FriendlyImportsLib\" }\n");
        }

        Assert.True(File.Exists(libPkgJson), "Missing lib package.json");
        Assert.True(Directory.GetFiles(libPkgDir, "bindings.json", SearchOption.AllDirectories).Length > 0, "Missing lib bindings.json");

        // 2) Emit TS for consumer assembly using the emitted library package as a --lib dependency.
        var emitConsumerOut = Path.Combine(scratch, "emit-consumer");
        var consumerResult = Builder.Build(
            assemblyPaths: new[] { consumerAssemblyPath },
            outputDirectory: emitConsumerOut,
            libraryPackagePaths: new[] { libPkgDir },
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(consumerResult.Success, $"tsbindgen build failed (consumer): {string.Join("\n", consumerResult.Diagnostics.Select(d => d.ToString()))}");

        var consumerInternalIndex = Path.Combine(emitConsumerOut, "FriendlyImportsConsumer", "internal", "index.d.ts");
        Assert.True(File.Exists(consumerInternalIndex), $"Missing consumer internal index: {consumerInternalIndex}");

        var dts = File.ReadAllText(consumerInternalIndex);

        // Internal index output must not "prettify" generic names with import aliases.
        // We want arity-stable imports (Box_1, Database_1_1, etc.) for airplane-grade correctness.
        Assert.DoesNotContain("Box_1 as Box", dts);

        // Generic should stay arity-stable in signatures.
        Assert.Contains("GetBox(): Box_1<", dts);
        Assert.DoesNotContain("GetBox(): Box<", dts);

        // Database_1 (non-generic) is a legitimate CLR name; it must remain as-is.
        Assert.Contains("GetDb0(): Database_1", dts);

        // Database_1<T> SHOULD NOT be aliased to Database_1 because that would collide with the non-generic sibling.
        Assert.DoesNotContain("Database_1_1 as Database_1", dts);
        Assert.Contains("GetDb1(): Database_1_1<", dts);

        // Library imports in internal/index.d.ts must come from dependency internal indices, not facades.
        Assert.Contains("/internal/index.js", dts);
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
            Arguments = $"build \"{projectFile}\" -c Release -o \"{outputDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start dotnet build process.");

        proc.WaitForExit();

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        Assert.True(proc.ExitCode == 0, $"dotnet build failed (exit {proc.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }
}
