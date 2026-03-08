using tsbindgen.Library;
using Xunit;

namespace tsbindgen.Tests;

public sealed class LibraryContractLoaderTests
{
    [Fact]
    public void Load_IgnoresNodeModulesBindings()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        File.WriteAllText(
            Path.Combine(scratch, "package.json"),
            """
            { "name": "@test/lib", "version": "1.0.0" }
            """);

        var ownNsDir = Path.Combine(scratch, "Foo");
        Directory.CreateDirectory(ownNsDir);
        File.WriteAllText(
            Path.Combine(ownNsDir, "bindings.json"),
            """
            {
              "namespace": "Foo",
              "types": [
                { "stableId": "Lib:Foo.Bar" }
              ]
            }
            """);

        // Simulate a repo checkout with transitive dependencies in node_modules.
        // These bindings MUST NOT contribute to the library contract.
        var depNsDir = Path.Combine(scratch, "node_modules", "@test", "dep", "Foo");
        Directory.CreateDirectory(depNsDir);
        File.WriteAllText(
            Path.Combine(depNsDir, "bindings.json"),
            """
            {
              "namespace": "Foo",
              "types": [
                { "stableId": "Dep:Foo.Baz" }
              ]
            }
            """);

        var contract = LibraryContractLoader.Load(scratch);

        Assert.Equal("@test/lib", contract.PackageName);
        Assert.Contains("Foo.Bar", contract.AllowedClrFullNames);
        Assert.DoesNotContain("Foo.Baz", contract.AllowedClrFullNames);
    }

    [Fact]
    public void Load_IgnoresSurfaceRootBindingsJson()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "tsbindgen-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        File.WriteAllText(
            Path.Combine(scratch, "package.json"),
            """
            { "name": "@test/js-like-lib", "version": "1.0.0" }
            """);

        File.WriteAllText(
            Path.Combine(scratch, "bindings.json"),
            """
            {
              "bindings": {
                "Date": {
                  "kind": "global",
                  "assembly": "Test.Runtime",
                  "type": "Test.Runtime.Date"
                }
              }
            }
            """);

        var ownNsDir = Path.Combine(scratch, "Test.Runtime");
        Directory.CreateDirectory(ownNsDir);
        File.WriteAllText(
            Path.Combine(ownNsDir, "bindings.json"),
            """
            {
              "namespace": "Test.Runtime",
              "types": [
                { "stableId": "Test.Runtime:Test.Runtime.Date" }
              ]
            }
            """);

        var contract = LibraryContractLoader.Load(scratch);

        Assert.Equal("@test/js-like-lib", contract.PackageName);
        Assert.Contains("Test.Runtime.Date", contract.AllowedClrFullNames);
    }
}
