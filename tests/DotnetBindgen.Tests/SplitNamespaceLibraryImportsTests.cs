using System.Diagnostics;
using DotnetBindgen;
using DotnetBindgen.Library;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class SplitNamespaceLibraryImportsTests
{
    [Fact]
    public void LibraryMode_Imports_SplitNamespace_Types_From_Correct_Packages()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "split-namespace-lib-fixture");
        var abstractionsProj = Path.Combine(fixtureDir, "SplitNs.Abstractions.csproj");
        var typesProj = Path.Combine(fixtureDir, "SplitNs.Types.csproj");
        var consumerProj = Path.Combine(fixtureDir, "SplitNs.Consumer.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "dotnet-bindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(abstractionsProj, buildOut);
        DotnetBuild(typesProj, buildOut);
        DotnetBuild(consumerProj, buildOut);

        var abstractionsAssemblyPath = Path.Combine(buildOut, "SplitNs.Abstractions.dll");
        Assert.True(File.Exists(abstractionsAssemblyPath), $"Fixture assembly not found: {abstractionsAssemblyPath}");

        var typesAssemblyPath = Path.Combine(buildOut, "SplitNs.Types.dll");
        Assert.True(File.Exists(typesAssemblyPath), $"Fixture assembly not found: {typesAssemblyPath}");

        var consumerAssemblyPath = Path.Combine(buildOut, "SplitNs.Consumer.dll");
        Assert.True(File.Exists(consumerAssemblyPath), $"Fixture assembly not found: {consumerAssemblyPath}");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        // 1) Emit dotnet-bindgen packages for both libraries (normal mode).
        var emitAbstractionsOut = Path.Combine(scratch, "emit-abstractions");
        var emitTypesOut = Path.Combine(scratch, "emit-types");
        Directory.CreateDirectory(emitAbstractionsOut);
        Directory.CreateDirectory(emitTypesOut);

        var abstractionsResult = Builder.Build(
            assemblyPaths: new[] { abstractionsAssemblyPath },
            outputDirectory: emitAbstractionsOut,
            referenceDirectories: new[] { runtimeDir! });
        Assert.True(abstractionsResult.Success, $"dotnet-bindgen build failed (abstractions): {string.Join("\n", abstractionsResult.Diagnostics.Select(d => d.ToString()))}");

        var typesResult = Builder.Build(
            assemblyPaths: new[] { typesAssemblyPath },
            outputDirectory: emitTypesOut,
            referenceDirectories: new[] { runtimeDir! });
        Assert.True(typesResult.Success, $"dotnet-bindgen build failed (types): {string.Join("\n", typesResult.Diagnostics.Select(d => d.ToString()))}");

        // Test contract structure: create minimal package roots containing ONLY the split namespace directory.
        // This keeps the fixture small and aligns with dotnet-bindgen's expected library layout:
        //   <pkgRoot>/<Namespace>/internal/index.d.ts
        var abstractionsPkgDir = CreateMinimalLibraryPackage(
            scratch,
            packageName: "SplitNsAbstractions",
            emittedOutRoot: emitAbstractionsOut,
            namespaceDirName: "Example.SplitNs");

        var typesPkgDir = CreateMinimalLibraryPackage(
            scratch,
            packageName: "SplitNsTypes",
            emittedOutRoot: emitTypesOut,
            namespaceDirName: "Example.SplitNs");

        // Sanity: abstractions package should NOT export DbContext/DbSet.
        var abstractionsInternalIndex = Path.Combine(abstractionsPkgDir, "Example.SplitNs", "internal", "index.d.ts");
        Assert.True(File.Exists(abstractionsInternalIndex), $"Missing abstractions internal index: {abstractionsInternalIndex}");
        var abstractionsDts = File.ReadAllText(abstractionsInternalIndex);
        Assert.Contains("KeylessAttribute", abstractionsDts);
        Assert.Contains("KeylessInfo", abstractionsDts);
        Assert.DoesNotContain("DbContext", abstractionsDts);
        Assert.DoesNotContain("DbSet", abstractionsDts);

        // 2) Emit TS for consumer assembly using BOTH library packages as --lib dependencies.
        // Order matters: pass abstractions first so a namespace→package "first wins" implementation fails.
        var emitConsumerOut = Path.Combine(scratch, "emit-consumer");
        var consumerResult = Builder.Build(
            assemblyPaths: new[] { consumerAssemblyPath },
            outputDirectory: emitConsumerOut,
            libraryPackagePaths: new[] { abstractionsPkgDir, typesPkgDir },
            referenceDirectories: new[] { runtimeDir! });

        Assert.True(consumerResult.Success, $"dotnet-bindgen build failed (consumer): {string.Join("\n", consumerResult.Diagnostics.Select(d => d.ToString()))}");

        var consumerInternalIndex = Path.Combine(emitConsumerOut, "SplitNs.Consumer", "internal", "index.d.ts");
        Assert.True(File.Exists(consumerInternalIndex), $"Missing consumer internal index: {consumerInternalIndex}");

        var consumerDts = File.ReadAllText(consumerInternalIndex);

        // Airplane-grade behavior: import each CLR type from the package that actually defines it.
        // KeylessInfo comes from the abstractions package.
        Assert.Contains("SplitNsAbstractions/Example.SplitNs/internal/index.js", consumerDts);
        Assert.Contains("KeylessInfo", consumerDts);

        // DbContext/DbSet come from the types package.
        Assert.Contains("SplitNsTypes/Example.SplitNs/internal/index.js", consumerDts);
        Assert.Contains("DbContext", consumerDts);
        Assert.Contains("DbSet_1", consumerDts);

        // Critical negative assertion: DbContext must NOT be imported from abstractions package.
        Assert.DoesNotContain("SplitNsAbstractions/Example.SplitNs/internal/index.js\";\nimport type { DbContext", consumerDts);
    }

    [Fact]
    public void LibraryMode_Requires_Explicit_Overrides_For_Ambiguous_Type_Ownership()
    {
        var repoRoot = FindRepoRoot();

        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "split-namespace-lib-fixture");
        var abstractionsProj = Path.Combine(fixtureDir, "SplitNs.Abstractions.csproj");
        var typesProj = Path.Combine(fixtureDir, "SplitNs.Types.csproj");
        var consumerProj = Path.Combine(fixtureDir, "SplitNs.Consumer.csproj");

        var scratch = Path.Combine(Path.GetTempPath(), "dotnet-bindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        var buildOut = Path.Combine(scratch, "fixture-out");
        Directory.CreateDirectory(buildOut);

        DotnetBuild(abstractionsProj, buildOut);
        DotnetBuild(typesProj, buildOut);
        DotnetBuild(consumerProj, buildOut);

        var abstractionsAssemblyPath = Path.Combine(buildOut, "SplitNs.Abstractions.dll");
        Assert.True(File.Exists(abstractionsAssemblyPath), $"Fixture assembly not found: {abstractionsAssemblyPath}");

        var typesAssemblyPath = Path.Combine(buildOut, "SplitNs.Types.dll");
        Assert.True(File.Exists(typesAssemblyPath), $"Fixture assembly not found: {typesAssemblyPath}");

        var consumerAssemblyPath = Path.Combine(buildOut, "SplitNs.Consumer.dll");
        Assert.True(File.Exists(consumerAssemblyPath), $"Fixture assembly not found: {consumerAssemblyPath}");

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate .NET runtime directory for reference resolution.");

        // Emit dotnet-bindgen packages for both libraries (normal mode).
        var emitAbstractionsOut = Path.Combine(scratch, "emit-abstractions");
        var emitTypesOut = Path.Combine(scratch, "emit-types");
        Directory.CreateDirectory(emitAbstractionsOut);
        Directory.CreateDirectory(emitTypesOut);

        var abstractionsResult = Builder.Build(
            assemblyPaths: new[] { abstractionsAssemblyPath },
            outputDirectory: emitAbstractionsOut,
            referenceDirectories: new[] { runtimeDir! });
        Assert.True(abstractionsResult.Success, $"dotnet-bindgen build failed (abstractions): {string.Join("\n", abstractionsResult.Diagnostics.Select(d => d.ToString()))}");

        var typesResult = Builder.Build(
            assemblyPaths: new[] { typesAssemblyPath },
            outputDirectory: emitTypesOut,
            referenceDirectories: new[] { runtimeDir! });
        Assert.True(typesResult.Success, $"dotnet-bindgen build failed (types): {string.Join("\n", typesResult.Diagnostics.Select(d => d.ToString()))}");

        var abstractionsPkgDir = CreateMinimalLibraryPackage(
            scratch,
            packageName: "SplitNsAbstractions",
            emittedOutRoot: emitAbstractionsOut,
            namespaceDirName: "Example.SplitNs");

        // Create TWO library packages with identical type payloads to force ambiguous ownership.
        var typesPkgDirA = CreateMinimalLibraryPackage(
            scratch,
            packageName: "SplitNsTypesA",
            emittedOutRoot: emitTypesOut,
            namespaceDirName: "Example.SplitNs");

        var typesPkgDirB = CreateMinimalLibraryPackage(
            scratch,
            packageName: "SplitNsTypesB",
            emittedOutRoot: emitTypesOut,
            namespaceDirName: "Example.SplitNs");

        // Without overrides, the build must fail deterministically (airplane-grade) when an
        // ambiguous CLR type is actually referenced and an import source is required.
        //
        // NOTE: Builder.Build returns diagnostics (it does not throw).
        var noOverrideResult = Builder.Build(
            assemblyPaths: new[] { consumerAssemblyPath },
            outputDirectory: Path.Combine(scratch, "emit-consumer-no-override"),
            libraryPackagePaths: new[] { abstractionsPkgDir, typesPkgDirA, typesPkgDirB },
            referenceDirectories: new[] { runtimeDir! });

        Assert.False(noOverrideResult.Success, "Build unexpectedly succeeded without overrides for ambiguous library types.");
        Assert.Contains(
            noOverrideResult.Diagnostics,
            d => d.Code == "BUILD_EXCEPTION" && d.Message.Contains("Ambiguous library type ownership", StringComparison.Ordinal));

        // With explicit per-type overrides, the build should succeed deterministically.
        var typesContract = LibraryContractLoader.Load(typesPkgDirA);
        var overrides = typesContract.AllowedClrFullNames.ToDictionary(clr => clr, _ => "SplitNsTypesA", StringComparer.Ordinal);

        var emitConsumerOut = Path.Combine(scratch, "emit-consumer-with-override");
        var consumerResult = Builder.Build(
            assemblyPaths: new[] { consumerAssemblyPath },
            outputDirectory: emitConsumerOut,
            libraryPackagePaths: new[] { abstractionsPkgDir, typesPkgDirA, typesPkgDirB },
            referenceDirectories: new[] { runtimeDir! },
            libraryClrTypePackageOverrides: overrides);

        Assert.True(consumerResult.Success, $"dotnet-bindgen build failed (consumer): {string.Join("\n", consumerResult.Diagnostics.Select(d => d.ToString()))}");

        var consumerInternalIndex = Path.Combine(emitConsumerOut, "SplitNs.Consumer", "internal", "index.d.ts");
        Assert.True(File.Exists(consumerInternalIndex), $"Missing consumer internal index: {consumerInternalIndex}");

        var consumerDts = File.ReadAllText(consumerInternalIndex);
        Assert.Contains("SplitNsTypesA/Example.SplitNs/internal/index.js", consumerDts);
        Assert.DoesNotContain("SplitNsTypesB/Example.SplitNs/internal/index.js", consumerDts);
    }

    private static string CreateMinimalLibraryPackage(
        string scratchDir,
        string packageName,
        string emittedOutRoot,
        string namespaceDirName)
    {
        var pkgRoot = Path.Combine(scratchDir, packageName);
        Directory.CreateDirectory(pkgRoot);

        // package.json at package root (required by LibraryContractLoader)
        File.WriteAllText(Path.Combine(pkgRoot, "package.json"), $"{{ \"name\": \"{packageName}\" }}\n");

        var srcNsDir = Path.Combine(emittedOutRoot, namespaceDirName);
        Assert.True(Directory.Exists(srcNsDir), $"Missing emitted namespace dir: {srcNsDir}");

        var dstNsDir = Path.Combine(pkgRoot, namespaceDirName);
        CopyDirectory(srcNsDir, dstNsDir);

        // Ensure bindings.json exists for the namespace (required by LibraryContractLoader)
        Assert.True(
            File.Exists(Path.Combine(dstNsDir, "bindings.json")),
            $"Missing bindings.json in minimal package namespace dir: {dstNsDir}");

        return pkgRoot;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDir, name), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(destinationDir, name));
        }
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
