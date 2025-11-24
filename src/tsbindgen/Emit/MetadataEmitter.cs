using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Normalize;
using tsbindgen.Plan;

namespace tsbindgen.Emit;

/// <summary>
/// Emits metadata.json files with provenance and CLR-specific information.
/// Includes member provenance, emit scopes, and transformation decisions.
/// </summary>
public static class MetadataEmitter
{
    public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
    {
        ctx.Log("MetadataEmitter", "Generating metadata.json files...");

        var emittedCount = 0;

        // Process each namespace in order
        foreach (var nsOrder in plan.EmissionOrder.Namespaces)
        {
            var ns = nsOrder.Namespace;
            ctx.Log("MetadataEmitter", $"  Emitting metadata for: {ns.Name}");

            // Generate metadata (include HonestEmissionPlan for unsatisfiable interfaces)
            var metadata = GenerateMetadata(ctx, nsOrder, plan.HonestEmission);

            // Write to file: output/Namespace.Name/internal/metadata.json (or _root for empty namespace)
            var namespacePath = Path.Combine(outputDirectory, ns.Name);
            // Use _root for empty namespace to avoid case-sensitivity collision with "Internal" namespace
            var subdirName = ns.IsRoot ? "_root" : "internal";
            var internalPath = Path.Combine(namespacePath, subdirName);
            Directory.CreateDirectory(internalPath);

            var outputFile = Path.Combine(internalPath, "metadata.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(metadata, jsonOptions);
            File.WriteAllText(outputFile, json);

            ctx.Log("MetadataEmitter", $"    → {outputFile}");
            emittedCount++;
        }

        ctx.Log("MetadataEmitter", $"Generated {emittedCount} metadata files");
    }

    private static NamespaceMetadata GenerateMetadata(BuildContext ctx, NamespaceEmitOrder nsOrder, HonestEmissionPlan honestEmission)
    {
        var typeMetadata = new List<TypeMetadata>();

        foreach (var typeOrder in nsOrder.OrderedTypes)
        {
            typeMetadata.Add(GenerateTypeMetadata(typeOrder.Type, ctx, honestEmission));
        }

        return new NamespaceMetadata
        {
            Namespace = nsOrder.Namespace.Name,
            ContributingAssemblies = nsOrder.Namespace.ContributingAssemblies.OrderBy(a => a).ToList(),
            Types = typeMetadata
        };
    }

    private static TypeMetadata GenerateTypeMetadata(TypeSymbol type, BuildContext ctx, HonestEmissionPlan honestEmission)
    {
        // Get final TypeScript name from Renamer
        var tsEmitName = ctx.Renamer.GetFinalTypeName(type);

        // PR C: Get unsatisfiable interfaces for this type (if any)
        List<UnsatisfiableInterfaceMetadata>? unsatisfiableInterfaces = null;
        if (honestEmission.UnsatisfiableInterfaces.TryGetValue(type.ClrFullName, out var unsatisfiableList))
        {
            unsatisfiableInterfaces = unsatisfiableList.Select(u => new UnsatisfiableInterfaceMetadata
            {
                InterfaceClrName = u.InterfaceClrName,
                Reason = u.Reason.ToString(),
                IssueCount = u.IssueCount
            }).ToList();
        }

        return new TypeMetadata
        {
            StableId = type.StableId.ToString(),
            ClrName = type.ClrFullName,
            TsEmitName = tsEmitName,
            Kind = type.Kind.ToString(),
            Accessibility = type.Accessibility.ToString(),
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsStatic,
            Arity = type.Arity,
            Methods = type.Members.Methods.Select(m => GenerateMethodMetadata(m, type, ctx)).ToList(),
            Properties = type.Members.Properties.Select(p => GeneratePropertyMetadata(p, type, ctx)).ToList(),
            Fields = type.Members.Fields.Select(f => GenerateFieldMetadata(f, type, ctx)).ToList(),
            Events = type.Members.Events.Select(e => GenerateEventMetadata(e, type, ctx)).ToList(),
            Constructors = type.Members.Constructors.Select(c => GenerateConstructorMetadata(c, type, ctx)).ToList(),
            UnsatisfiableInterfaces = unsatisfiableInterfaces
        };
    }

    private static MethodMetadata GenerateMethodMetadata(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
    {
        // Use view scope for ViewOnly members, class scope for others
        string tsEmitName;
        if (method.EmitScope == EmitScope.ViewOnly && method.SourceInterface != null)
        {
            // ViewOnly member - use view scope
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, method.IsStatic);
            tsEmitName = ctx.Renamer.GetFinalMemberName(method.StableId, viewScope);
        }
        else
        {
            // Class surface member - use class scope
            var classScope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
            tsEmitName = ctx.Renamer.GetFinalMemberName(method.StableId, classScope);
        }

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeMethod(method);

        return new MethodMetadata
        {
            StableId = method.StableId.ToString(),
            ClrName = method.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            Provenance = method.Provenance.ToString(),
            EmitScope = method.EmitScope.ToString(),
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsSealed = method.IsSealed,
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            SourceInterface = method.SourceInterface != null ? GetTypeRefName(method.SourceInterface) : null,
            IsExtensionMethod = method.IsExtensionMethod
        };
    }

    private static PropertyMetadata GeneratePropertyMetadata(PropertySymbol property, TypeSymbol declaringType, BuildContext ctx)
    {
        // Use view scope for ViewOnly members, class scope for others
        string tsEmitName;
        if (property.EmitScope == EmitScope.ViewOnly && property.SourceInterface != null)
        {
            // ViewOnly member - use view scope
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(property.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, property.IsStatic);
            tsEmitName = ctx.Renamer.GetFinalMemberName(property.StableId, viewScope);
        }
        else
        {
            // Class surface member - use class scope
            var classScope = ScopeFactory.ClassSurface(declaringType, property.IsStatic);
            tsEmitName = ctx.Renamer.GetFinalMemberName(property.StableId, classScope);
        }

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeProperty(property);

        return new PropertyMetadata
        {
            StableId = property.StableId.ToString(),
            ClrName = property.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            Provenance = property.Provenance.ToString(),
            EmitScope = property.EmitScope.ToString(),
            IsStatic = property.IsStatic,
            IsAbstract = property.IsAbstract,
            IsVirtual = property.IsVirtual,
            IsOverride = property.IsOverride,
            IsIndexer = property.IsIndexer,
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            SourceInterface = property.SourceInterface != null ? GetTypeRefName(property.SourceInterface) : null
        };
    }

    private static FieldMetadata GenerateFieldMetadata(FieldSymbol field, TypeSymbol declaringType, BuildContext ctx)
    {
        // Fields are always ClassSurface, use class scope
        var classScope = ScopeFactory.ClassSurface(declaringType, field.IsStatic);
        var tsEmitName = ctx.Renamer.GetFinalMemberName(field.StableId, classScope);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeField(field);

        return new FieldMetadata
        {
            StableId = field.StableId.ToString(),
            ClrName = field.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsReadOnly,
            IsLiteral = field.IsConst
        };
    }

    private static EventMetadata GenerateEventMetadata(EventSymbol evt, TypeSymbol declaringType, BuildContext ctx)
    {
        // Events are always ClassSurface, use class scope
        var classScope = ScopeFactory.ClassSurface(declaringType, evt.IsStatic);
        var tsEmitName = ctx.Renamer.GetFinalMemberName(evt.StableId, classScope);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeEvent(evt);

        return new EventMetadata
        {
            StableId = evt.StableId.ToString(),
            ClrName = evt.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            IsStatic = evt.IsStatic
        };
    }

    private static ConstructorMetadata GenerateConstructorMetadata(ConstructorSymbol ctor, TypeSymbol declaringType, BuildContext ctx)
    {
        // Constructors always have name "constructor" in TypeScript, but still get it from Renamer for consistency
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeConstructor(ctor);

        return new ConstructorMetadata
        {
            NormalizedSignature = normalizedSignature,
            IsStatic = ctor.IsStatic,
            ParameterCount = ctor.Parameters.Length
        };
    }

    private static string GetTypeRefName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}

/// <summary>
/// Metadata for a namespace.
/// </summary>
public sealed record NamespaceMetadata
{
    public required string Namespace { get; init; }
    public required List<string> ContributingAssemblies { get; init; }
    public required List<TypeMetadata> Types { get; init; }
}

/// <summary>
/// Metadata for a type.
/// </summary>
public sealed record TypeMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string Kind { get; init; }
    public required string Accessibility { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsSealed { get; init; }
    public required bool IsStatic { get; init; }
    public required int Arity { get; init; }
    public required List<MethodMetadata> Methods { get; init; }
    public required List<PropertyMetadata> Properties { get; init; }
    public required List<FieldMetadata> Fields { get; init; }
    public required List<EventMetadata> Events { get; init; }
    public required List<ConstructorMetadata> Constructors { get; init; }

    /// <summary>
    /// PR C: Interfaces that this type claims to implement in CLR but cannot satisfy in TypeScript.
    /// These are omitted from TypeScript 'implements' clause but preserved here for truth.
    /// Null if no unsatisfiable interfaces.
    /// </summary>
    public List<UnsatisfiableInterfaceMetadata>? UnsatisfiableInterfaces { get; init; }
}

/// <summary>
/// PR C: Metadata for an unsatisfiable interface omitted from TypeScript 'implements' clause.
/// </summary>
public sealed record UnsatisfiableInterfaceMetadata
{
    public required string InterfaceClrName { get; init; }
    public required string Reason { get; init; }
    public required int IssueCount { get; init; }
}

/// <summary>
/// Metadata for a method.
/// </summary>
public sealed record MethodMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string Provenance { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsSealed { get; init; }
    public required int Arity { get; init; }
    public required int ParameterCount { get; init; }
    public string? SourceInterface { get; init; }
    public bool IsExtensionMethod { get; init; }
}

/// <summary>
/// Metadata for a property.
/// </summary>
public sealed record PropertyMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string Provenance { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsIndexer { get; init; }
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }
    public string? SourceInterface { get; init; }
}

/// <summary>
/// Metadata for a field.
/// </summary>
public sealed record FieldMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsReadOnly { get; init; }
    public required bool IsLiteral { get; init; }
}

/// <summary>
/// Metadata for an event.
/// </summary>
public sealed record EventMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
}

/// <summary>
/// Metadata for a constructor.
/// </summary>
public sealed record ConstructorMetadata
{
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required int ParameterCount { get; init; }
}
