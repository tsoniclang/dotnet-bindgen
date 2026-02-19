using System.Diagnostics;
using System.Text.Json;
using tsbindgen;
using tsbindgen.Core.Policy;
using Xunit;

namespace tsbindgen.Tests;

public sealed class ModuleContainerExportsTests
{
    [Fact]
    public void ModuleContainerMarker_EmitsNamespaceExports()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "module-container-exports-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "ModuleContainerExportsFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "ModuleContainerExportsFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(result.Success, $"tsbindgen build failed: {string.Join("\n", result.Diagnostics.Select(d => d.ToString()))}");

        // Bindings.json must include `exports` for module containers.
        var bindingsPath = Path.Combine(emitOut, "ModuleContainerExportsFixture", "bindings.json");
        Assert.True(File.Exists(bindingsPath), $"Missing bindings.json: {bindingsPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(bindingsPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("exports", out var exports), "bindings.json is missing `exports`");

        AssertExport(exports, "buildSite", "method", "ModuleContainerExportsFixture.BuildSite", "ModuleContainerExportsFixture", "buildSite");
        AssertExport(exports, "Version", "property", "ModuleContainerExportsFixture.BuildSite", "ModuleContainerExportsFixture", "Version");
        AssertExport(exports, "Count", "field", "ModuleContainerExportsFixture.BuildSite", "ModuleContainerExportsFixture", "Count");

        // Facade surface must include the flattened exports.
        var facadePath = Path.Combine(emitOut, "ModuleContainerExportsFixture.d.ts");
        Assert.True(File.Exists(facadePath), $"Missing facade file: {facadePath}");

        var dts = File.ReadAllText(facadePath);
        Assert.Contains("export declare function buildSite(req: Internal.BuildRequest): Internal.BuildResult;", dts);
        Assert.Contains("export declare const Log: Action_1<string>;", dts);
        Assert.Contains("export declare const Version", dts);
        Assert.Contains("export declare const Count", dts);

        // Non-public members must not be emitted.
        Assert.DoesNotContain("Hidden", dts);
    }

    [Fact]
    public void FlattenedClass_CollidingWithModuleContainer_IsDiagnosticError()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "module-container-exports-fixture");
        var fixtureProj = Path.Combine(fixtureDir, "ModuleContainerExportsFixture.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(fixtureProj, buildOut);

        var assemblyPath = Path.Combine(buildOut, "ModuleContainerExportsFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Fixture assembly not found: {assemblyPath}");

        var emitOut = Path.Combine(scratch, "emit-out");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        var defaultPolicy = PolicyDefaults.Create();
        var policy = defaultPolicy with
        {
            Emission = defaultPolicy.Emission with
            {
                FlattenedClasses = new HashSet<string>
                {
                    // Collides with the module container's exported buildSite(...)
                    "ModuleContainerExportsFixture.OtherExports"
                }
            }
        };

        var result = Builder.Build(
            assemblyPaths: new[] { assemblyPath },
            outputDirectory: emitOut,
            policy: policy,
            referenceDirectories: new[] { runtimeDir! });

        Assert.False(result.Success, "Expected build to fail due to flattened export name collision.");
        Assert.Contains(result.Diagnostics, d => d.Code == tsbindgen.Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved);
    }

    private static void AssertExport(
        JsonElement exports,
        string exportName,
        string expectedKind,
        string expectedDeclaringClrType,
        string expectedDeclaringAssembly,
        string expectedClrName)
    {
        Assert.True(exports.TryGetProperty(exportName, out var exp), $"Missing export '{exportName}'");

        Assert.Equal(expectedKind, exp.GetProperty("kind").GetString());
        Assert.Equal(expectedDeclaringClrType, exp.GetProperty("declaringClrType").GetString());
        Assert.Equal(expectedDeclaringAssembly, exp.GetProperty("declaringAssemblyName").GetString());
        Assert.Equal(expectedClrName, exp.GetProperty("clrName").GetString());
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
