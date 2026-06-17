using System.Linq;
using DotnetBindgen;
using DotnetBindgen.Emit;
using DotnetBindgen.Model;
using Xunit;

namespace DotnetBindgen.Tests;

/// <summary>
/// Phase 1.2: Integration tests for BindingsProvider exposure validation.
/// Tests key type hierarchies to ensure override-wins semantics and no duplicates.
/// </summary>
public class BindingsProviderTests
{
    /// <summary>
    /// Helper to validate ExposedMethods for a type.
    /// </summary>
    private static void ValidateExposedMethods(
        BuildContext ctx,
        SymbolGraph graph,
        string typeFullName,
        string[] expectedOwnMethods,
        string[] expectedInheritedMethods)
    {
        var provider = new BindingsProvider(ctx, graph);

        // Get type
        if (!graph.TryGetType(typeFullName, out var type))
        {
            throw new System.Exception($"Type {typeFullName} not found in graph");
        }

        // Get exposures
        var exposedMethods = provider.GetExposedMethods(type);
        Assert.NotNull(exposedMethods);

        // Validate no duplicates by signature
        var signatureGroups = exposedMethods.GroupBy(e => e.TsSignatureId);
        foreach (var group in signatureGroups)
        {
            Assert.Single(group); // Each signature should appear exactly once
        }

        // Validate own methods
        var ownMethods = exposedMethods.Where(e => !e.IsInherited).ToList();
        var ownMethodNames = ownMethods.Select(e => e.Method.ClrName).Distinct().OrderBy(n => n).ToArray();
        Assert.Equal(expectedOwnMethods.OrderBy(n => n).ToArray(), ownMethodNames);

        // Validate inherited methods (those not overridden)
        var inheritedMethods = exposedMethods.Where(e => e.IsInherited).ToList();
        var inheritedMethodNames = inheritedMethods.Select(e => e.Method.ClrName).Distinct().OrderBy(n => n).ToArray();
        Assert.Equal(expectedInheritedMethods.OrderBy(n => n).ToArray(), inheritedMethodNames);

        // Validate override-wins: no method appears as both own and inherited
        var ownSignatures = new System.Collections.Generic.HashSet<string>(ownMethods.Select(e => e.TsSignatureId));
        var inheritedSignatures = new System.Collections.Generic.HashSet<string>(inheritedMethods.Select(e => e.TsSignatureId));
        Assert.Empty(ownSignatures.Intersect(inheritedSignatures));
    }

    [Fact(Skip = "Integration test - requires BCL assembly loading infrastructure")]
    public void Stream_FileStream_Hierarchy()
    {
        // This test validates the Stream/FileStream hierarchy
        // Expected behavior:
        // - FileStream has its own Read/Write overrides
        // - FileStream inherits other Stream methods not overridden
        // - No duplicate signatures

        // TODO: Set up BuildContext and SymbolGraph from System.IO.dll
        // BuildContext ctx = ...;
        // SymbolGraph graph = ...;

        // ValidateExposedMethods(
        //     ctx,
        //     graph,
        //     "System.IO.FileStream",
        //     expectedOwnMethods: new[] { "Read", "Write", "Seek", ... },
        //     expectedInheritedMethods: new[] { "Flush", "CopyTo", ... });
    }

    [Fact(Skip = "Integration test - requires BCL assembly loading infrastructure")]
    public void TextWriter_StreamWriter_Hierarchy()
    {
        // This test validates the TextWriter/StreamWriter hierarchy
        // Expected behavior:
        // - StreamWriter overrides Write methods
        // - StreamWriter inherits other TextWriter methods
        // - No duplicate signatures

        // TODO: Set up BuildContext and SymbolGraph from System.IO.dll
        // BuildContext ctx = ...;
        // SymbolGraph graph = ...;

        // ValidateExposedMethods(
        //     ctx,
        //     graph,
        //     "System.IO.StreamWriter",
        //     expectedOwnMethods: new[] { "Write", "WriteLine", ... },
        //     expectedInheritedMethods: new[] { "Flush", ... });
    }

    [Fact(Skip = "Integration test - requires BCL assembly loading infrastructure")]
    public void SystemNetSecurity_SslStream_Hierarchy()
    {
        // This test validates a System.Net.Security type that had TS2416 issues
        // Expected behavior:
        // - SslStream has authentication-specific methods
        // - SslStream inherits Stream methods
        // - No duplicate signatures

        // TODO: Set up BuildContext and SymbolGraph from System.Net.Security.dll
        // BuildContext ctx = ...;
        // SymbolGraph graph = ...;

        // ValidateExposedMethods(
        //     ctx,
        //     graph,
        //     "System.Net.Security.SslStream",
        //     expectedOwnMethods: new[] { "AuthenticateAsClient", "AuthenticateAsServer", ... },
        //     expectedInheritedMethods: new[] { "Read", "Write", ... });
    }

    [Fact(Skip = "Integration test - requires BCL assembly loading infrastructure")]
    public void SystemReflectionEmit_TypeBuilder_Hierarchy()
    {
        // This test validates a Reflection.Emit builder type
        // Expected behavior:
        // - TypeBuilder has type building methods
        // - TypeBuilder inherits Type methods
        // - No duplicate signatures

        // TODO: Set up BuildContext and SymbolGraph from System.Reflection.Emit.dll
        // BuildContext ctx = ...;
        // SymbolGraph graph = ...;

        // ValidateExposedMethods(
        //     ctx,
        //     graph,
        //     "System.Reflection.Emit.TypeBuilder",
        //     expectedOwnMethods: new[] { "DefineMethod", "DefineProperty", ... },
        //     expectedInheritedMethods: new[] { "GetMethods", "GetProperties", ... });
    }
}
