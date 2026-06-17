using System.Diagnostics;
using System.Text.RegularExpressions;
using DotnetBindgen;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class StickyExtensionScopesTests
{
    [Fact]
    public void ExtensionIndex_UsesRewrapAndHktAppliers()
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

        // Sticky scopes require Rewrap<this, ReturnShape> in the emitted extension surface.
        Assert.Contains("Rewrap<this,", dts);

        // HKT appliers avoid TypeScript instantiating the applier with a widened structural supertype.
        Assert.Contains("interface __TsonicExtApplier_", dts);
        Assert.Contains("__tsonic_shape", dts);
        Assert.Contains("__tsonic_type", dts);

        // Airplane-grade: avoid mapped-type helpers (Omit/...) in the sticky-scope machinery.
        // These cause TS2321 "Excessive stack depth comparing types" for large extension surfaces.
        Assert.Contains("type __TsonicMergeExtMaps<A, B> = A & B;", dts);
        Assert.DoesNotContain("Omit<", dts);
        Assert.DoesNotContain("__TsonicPreferExt", dts);

        // Airplane-grade: "more specific receiver wins" without mapped types.
        // In method-table typing, this is enforced by overload ordering on `this:` receivers:
        // the more-specific receiver overload (ISeq<T>) must appear before the base overload (ISeq).
        Assert.DoesNotContain("export interface __Ext_", dts);

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

        // Old generic function applier shape is banned (non-deterministic re-application).
        Assert.DoesNotContain("=> __TsonicExtSurface_", dts);
        Assert.DoesNotContain("<TShape>(shape: TShape) =>", dts);

        // Airplane-grade: ensure "more specific receiver wins" for BCL receiver types too.
        // IQueryable<T> is a strict subtype of IEnumerable<T>, so Stamp(this: IQueryable<T>) must
        // appear BEFORE Stamp(this: IEnumerable<T>) in the method table.
        Assert.Contains("Stamp<T extends unknown>(this: System_Linq.IQueryable_1<T>)", body);
        Assert.Contains("Stamp<T extends unknown>(this: System_Collections_Generic.IEnumerable_1<T>)", body);

        var stampQueryableIdx = body.IndexOf(
            "Stamp<T extends unknown>(this: System_Linq.IQueryable_1<T>)",
            StringComparison.Ordinal);
        var stampEnumerableIdx = body.IndexOf(
            "Stamp<T extends unknown>(this: System_Collections_Generic.IEnumerable_1<T>)",
            StringComparison.Ordinal);

        Assert.True(stampQueryableIdx >= 0 && stampEnumerableIdx >= 0, "Failed to locate both Stamp overloads in method table.");
        Assert.True(stampQueryableIdx < stampEnumerableIdx, "Expected IQueryable<T> Stamp overload to appear before IEnumerable<T> Stamp overload.");

        // Also validate ordering in the *real* BCL System.Linq method table.
        // This catches regressions that only surface when generating large BCL extension surfaces.
        var linqMethodsTable = Regex.Match(
            dts,
            @"interface __TsonicExtMethods_System_Linq\s*\{([\s\S]*?)\n\}",
            RegexOptions.Singleline);
        Assert.True(linqMethodsTable.Success, "Failed to locate method-table interface for System.Linq in extension index output.");

        var linqBody = linqMethodsTable.Groups[1].Value;

        var whereEnumerableIdx = linqBody.IndexOf(
            "Where<TSource extends unknown>(this: System_Collections_Generic.IEnumerable_1<TSource>",
            StringComparison.Ordinal);
        var whereQueryableIdx = linqBody.IndexOf(
            "Where<TSource extends unknown>(this: System_Linq.IQueryable_1<TSource>",
            StringComparison.Ordinal);
        var whereParallelIdx = linqBody.IndexOf(
            "Where<TSource extends unknown>(this: System_Linq.ParallelQuery_1<TSource>",
            StringComparison.Ordinal);

        Assert.True(whereEnumerableIdx >= 0, "Failed to locate LINQ Enumerable.Where overload in method table.");
        Assert.True(whereQueryableIdx >= 0, "Failed to locate LINQ Queryable.Where overload in method table.");
        Assert.True(whereParallelIdx >= 0, "Failed to locate LINQ ParallelQuery.Where overload in method table.");

        Assert.True(whereQueryableIdx < whereEnumerableIdx, "Expected Queryable.Where overload to appear before Enumerable.Where overload.");
        Assert.True(whereParallelIdx < whereEnumerableIdx, "Expected ParallelQuery.Where overload to appear before Enumerable.Where overload.");
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
