using System.Diagnostics;
using System.Text.Json;
using tsbindgen;
using Xunit;

namespace tsbindgen.Tests;

public sealed class ProtectedVirtualMembersTests
{
    [Fact]
    public void ProtectedVirtuals_EmitProtectedSurface_AndAreHiddenFromPublicInstance()
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

        // Protected surface exists.
        Assert.Contains("export abstract class Base$protected", dts);

        // Includes protected virtual members.
        Assert.Contains("protected Foo", dts);
        Assert.Contains("protected Bar", dts);
        Assert.Contains("protected Prop", dts);

        // Excludes non-virtual protected members.
        Assert.DoesNotContain("ProtectedNonVirtual", dts);

        // Excludes internal/private-protected members.
        Assert.DoesNotContain("InternalVirt", dts);
        Assert.DoesNotContain("PrivateProtectedVirt", dts);

        // $instance extends $protected but does not re-declare protected members.
        Assert.Contains("export interface Base$instance extends Base$protected", dts);

        // Public instance surface should not expose protected members directly.
        // (We only want these available for override typing, not for public consumption.)
        var instanceStart = dts.IndexOf("export interface Base$instance", StringComparison.Ordinal);
        Assert.True(instanceStart >= 0, "Expected Base$instance interface in output.");
        var instanceEnd = dts.IndexOf("}\n", instanceStart, StringComparison.Ordinal);
        Assert.True(instanceEnd > instanceStart, "Failed to extract Base$instance interface block from output.");
        var instanceBlock = dts.Substring(instanceStart, instanceEnd - instanceStart);

        // Allow `Base$protected` in extends clause, but never emit `protected` members on the public instance interface.
        Assert.DoesNotContain("\n    protected ", instanceBlock);
        Assert.DoesNotContain("Foo(", instanceBlock);
        Assert.DoesNotContain("Bar(", instanceBlock);
        Assert.DoesNotContain("Prop", instanceBlock);

        // bindings.json includes visibility + base type and interface heritage.
        using var doc = JsonDocument.Parse(File.ReadAllText(bindingsJson));
        var types = doc.RootElement.GetProperty("types").EnumerateArray().ToList();

        var baseType = types.Single(t => t.GetProperty("clrName").GetString() == "ProtectedVirtualFixture.Base");
        Assert.True(baseType.TryGetProperty("baseType", out _), "Expected baseType in bindings.json");

        var methods = baseType.GetProperty("methods").EnumerateArray().ToList();
        var foo = methods.Single(m => m.GetProperty("clrName").GetString() == "Foo");
        Assert.Equal("Protected", foo.GetProperty("visibility").GetString());

        var bar = methods.Single(m => m.GetProperty("clrName").GetString() == "Bar");
        Assert.Equal("ProtectedInternal", bar.GetProperty("visibility").GetString());

        Assert.DoesNotContain(methods, m => m.GetProperty("clrName").GetString() == "InternalVirt");
        Assert.DoesNotContain(methods, m => m.GetProperty("clrName").GetString() == "PrivateProtectedVirt");
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
