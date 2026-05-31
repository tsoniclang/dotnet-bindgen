using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;
using tsbindgen.Renaming;

namespace tsbindgen.Emit;

/// <summary>
/// Provides on-demand access to binding exposures for types.
/// Generates and caches ExposedMethods/ExposedProperties for use by emitters.
/// This allows emitters to access complete overload sets (own + inherited members).
/// </summary>
public sealed class BindingsProvider
{
    private readonly BuildContext _ctx;
    private readonly SymbolGraph _graph;
    private readonly Dictionary<string, TypeBindingCache> _cache = new();

    public BindingsProvider(BuildContext ctx, SymbolGraph graph)
    {
        _ctx = ctx;
        _graph = graph;
    }

    /// <summary>
    /// Get all exposed methods for a type (own + inherited).
    /// Returns null if type has no methods to expose.
    /// </summary>
    public List<MethodExposureInfo>? GetExposedMethods(TypeSymbol type)
    {
        var binding = GetOrCreateBinding(type);
        return binding.ExposedMethods;
    }

    /// <summary>
    /// Get all exposed properties for a type (own + inherited).
    /// Returns null if type has no properties to expose.
    /// </summary>
    public List<PropertyExposureInfo>? GetExposedProperties(TypeSymbol type)
    {
        var binding = GetOrCreateBinding(type);
        return binding.ExposedProperties;
    }

    private TypeBindingCache GetOrCreateBinding(TypeSymbol type)
    {
        if (_cache.TryGetValue(type.ClrFullName, out var cached))
            return cached;

        var binding = GenerateBinding(type);
        _cache[type.ClrFullName] = binding;
        return binding;
    }

    private TypeBindingCache GenerateBinding(TypeSymbol type)
    {
        var inheritanceSubstitution = BuildInheritanceSubstitutionMap(type);

        // Collect instance methods (own + inherited)
        var exposedMethods = CollectExposedMethods(type, inheritanceSubstitution);

        // Collect instance properties (own + inherited)
        var exposedProperties = CollectExposedProperties(type, inheritanceSubstitution);

        return new TypeBindingCache
        {
            ExposedMethods = exposedMethods.Any() ? exposedMethods : null,
            ExposedProperties = exposedProperties.Any() ? exposedProperties : null
        };
    }

    private List<MethodExposureInfo> CollectExposedMethods(TypeSymbol type, Dictionary<GenericParameterId, TypeReference> inheritanceSubstitution)
    {
        var exposures = new List<MethodExposureInfo>();

        // Add type's own methods (ONLY ClassSurface - ViewOnly methods are emitted separately)
        foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && !m.IsStatic))
        {
            var substitutedMethod = ApplyInheritanceSubstitution(method, inheritanceSubstitution);
            var tsName = GetMethodTsName(method, type);
            var tsSignatureId = NormalizeMethodForExposure(substitutedMethod);

            exposures.Add(new MethodExposureInfo
            {
                Method = substitutedMethod,
                TsName = tsName,
                TsSignatureId = tsSignatureId,
                DeclaringType = type,
                IsInherited = false
            });
        }

        // Collect inherited methods from base classes
        CollectInheritedMethods(type, type, exposures, inheritanceSubstitution);

        // Explicit override-wins deduplication
        // Group by (ClrName, TsSignatureId, IsStatic) and ensure only one exposure per signature
        var deduplicated = DeduplicateMethodExposures(type, exposures);

        return deduplicated;
    }

    /// <summary>
    /// Phase 1.1: Explicit override-wins deduplication for method exposures.
    /// Groups by (ClrName, TsSignatureId, IsStatic) and ensures only one exposure per signature.
    /// Derived type's version wins over inherited versions.
    /// </summary>
    private List<MethodExposureInfo> DeduplicateMethodExposures(TypeSymbol type, List<MethodExposureInfo> exposures)
    {
        var deduplicated = new List<MethodExposureInfo>();

        // Group by (ClrName, TsSignatureId, IsStatic)
        var groups = exposures
            .GroupBy(e => new
            {
                ClrName = e.Method.ClrName,
                e.TsSignatureId,
                IsStatic = e.Method.IsStatic
            });

        foreach (var group in groups)
        {
            var candidates = group.ToList();

            // PHASEGATE: Assert at most one non-inherited (own) method per signature
            var ownMethods = candidates.Where(e => !e.IsInherited).ToList();
            if (ownMethods.Count > 1)
            {
                throw new System.InvalidOperationException(
                    $"PHASEGATE VIOLATION: Type {type.ClrFullName} has multiple own methods with same signature:\n" +
                    $"  CLR Name: {group.Key.ClrName}\n" +
                    $"  Signature: {group.Key.TsSignatureId}\n" +
                    $"  Count: {ownMethods.Count}\n" +
                    $"  This indicates a bug in method normalization or method collection.");
            }

            // Override-wins: Prefer own method over inherited
            MethodExposureInfo winner;
            if (ownMethods.Count == 1)
            {
                // Derived type's version wins
                winner = ownMethods[0];

                // If overriding abstract base method, use base's TsName
                // When Renamer adds numeric suffixes due to ViewOnly collisions (equals -> equals3),
                // we must use the base abstract method's TsName so TypeScript sees the implementation
                var inheritedMethods = candidates.Where(e => e.IsInherited).ToList();
                var abstractBase = inheritedMethods.FirstOrDefault(e => e.Method.IsAbstract);
                if (abstractBase != null)
                {
                    // Use base's TsName for the override (e.g., "equals" not "equals3")
                    winner = new MethodExposureInfo
                    {
                        Method = winner.Method,
                        TsName = abstractBase.TsName,  // ← Use base's name
                        TsSignatureId = winner.TsSignatureId,
                        DeclaringType = winner.DeclaringType,
                        IsInherited = winner.IsInherited
                    };
                }
            }
            else
            {
                // All inherited - take first one (from most derived base class)
                winner = candidates[0];
            }

            deduplicated.Add(winner);
        }

        return deduplicated;
    }

    private void CollectInheritedMethods(TypeSymbol derivedType, TypeSymbol currentType, List<MethodExposureInfo> exposures, Dictionary<GenericParameterId, TypeReference> inheritanceSubstitution)
    {
        // Get base type
        if (currentType.BaseType == null) return;

        var baseTypeRef = currentType.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!_graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Cross-namespace inheritance now enabled
        // Import planning (Phase 3.2) will handle types from other namespaces

        // Add ALL inherited ClassSurface methods (deduplication happens in DeduplicateMethodExposures)
        // ViewOnly methods are emitted separately and shouldn't be part of exposures
        foreach (var baseMethod in baseType.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && !m.IsStatic))
        {
            var substitutedMethod = ApplyInheritanceSubstitution(baseMethod, inheritanceSubstitution);
            var baseSignature = NormalizeMethodForExposure(substitutedMethod);

            // Use base type's scope for TS name (where method was declared and renamed)
            var tsName = GetMethodTsName(baseMethod, baseType);

            exposures.Add(new MethodExposureInfo
            {
                Method = substitutedMethod,
                TsName = tsName,
                TsSignatureId = baseSignature,
                DeclaringType = baseType,
                IsInherited = true
            });
        }

        // Recursively collect from base's base
        CollectInheritedMethods(derivedType, baseType, exposures, inheritanceSubstitution);
    }

    private List<PropertyExposureInfo> CollectExposedProperties(TypeSymbol type, Dictionary<GenericParameterId, TypeReference> inheritanceSubstitution)
    {
        var exposures = new List<PropertyExposureInfo>();

        // Add type's own properties (ONLY ClassSurface - ViewOnly properties are emitted separately)
        foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && !p.IsStatic))
        {
            var substitutedProperty = ApplyInheritanceSubstitution(property, inheritanceSubstitution);
            var tsName = GetPropertyTsName(property, type);
            var tsSignatureId = NormalizePropertyForExposure(substitutedProperty);

            exposures.Add(new PropertyExposureInfo
            {
                Property = substitutedProperty,
                TsName = tsName,
                TsSignatureId = tsSignatureId,
                DeclaringType = type,
                IsInherited = false
            });
        }

        // Collect inherited properties from base classes
        CollectInheritedProperties(type, type, exposures, inheritanceSubstitution);

        // Explicit override-wins deduplication
        // Group by (ClrName, TsSignatureId, IsStatic) and ensure only one exposure per signature
        var deduplicated = DeduplicatePropertyExposures(type, exposures);

        return deduplicated;
    }

    /// <summary>
    /// Phase 1.1: Explicit override-wins deduplication for property exposures.
    /// Groups by (ClrName, TsSignatureId, IsStatic) and ensures only one exposure per signature.
    /// Derived type's version wins over inherited versions.
    /// </summary>
    private List<PropertyExposureInfo> DeduplicatePropertyExposures(TypeSymbol type, List<PropertyExposureInfo> exposures)
    {
        var deduplicated = new List<PropertyExposureInfo>();

        // Group by (ClrName, TsSignatureId, IsStatic)
        var groups = exposures
            .GroupBy(e => new
            {
                ClrName = e.Property.ClrName,
                e.TsSignatureId,
                IsStatic = e.Property.IsStatic
            });

        foreach (var group in groups)
        {
            var candidates = group.ToList();

            // PHASEGATE: Assert at most one non-inherited (own) property per signature
            var ownProperties = candidates.Where(e => !e.IsInherited).ToList();
            if (ownProperties.Count > 1)
            {
                throw new System.InvalidOperationException(
                    $"PHASEGATE VIOLATION: Type {type.ClrFullName} has multiple own properties with same signature:\n" +
                    $"  CLR Name: {group.Key.ClrName}\n" +
                    $"  Signature: {group.Key.TsSignatureId}\n" +
                    $"  Count: {ownProperties.Count}\n" +
                    $"  This indicates a bug in signature normalization or property collection.");
            }

            // Override-wins: Prefer own property over inherited
            PropertyExposureInfo winner;
            if (ownProperties.Count == 1)
            {
                // Derived type's version wins
                winner = ownProperties[0];

                // If overriding abstract base property, use base's TsName
                // When Renamer adds numeric suffixes due to ViewOnly collisions,
                // we must use the base abstract property's TsName so TypeScript sees the implementation
                var inheritedProperties = candidates.Where(e => e.IsInherited).ToList();
                var abstractBase = inheritedProperties.FirstOrDefault(e => e.Property.IsAbstract);
                if (abstractBase != null)
                {
                    // Use base's TsName for the override
                    winner = new PropertyExposureInfo
                    {
                        Property = winner.Property,
                        TsName = abstractBase.TsName,  // ← Use base's name
                        TsSignatureId = winner.TsSignatureId,
                        DeclaringType = winner.DeclaringType,
                        IsInherited = winner.IsInherited
                    };
                }
            }
            else
            {
                // All inherited - take first one (from most derived base class)
                winner = candidates[0];
            }

            deduplicated.Add(winner);
        }

        return deduplicated;
    }

    private void CollectInheritedProperties(TypeSymbol derivedType, TypeSymbol currentType, List<PropertyExposureInfo> exposures, Dictionary<GenericParameterId, TypeReference> inheritanceSubstitution)
    {
        // Get base type
        if (currentType.BaseType == null) return;

        var baseTypeRef = currentType.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!_graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Cross-namespace inheritance now enabled
        // Import planning (Phase 3.2) will handle types from other namespaces

        // Add ALL inherited ClassSurface properties (deduplication happens in DeduplicatePropertyExposures)
        // ViewOnly properties are emitted separately and shouldn't be part of exposures
        foreach (var baseProperty in baseType.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && !p.IsStatic))
        {
            var substitutedProperty = ApplyInheritanceSubstitution(baseProperty, inheritanceSubstitution);
            var baseSignature = NormalizePropertyForExposure(substitutedProperty);

            // Use base type's scope for TS name (where property was declared and renamed)
            var tsName = GetPropertyTsName(baseProperty, baseType);

            exposures.Add(new PropertyExposureInfo
            {
                Property = substitutedProperty,
                TsName = tsName,
                TsSignatureId = baseSignature,
                DeclaringType = baseType,
                IsInherited = true
            });
        }

        // Recursively collect from base's base
        CollectInheritedProperties(derivedType, baseType, exposures, inheritanceSubstitution);
    }

    private static string NormalizeMethodForExposure(MethodSymbol method)
    {
        // Exposure identity for "override-wins" deduplication.
        // We include:
        // - method generic arity
        // - parameter ref/out/in modifiers (CLR-significant)
        // - params/default markers (TS-significant)
        // - return type (so BaseOverload signatures that differ only by return are not collapsed)
        var parts = method.Parameters.Select((p) =>
        {
            var refKind = p.IsOut ? "out" : p.IsIn ? "in" : p.IsRef ? "ref" : "val";
            var isParams = p.IsParams ? "params" : "noparams";
            var isOptional = p.HasDefaultValue ? "opt" : "req";
            return $"{refKind}:{isParams}:{isOptional}:{NormalizeTypeKey(p.Type)}";
        });

        return $"{method.ClrName}|arity={method.Arity}|static={(method.IsStatic ? "true" : "false")}|params={string.Join(",", parts)}|ret={NormalizeTypeKey(method.ReturnType)}";
    }

    private static string NormalizeTypeKey(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => $"Named:{named.AssemblyName}:{named.FullName}:{string.Join(",", named.TypeArguments.Select(NormalizeTypeKey))}",
            GenericParameterReference gp => $"GenericParam:{gp.Id}",
            ArrayTypeReference arr => $"Array:{NormalizeTypeKey(arr.ElementType)}:{arr.Rank}",
            PointerTypeReference ptr => $"Pointer:{NormalizeTypeKey(ptr.PointeeType)}:{ptr.Depth}",
            ByRefTypeReference byRef => $"ByRef:{NormalizeTypeKey(byRef.ReferencedType)}",
            NestedTypeReference nested => $"Nested:{NormalizeTypeKey(nested.FullReference)}",
            PlaceholderTypeReference placeholder => $"Placeholder:{placeholder.DebugName}",
            _ => typeRef.ToString() ?? "<opaque>"
        };
    }

    private static string NormalizePropertyForExposure(PropertySymbol prop)
    {
        var accessor = prop.HasGetter && prop.HasSetter
            ? "getset"
            : prop.HasGetter ? "get"
            : prop.HasSetter ? "set"
            : "none";

        var indexParts = prop.IndexParameters.Select(p =>
        {
            var isOptional = p.HasDefaultValue ? "opt" : "req";
            return $"{isOptional}:{NormalizeTypeKey(p.Type)}";
        });

        return $"{prop.ClrName}|static={(prop.IsStatic ? "true" : "false")}|accessor={accessor}|type={NormalizeTypeKey(prop.PropertyType)}|index={string.Join(",", indexParts)}";
    }

    private Dictionary<GenericParameterId, TypeReference> BuildInheritanceSubstitutionMap(TypeSymbol derivedType)
    {
        // Maps generic parameter IDs (from any base type in the chain) to the corresponding
        // closed type argument as seen from the derived type.
        var map = new Dictionary<GenericParameterId, TypeReference>();

        var current = derivedType;
        while (current.BaseType != null)
        {
            NamedTypeReference? baseNamed = current.BaseType switch
            {
                NamedTypeReference n => n,
                NestedTypeReference nested => nested.FullReference,
                _ => null
            };

            if (baseNamed == null)
                break;

            if (!_graph.TryGetType(baseNamed.FullName, out var baseSymbol) || baseSymbol == null)
                break;

            if (baseSymbol.GenericParameters.Length > 0 && baseNamed.TypeArguments.Count == baseSymbol.GenericParameters.Length)
            {
                for (var i = 0; i < baseSymbol.GenericParameters.Length; i++)
                {
                    var gp = baseSymbol.GenericParameters[i];
                    var arg = SubstituteTypeRef(baseNamed.TypeArguments[i], map);
                    map[gp.Id] = arg;
                }
            }

            current = baseSymbol;
        }

        return map;
    }

    private static MethodSymbol ApplyInheritanceSubstitution(MethodSymbol method, Dictionary<GenericParameterId, TypeReference> substitution)
    {
        if (substitution.Count == 0)
            return method;

        var newReturnType = SubstituteTypeRef(method.ReturnType, substitution);
        var newParameters = method.Parameters
            .Select(p => p with { Type = SubstituteTypeRef(p.Type, substitution) })
            .ToImmutableArray();

        return method with
        {
            ReturnType = newReturnType,
            Parameters = newParameters
        };
    }

    private static PropertySymbol ApplyInheritanceSubstitution(PropertySymbol prop, Dictionary<GenericParameterId, TypeReference> substitution)
    {
        if (substitution.Count == 0)
            return prop;

        var newPropertyType = SubstituteTypeRef(prop.PropertyType, substitution);
        var newIndexParameters = prop.IndexParameters
            .Select(p => p with { Type = SubstituteTypeRef(p.Type, substitution) })
            .ToImmutableArray();

        return prop with
        {
            PropertyType = newPropertyType,
            IndexParameters = newIndexParameters
        };
    }

    private static TypeReference SubstituteTypeRef(TypeReference typeRef, Dictionary<GenericParameterId, TypeReference> substitution)
    {
        return typeRef switch
        {
            GenericParameterReference gp when substitution.TryGetValue(gp.Id, out var repl) => repl,

            NamedTypeReference named when named.TypeArguments.Count > 0 => named with
            {
                TypeArguments = named.TypeArguments.Select(t => SubstituteTypeRef(t, substitution)).ToList()
            },

            ArrayTypeReference arr => arr with { ElementType = SubstituteTypeRef(arr.ElementType, substitution) },
            PointerTypeReference ptr => ptr with { PointeeType = SubstituteTypeRef(ptr.PointeeType, substitution) },
            ByRefTypeReference byref => byref with { ReferencedType = SubstituteTypeRef(byref.ReferencedType, substitution) },
            NestedTypeReference nested => nested with
            {
                DeclaringType = SubstituteTypeRef(nested.DeclaringType, substitution),
                FullReference = (NamedTypeReference)SubstituteTypeRef(nested.FullReference, substitution)
            },

            _ => typeRef
        };
    }

    private string GetMethodTsName(Model.Symbols.MemberSymbols.MethodSymbol method, TypeSymbol declaringType)
    {
        // Get TS name from declaring type's scope
        if (method.EmitScope == EmitScope.ViewOnly && method.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, method.IsStatic);
            return _ctx.Renamer.GetFinalMemberName(method.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
            return _ctx.Renamer.GetFinalMemberName(method.StableId, classScope);
        }
    }

    private string GetPropertyTsName(Model.Symbols.MemberSymbols.PropertySymbol property, TypeSymbol declaringType)
    {
        // Get TS name from declaring type's scope
        if (property.EmitScope == EmitScope.ViewOnly && property.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(property.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, property.IsStatic);
            return _ctx.Renamer.GetFinalMemberName(property.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(declaringType, property.IsStatic);
            return _ctx.Renamer.GetFinalMemberName(property.StableId, classScope);
        }
    }
}

/// <summary>
/// Cached binding information for a type.
/// </summary>
public sealed class TypeBindingCache
{
    public List<MethodExposureInfo>? ExposedMethods { get; init; }
    public List<PropertyExposureInfo>? ExposedProperties { get; init; }
}

/// <summary>
/// Information about an exposed method (own or inherited).
/// </summary>
public sealed class MethodExposureInfo
{
    public required Model.Symbols.MemberSymbols.MethodSymbol Method { get; init; }
    public required string TsName { get; init; }
    public required string TsSignatureId { get; init; }
    public required TypeSymbol DeclaringType { get; init; }
    public required bool IsInherited { get; init; }
}

/// <summary>
/// Information about an exposed property (own or inherited).
/// </summary>
public sealed class PropertyExposureInfo
{
    public required Model.Symbols.MemberSymbols.PropertySymbol Property { get; init; }
    public required string TsName { get; init; }
    public required string TsSignatureId { get; init; }
    public required TypeSymbol DeclaringType { get; init; }
    public required bool IsInherited { get; init; }
}
