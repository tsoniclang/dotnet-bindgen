using System.Collections.Immutable;
using DotnetBindgen.Emit;
using DotnetBindgen.Emit.Printers;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Types;
using DotnetBindgen.Renaming;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class TypeSurfaceHardeningTests
{
    [Fact]
    public void Builtin_SystemObject_MapsTo_Unknown()
    {
        Assert.True(TypeMap.TryMapBuiltin("System.Object", out var tsType));
        Assert.Equal("unknown", tsType);
    }

    [Fact]
    public void Builtin_SystemValueType_MapsTo_NonNullableUnknown()
    {
        Assert.True(TypeMap.TryMapBuiltin("System.ValueType", out var tsType));
        Assert.Equal("NonNullable<unknown>", tsType);
    }

    [Fact]
    public void Resolver_UsesOpaquePlaceholder_ForOmittedNestedTypes()
    {
        var ctx = BuildContext.Create();
        var outer = CreateType("FixtureAssembly", "Fixture.Outer", "Outer");
        var graph = CreateGraph("Fixture", outer);
        var resolver = new TypeNameResolver(ctx, graph);

        var resolved = resolver.For(new NamedTypeReference
        {
            AssemblyName = "FixtureAssembly",
            FullName = "Fixture.Outer+Hidden",
            Namespace = "Fixture",
            Name = "Outer+Hidden",
            Arity = 0,
            TypeArguments = [],
            IsValueType = false
        });

        Assert.Equal("__OpaqueClrType<\"omitted-nested-type:Fixture.Outer+Hidden\">", resolved);
    }

    [Fact]
    public void Resolver_UsesOpaquePlaceholder_ForNonPublicTypesInSignatures()
    {
        var ctx = BuildContext.Create();
        var hidden = CreateType("FixtureAssembly", "Fixture.Hidden", "Hidden", Accessibility.Internal);
        var graph = CreateGraph("Fixture", hidden);
        var resolver = new TypeNameResolver(ctx, graph);

        var resolved = resolver.For(new NamedTypeReference
        {
            AssemblyName = "FixtureAssembly",
            FullName = "Fixture.Hidden",
            Namespace = "Fixture",
            Name = "Hidden",
            Arity = 0,
            TypeArguments = [],
            IsValueType = false
        });

        Assert.Equal("__OpaqueClrType<\"non-public-type:Fixture.Hidden\">", resolved);
    }

    [Fact]
    public void FreeGenericParameters_EmitExplicitOpaquePlaceholders()
    {
        var ctx = BuildContext.Create();
        var resolver = new TypeNameResolver(ctx, EmptyGraph());

        var resolved = TypeRefPrinter.Print(
            new GenericParameterReference
            {
                Id = new GenericParameterId
                {
                    DeclaringTypeName = "Fixture.Box`1",
                    Position = 0
                },
                Name = "T",
                Position = 0,
                Constraints = []
            },
            resolver,
            ctx,
            allowedTypeParameterNames: new HashSet<string>(StringComparer.Ordinal) { "U" });

        Assert.Equal("__OpaqueClrType<\"free-type-param:T\">", resolved);
    }

    private static SymbolGraph EmptyGraph() =>
        new SymbolGraph
        {
            Namespaces = ImmutableArray<NamespaceSymbol>.Empty,
            SourceAssemblies = ImmutableHashSet<string>.Empty
        }.WithIndices();

    private static SymbolGraph CreateGraph(string @namespace, params TypeSymbol[] types) =>
        new SymbolGraph
        {
            Namespaces =
            [
                new NamespaceSymbol
                {
                    Name = @namespace,
                    Types = types.ToImmutableArray(),
                    StableId = new TypeStableId
                    {
                        AssemblyName = "Namespace",
                        ClrFullName = @namespace
                    },
                    ContributingAssemblies = types
                        .Select(type => type.StableId.AssemblyName)
                        .ToImmutableHashSet(StringComparer.Ordinal)
                }
            ],
            SourceAssemblies = types
                .Select(type => type.StableId.AssemblyName)
                .ToImmutableHashSet(StringComparer.Ordinal)
        }.WithIndices();

    private static TypeSymbol CreateType(
        string assemblyName,
        string clrFullName,
        string clrName,
        Accessibility accessibility = Accessibility.Public) =>
        new()
        {
            StableId = new TypeStableId
            {
                AssemblyName = assemblyName,
                ClrFullName = clrFullName
            },
            ClrFullName = clrFullName,
            ClrName = clrName,
            TsEmitName = clrName,
            Namespace = "Fixture",
            Kind = TypeKind.Class,
            Arity = 0,
            GenericParameters = ImmutableArray<GenericParameterSymbol>.Empty,
            Interfaces = ImmutableArray<TypeReference>.Empty,
            Members = TypeMembers.Empty,
            NestedTypes = ImmutableArray<TypeSymbol>.Empty,
            IsValueType = false,
            Accessibility = accessibility
        };
}
