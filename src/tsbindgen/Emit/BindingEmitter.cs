using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using tsbindgen.Renaming;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;
using tsbindgen.Normalize;
using tsbindgen.Plan;
using tsbindgen.Surface;

namespace tsbindgen.Emit;

/// <summary>
/// Emits bindings.json files with CLR-to-TypeScript name mappings.
/// Provides correlation data for runtime binding and code generation.
/// </summary>
public static class BindingEmitter
{
    public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
    {
        ctx.Log("BindingEmitter", "Generating bindings.json files...");

        var emittedCount = 0;

        // Process each namespace in order
        foreach (var nsOrder in plan.EmissionOrder.Namespaces)
        {
            var ns = nsOrder.Namespace;
            ctx.Log("BindingEmitter", $"  Emitting bindings for: {ns.Name}");

            // Generate bindings (pass full plan for base type resolution)
            var bindings = GenerateBindings(ctx, plan, nsOrder);

            // Write to file: output/Namespace.Name/bindings.json
            // Use mapped output name if namespace-map is configured
            var outputName = NamespacePathMapper.GetOutputName(ns, ctx);
            var namespacePath = Path.Combine(outputDirectory, outputName);
            Directory.CreateDirectory(namespacePath);

            var outputFile = Path.Combine(namespacePath, "bindings.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            var json = JsonSerializer.Serialize(bindings, jsonOptions);
            File.WriteAllText(outputFile, json);

            ctx.Log("BindingEmitter", $"    → {outputFile}");
            emittedCount++;
        }

        ctx.Log("BindingEmitter", $"Generated {emittedCount} binding files");
    }

    private static NamespaceBindings GenerateBindings(BuildContext ctx, EmissionPlan plan, NamespaceEmitOrder nsOrder)
    {
        var typeBindings = new List<TypeBinding>();

        foreach (var typeOrder in EnumerateTypeOrders(nsOrder.OrderedTypes))
        {
            typeBindings.Add(GenerateTypeBinding(typeOrder.Type, ctx));
        }

        // Collect flattened named exports (module containers + explicit --flatten-class).
        var exports = CollectNamedExports(ctx, nsOrder);

        return new NamespaceBindings
        {
            Namespace = nsOrder.Namespace.Name,
            ContributingAssemblies = nsOrder.Namespace.ContributingAssemblies
                .OrderBy(a => a)
                .ToList(),
            Types = typeBindings,
            Exports = exports.Count > 0 ? exports : null
        };
    }

    /// <summary>
    /// Collects flattened named exports from:
    /// - Tsonic module container static classes (marked with ModuleContainerAttribute), and
    /// - Static classes explicitly listed in --flatten-class.
    /// </summary>
    private static Dictionary<string, ExportBinding> CollectNamedExports(BuildContext ctx, NamespaceEmitOrder nsOrder)
    {
        var exports = new Dictionary<string, ExportBinding>();
        var flattenedClasses = ctx.Policy.Emission.FlattenedClasses;
        var orderedTypes = EnumerateTypeOrders(nsOrder.OrderedTypes).ToList();

        // Module containers are always eligible, even if no explicit --flatten-class is set.
        var hasAnyFlattening = flattenedClasses.Count > 0 ||
                               orderedTypes.Any(t => t.Type.IsTsonicModuleContainer);
        if (!hasAnyFlattening)
            return exports;

        foreach (var typeOrder in orderedTypes)
        {
            var type = typeOrder.Type;

            // Check if this type should contribute named exports.
            // - Explicit flattening: --flatten-class <ClrFullName>
            // - Automatic: Tsonic module containers (attribute marker)
            var shouldFlatten = type.IsTsonicModuleContainer || flattenedClasses.Contains(type.ClrFullName);
            if (!shouldFlatten)
                continue;

            // Only static classes should be flattened
            if (!type.IsStatic)
            {
                ctx.Log("BindingEmitter", $"WARNING: flattening specified non-static class: {type.ClrFullName}");
                continue;
            }

            ctx.Log("BindingEmitter", $"  Collecting named exports from: {type.ClrFullName}");

            var scope = ScopeFactory.ClassSurface(type, isStatic: true);

            void AddExport(string exportName, ExportBinding binding)
            {
                if (exports.TryGetValue(exportName, out var existing))
                {
                    // Allow method overload groups to unify to the same export target.
                    if (existing.Kind == binding.Kind &&
                        existing.TargetName == binding.TargetName &&
                        existing.OwnerQualifiedName == binding.OwnerQualifiedName &&
                        existing.OwnerIdentity == binding.OwnerIdentity)
                    {
                        return;
                    }

                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved,
                        $"Flattened export name collision '{exportName}' in namespace '{nsOrder.Namespace.Name}'. " +
                        $"Targets: {existing.OwnerQualifiedName}.{existing.TargetName} vs {binding.OwnerQualifiedName}.{binding.TargetName}. " +
                        $"Rename one member or remove one flattening source.");
                    return;
                }

                exports[exportName] = binding;
            }

            // Static methods (export as top-level functions in facade).
            foreach (var method in type.Members.Methods.Where(m =>
                         m.IsStatic &&
                         m.EmitScope != EmitScope.Omitted &&
                         m.Visibility == Visibility.Public))
            {
                var exportName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

                AddExport(exportName, new ExportBinding
                {
                    Kind = NamedExportKind.Method,
                    TargetName = method.ClrName,
                    OwnerQualifiedName = method.StableId.DeclaringClrFullName,
                    OwnerIdentity = method.StableId.AssemblyName
                });
            }

            // Static properties (export as top-level const values).
            foreach (var prop in type.Members.Properties.Where(p =>
                         p.IsStatic &&
                         p.EmitScope != EmitScope.Omitted &&
                         p.Visibility == Visibility.Public &&
                         !p.IsIndexer))
            {
                var exportName = ctx.Renamer.GetFinalMemberName(prop.StableId, scope);
                AddExport(exportName, new ExportBinding
                {
                    Kind = NamedExportKind.Property,
                    TargetName = prop.ClrName,
                    OwnerQualifiedName = prop.StableId.DeclaringClrFullName,
                    OwnerIdentity = prop.StableId.AssemblyName
                });
            }

            // Static fields (export as top-level const values).
            foreach (var field in type.Members.Fields.Where(f =>
                         f.IsStatic &&
                         f.EmitScope != EmitScope.Omitted &&
                         f.Visibility == Visibility.Public))
            {
                var exportName = ctx.Renamer.GetFinalMemberName(field.StableId, scope);
                AddExport(exportName, new ExportBinding
                {
                    Kind = NamedExportKind.Field,
                    TargetName = field.ClrName,
                    OwnerQualifiedName = field.StableId.DeclaringClrFullName,
                    OwnerIdentity = field.StableId.AssemblyName
                });
            }

            ctx.Log("BindingEmitter", $"    Found {exports.Count} named exports");
        }

        return exports;
    }

    private static IEnumerable<TypeEmitOrder> EnumerateTypeOrders(IEnumerable<TypeEmitOrder> orders)
    {
        foreach (var order in orders)
        {
            yield return order;

            foreach (var nested in EnumerateTypeOrders(order.OrderedNestedTypes))
            {
                yield return nested;
            }
        }
    }

    private static TypeBinding GenerateTypeBinding(TypeSymbol type, BuildContext ctx)
    {
        // V1: Generate definitions (what CLR declares on this type)
        var methodDefinitions = type.Members.Methods
            .Select(m => GenerateMethodBinding(m, type, ctx))
            .ToList();
        var propertyDefinitions = type.Members.Properties
            .Select(p => GeneratePropertyBinding(p, type, ctx))
            .ToList();
        var fieldDefinitions = type.Members.Fields
            .Select(f => GenerateFieldBinding(f, type, ctx))
            .ToList();
        var eventDefinitions = type.Members.Events
            .Select(e => GenerateEventBinding(e, type, ctx))
            .ToList();
        var constructorDefinitions = type.Members.Constructors
            .Select(c => GenerateConstructorBinding(c, type, ctx))
            .ToList();

        return new TypeBinding
        {
            StableId = type.StableId.ToString(),
            TargetName = type.ClrFullName,
            OwnerIdentity = type.StableId.AssemblyName,
            MetadataToken = 0, // Types don't have metadata tokens
            Kind = type.Kind.ToString(),
            Accessibility = type.Accessibility.ToString(),
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsStatic,
            Arity = type.Arity,
            BaseType = type.BaseType != null ? GetHeritageTypeBinding(type.BaseType) : null,
            Interfaces = type.Interfaces.Length > 0
                ? type.Interfaces.Select(GetHeritageTypeBinding).ToList()
                : null,
            TypeParameters = type.GenericParameters.Length > 0
                ? type.GenericParameters.Select(p => p.Name).ToList()
                : null,

            // V1: Definitions
            Methods = methodDefinitions,
            Properties = propertyDefinitions,
            Fields = fieldDefinitions,
            Events = eventDefinitions,
            Constructors = constructorDefinitions
        };
    }

    private static MethodBinding GenerateMethodBinding(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
    {
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeMethod(method);

        // Generate parameter modifier vector for ref/out/in semantics
        var modifiers = method.Parameters
            .Select((p, i) => new ParameterModifierMetadata
            {
                Index = i,
                Modifier = p.GetModifier()
            })
            .Where(m => m.Modifier != ParameterModifier.None)
            .ToList();

        return new MethodBinding
        {
            StableId = method.StableId.ToString(),
            TargetName = method.ClrName,
            MetadataToken = method.StableId.MetadataToken ?? 0,
            CanonicalSignature = method.StableId.CanonicalSignature,
            NormalizedSignature = normalizedSignature,
            EmitScope = method.EmitScope.ToString(),
            Provenance = method.Provenance.ToString(),
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsSealed = method.IsSealed,
            Visibility = method.Visibility.ToString(),
            // V2: Add declaring type information from StableId
            OwnerQualifiedName = method.StableId.DeclaringClrFullName,
            OwnerIdentity = method.StableId.AssemblyName,
            IsExtensionMethod = method.IsExtensionMethod,
            SourceInterface = method.SourceInterface != null ? GetTypeRefName(method.SourceInterface) : null,
            ParameterModifiers = modifiers.Count > 0 ? modifiers : null,
            EmitSemantics = GetEmitSemantics(ctx, method.StableId.DeclaringClrFullName, method.ClrName)
        };
    }

    private static EmitSemanticsSpec? GetEmitSemantics(
        BuildContext ctx,
        string declaringClrType,
        string clrMemberName)
    {
        var callStyle = ctx.BindingSemantics.ResolveMethodCallStyle(declaringClrType, clrMemberName);
        if (callStyle is null)
        {
            return null;
        }

        return new EmitSemanticsSpec
        {
            CallStyle = callStyle
        };
    }

    private static PropertyBinding GeneratePropertyBinding(PropertySymbol property, TypeSymbol declaringType, BuildContext ctx)
    {
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeProperty(property);
        var emitSemantics =
            property.IsStatic &&
            !property.HasSetter &&
            StaticGenericMemberSupport.RequiresCallableAccessor(
                property.PropertyType,
                declaringType.GenericParameters)
                ? new EmitSemanticsSpec
                {
                    CallableStaticAccessorKind = "property"
                }
                : null;

        return new PropertyBinding
        {
            StableId = property.StableId.ToString(),
            TargetName = property.ClrName,
            MetadataToken = property.StableId.MetadataToken ?? 0,
            CanonicalSignature = property.StableId.CanonicalSignature,
            NormalizedSignature = normalizedSignature,
            EmitScope = property.EmitScope.ToString(),
            Provenance = property.Provenance.ToString(),
            IsIndexer = property.IsIndexer,
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            IsStatic = property.IsStatic,
            IsAbstract = property.IsAbstract,
            IsVirtual = property.IsVirtual,
            IsOverride = property.IsOverride,
            Visibility = property.Visibility.ToString(),
            SourceInterface = property.SourceInterface != null ? GetTypeRefName(property.SourceInterface) : null,
            // V2: Add declaring type information from StableId
            OwnerQualifiedName = property.StableId.DeclaringClrFullName,
            OwnerIdentity = property.StableId.AssemblyName,
            EmitSemantics = emitSemantics
        };
    }

    private static FieldBinding GenerateFieldBinding(FieldSymbol field, TypeSymbol declaringType, BuildContext ctx)
    {
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeField(field);
        var emitSemantics =
            field.IsStatic &&
            (field.IsReadOnly || field.IsConst) &&
            StaticGenericMemberSupport.RequiresCallableAccessor(
                field.FieldType,
                declaringType.GenericParameters)
                ? new EmitSemanticsSpec
                {
                    CallableStaticAccessorKind = "field"
                }
                : null;

        return new FieldBinding
        {
            StableId = field.StableId.ToString(),
            TargetName = field.ClrName,
            MetadataToken = field.StableId.MetadataToken ?? 0,
            NormalizedSignature = normalizedSignature,
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsReadOnly,
            IsLiteral = field.IsConst,
            Visibility = field.Visibility.ToString(),
            // V2: Add declaring type information from StableId
            OwnerQualifiedName = field.StableId.DeclaringClrFullName,
            OwnerIdentity = field.StableId.AssemblyName,
            EmitSemantics = emitSemantics
        };
    }

    private static EventBinding GenerateEventBinding(EventSymbol evt, TypeSymbol declaringType, BuildContext ctx)
    {
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeEvent(evt);

        return new EventBinding
        {
            StableId = evt.StableId.ToString(),
            TargetName = evt.ClrName,
            MetadataToken = evt.StableId.MetadataToken ?? 0,
            NormalizedSignature = normalizedSignature,
            IsStatic = evt.IsStatic,
            Visibility = evt.Visibility.ToString(),
            // V2: Add declaring type information from StableId
            OwnerQualifiedName = evt.StableId.DeclaringClrFullName,
            OwnerIdentity = evt.StableId.AssemblyName
        };
    }

    private static ConstructorBinding GenerateConstructorBinding(ConstructorSymbol ctor, TypeSymbol declaringType, BuildContext ctx)
    {
        // Constructors always have name "constructor" in TypeScript, but record it from Renamer for consistency
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeConstructor(ctor);

        // Generate parameter modifier vector for ref/out/in semantics
        var modifiers = ctor.Parameters
            .Select((p, i) => new ParameterModifierMetadata
            {
                Index = i,
                Modifier = p.GetModifier()
            })
            .Where(m => m.Modifier != ParameterModifier.None)
            .ToList();

        return new ConstructorBinding
        {
            StableId = ctor.StableId.ToString(),
            MetadataToken = ctor.StableId.MetadataToken ?? 0,
            CanonicalSignature = ctor.StableId.CanonicalSignature,
            NormalizedSignature = normalizedSignature,
            IsStatic = ctor.IsStatic,
            ParameterCount = ctor.Parameters.Length,
            Visibility = ctor.Visibility.ToString(),
            // V2: Add declaring type information from StableId
            OwnerQualifiedName = ctor.StableId.DeclaringClrFullName,
            OwnerIdentity = ctor.StableId.AssemblyName,
            ParameterModifiers = modifiers.Count > 0 ? modifiers : null
        };
    }

    // ============================================================================
    // V2 EXPOSURE COLLECTION (own + inherited members)
    // ============================================================================

    private static List<MethodExposure> CollectMethodExposures(TypeSymbol type, BuildContext ctx, EmissionPlan plan)
    {
        var exposures = new List<MethodExposure>();

        // Start with the type's own methods
        foreach (var method in type.Members.Methods.Where(m => m.EmitScope != EmitScope.Omitted))
        {
            exposures.Add(GenerateMethodExposure(method, type, ctx));
        }

        // Collect inherited methods from base classes
        CollectInheritedMethodExposures(type, type, ctx, plan, exposures);

        return exposures;
    }

    private static List<PropertyExposure> CollectPropertyExposures(TypeSymbol type, BuildContext ctx, EmissionPlan plan)
    {
        var exposures = new List<PropertyExposure>();

        // Start with the type's own properties
        foreach (var property in type.Members.Properties.Where(p => p.EmitScope != EmitScope.Omitted))
        {
            exposures.Add(GeneratePropertyExposure(property, type, ctx));
        }

        // Collect inherited properties from base classes
        CollectInheritedPropertyExposures(type, type, ctx, plan, exposures);

        return exposures;
    }

    private static List<FieldExposure> CollectFieldExposures(TypeSymbol type, BuildContext ctx, EmissionPlan plan)
    {
        var exposures = new List<FieldExposure>();

        // Start with the type's own fields
        foreach (var field in type.Members.Fields)
        {
            exposures.Add(GenerateFieldExposure(field, type, ctx));
        }

        // Fields are not inherited in the same way as methods/properties
        // (they shadow rather than override), so no inheritance collection needed

        return exposures;
    }

    private static List<EventExposure> CollectEventExposures(TypeSymbol type, BuildContext ctx, EmissionPlan plan)
    {
        var exposures = new List<EventExposure>();

        // Start with the type's own events
        foreach (var evt in type.Members.Events)
        {
            exposures.Add(GenerateEventExposure(evt, type, ctx));
        }

        // Collect inherited events from base classes
        CollectInheritedEventExposures(type, type, ctx, plan, exposures);

        return exposures;
    }

    private static void CollectInheritedMethodExposures(
        TypeSymbol derivedType,
        TypeSymbol currentType,
        BuildContext ctx,
        EmissionPlan plan,
        List<MethodExposure> exposures)
    {
        // Get base type
        if (currentType.BaseType == null) return;

        var baseTypeRef = currentType.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!plan.Graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Get methods already exposed on the derived type (to detect overrides)
        var exposedSignatures = new HashSet<string>(
            exposures.Select(e => e.TsSignatureId));

        // Add exposures for inherited methods not already overridden
        foreach (var baseMethod in baseType.Members.Methods.Where(m => m.EmitScope != EmitScope.Omitted))
        {
            var baseSignature = SignatureNormalization.NormalizeMethod(baseMethod);

            // Skip if already exposed (overridden in derived class)
            if (exposedSignatures.Contains(baseSignature))
                continue;

            // Generate exposure with TS name from base class scope, but keep base class as target
            exposures.Add(GenerateInheritedMethodExposure(baseMethod, baseType, ctx));
        }

        // Recursively collect from base's base
        CollectInheritedMethodExposures(derivedType, baseType, ctx, plan, exposures);
    }

    private static void CollectInheritedPropertyExposures(
        TypeSymbol derivedType,
        TypeSymbol currentType,
        BuildContext ctx,
        EmissionPlan plan,
        List<PropertyExposure> exposures)
    {
        // Get base type
        if (currentType.BaseType == null) return;

        var baseTypeRef = currentType.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!plan.Graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Get properties already exposed on the derived type (to detect overrides)
        var exposedSignatures = new HashSet<string>(
            exposures.Select(e => e.TsSignatureId));

        // Add exposures for inherited properties not already overridden
        foreach (var baseProperty in baseType.Members.Properties.Where(p => p.EmitScope != EmitScope.Omitted))
        {
            var baseSignature = SignatureNormalization.NormalizeProperty(baseProperty);

            // Skip if already exposed (overridden in derived class)
            if (exposedSignatures.Contains(baseSignature))
                continue;

            // Generate exposure with TS name from base class scope
            exposures.Add(GenerateInheritedPropertyExposure(baseProperty, baseType, ctx));
        }

        // Recursively collect from base's base
        CollectInheritedPropertyExposures(derivedType, baseType, ctx, plan, exposures);
    }

    private static void CollectInheritedEventExposures(
        TypeSymbol derivedType,
        TypeSymbol currentType,
        BuildContext ctx,
        EmissionPlan plan,
        List<EventExposure> exposures)
    {
        // Get base type
        if (currentType.BaseType == null) return;

        var baseTypeRef = currentType.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!plan.Graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Get events already exposed on the derived type (to detect shadowing)
        var exposedSignatures = new HashSet<string>(
            exposures.Select(e => e.TsSignatureId));

        // Add exposures for inherited events not already shadowed
        foreach (var baseEvent in baseType.Members.Events)
        {
            var baseSignature = SignatureNormalization.NormalizeEvent(baseEvent);

            // Skip if already exposed (shadowed in derived class)
            if (exposedSignatures.Contains(baseSignature))
                continue;

            // Generate exposure with TS name from base class scope
            exposures.Add(GenerateInheritedEventExposure(baseEvent, baseType, ctx));
        }

        // Recursively collect from base's base
        CollectInheritedEventExposures(derivedType, baseType, ctx, plan, exposures);
    }

    // ============================================================================
    // V2 EXPOSURE GENERATION (for own members)
    // ============================================================================

    private static MethodExposure GenerateMethodExposure(MethodSymbol method, TypeSymbol ownerType, BuildContext ctx)
    {
        // Get TS name (same logic as definition)
        string tsName;
        if (method.EmitScope == EmitScope.ViewOnly && method.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(ownerType, interfaceStableId, method.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(method.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(ownerType, method.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(method.StableId, classScope);
        }

        // Use NormalizedSignature as TsSignatureId for overload disambiguation
        var tsSignatureId = SignatureNormalization.NormalizeMethod(method);

        return new MethodExposure
        {
            TsName = tsName,
            IsStatic = method.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = method.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = method.StableId.AssemblyName,
                MetadataToken = method.StableId.MetadataToken ?? 0
            }
        };
    }

    private static PropertyExposure GeneratePropertyExposure(PropertySymbol property, TypeSymbol ownerType, BuildContext ctx)
    {
        // Get TS name (same logic as definition)
        string tsName;
        if (property.EmitScope == EmitScope.ViewOnly && property.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(property.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(ownerType, interfaceStableId, property.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(property.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(ownerType, property.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(property.StableId, classScope);
        }

        var tsSignatureId = SignatureNormalization.NormalizeProperty(property);

        return new PropertyExposure
        {
            TsName = tsName,
            IsStatic = property.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = property.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = property.StableId.AssemblyName,
                MetadataToken = property.StableId.MetadataToken ?? 0
            }
        };
    }

    private static FieldExposure GenerateFieldExposure(FieldSymbol field, TypeSymbol ownerType, BuildContext ctx)
    {
        var classScope = ScopeFactory.ClassSurface(ownerType, field.IsStatic);
        var tsName = ctx.Renamer.GetFinalMemberName(field.StableId, classScope);
        var tsSignatureId = SignatureNormalization.NormalizeField(field);

        return new FieldExposure
        {
            TsName = tsName,
            IsStatic = field.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = field.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = field.StableId.AssemblyName,
                MetadataToken = field.StableId.MetadataToken ?? 0
            }
        };
    }

    private static EventExposure GenerateEventExposure(EventSymbol evt, TypeSymbol ownerType, BuildContext ctx)
    {
        var classScope = ScopeFactory.ClassSurface(ownerType, evt.IsStatic);
        var tsName = ctx.Renamer.GetFinalMemberName(evt.StableId, classScope);
        var tsSignatureId = SignatureNormalization.NormalizeEvent(evt);

        return new EventExposure
        {
            TsName = tsName,
            IsStatic = evt.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = evt.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = evt.StableId.AssemblyName,
                MetadataToken = evt.StableId.MetadataToken ?? 0
            }
        };
    }

    private static ConstructorExposure GenerateConstructorExposure(ConstructorSymbol ctor, TypeSymbol ownerType, BuildContext ctx)
    {
        var tsSignatureId = SignatureNormalization.NormalizeConstructor(ctor);

        return new ConstructorExposure
        {
            IsStatic = ctor.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = ctor.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = ctor.StableId.AssemblyName,
                MetadataToken = ctor.StableId.MetadataToken ?? 0
            }
        };
    }

    // ============================================================================
    // V2 INHERITED EXPOSURE GENERATION
    // (Use declaring type's scope for TS name lookup)
    // ============================================================================

    private static MethodExposure GenerateInheritedMethodExposure(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get TS name from declaring type's scope (not derived type)
        string tsName;
        if (method.EmitScope == EmitScope.ViewOnly && method.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, method.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(method.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(method.StableId, classScope);
        }

        var tsSignatureId = SignatureNormalization.NormalizeMethod(method);

        return new MethodExposure
        {
            TsName = tsName,
            IsStatic = method.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = method.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = method.StableId.AssemblyName,
                MetadataToken = method.StableId.MetadataToken ?? 0
            }
        };
    }

    private static PropertyExposure GenerateInheritedPropertyExposure(PropertySymbol property, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get TS name from declaring type's scope (not derived type)
        string tsName;
        if (property.EmitScope == EmitScope.ViewOnly && property.SourceInterface != null)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(property.SourceInterface);
            var viewScope = ScopeFactory.ViewSurface(declaringType, interfaceStableId, property.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(property.StableId, viewScope);
        }
        else
        {
            var classScope = ScopeFactory.ClassSurface(declaringType, property.IsStatic);
            tsName = ctx.Renamer.GetFinalMemberName(property.StableId, classScope);
        }

        var tsSignatureId = SignatureNormalization.NormalizeProperty(property);

        return new PropertyExposure
        {
            TsName = tsName,
            IsStatic = property.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = property.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = property.StableId.AssemblyName,
                MetadataToken = property.StableId.MetadataToken ?? 0
            }
        };
    }

    private static EventExposure GenerateInheritedEventExposure(EventSymbol evt, TypeSymbol declaringType, BuildContext ctx)
    {
        // Events don't have ViewOnly scope, always use class scope
        var classScope = ScopeFactory.ClassSurface(declaringType, evt.IsStatic);
        var tsName = ctx.Renamer.GetFinalMemberName(evt.StableId, classScope);

        var tsSignatureId = SignatureNormalization.NormalizeEvent(evt);

        return new EventExposure
        {
            TsName = tsName,
            IsStatic = evt.IsStatic,
            TsSignatureId = tsSignatureId,
            Target = new ExposureTarget
            {
                DeclaringClrType = evt.StableId.DeclaringClrFullName,
                DeclaringAssemblyName = evt.StableId.AssemblyName,
                MetadataToken = evt.StableId.MetadataToken ?? 0
            }
        };
    }

    private static string GetTypeRefName(tsbindgen.Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            tsbindgen.Model.Types.NamedTypeReference named => named.FullName,
            tsbindgen.Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            tsbindgen.Model.Types.GenericParameterReference gp => gp.Name,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static HeritageTypeBinding GetHeritageTypeBinding(tsbindgen.Model.Types.TypeReference typeRef)
    {
        var named = typeRef switch
        {
            tsbindgen.Model.Types.NamedTypeReference n => n,
            tsbindgen.Model.Types.NestedTypeReference nested => nested.FullReference,
            _ => null
        };

        if (named == null)
        {
            // Fallback: preserve ToString() in clrName for debugging but avoid crashing emission.
            // This should be rare (e.g., arrays/pointers as heritage types).
            var fallbackName = GetTypeRefName(typeRef);
            return new HeritageTypeBinding
            {
                StableId = $"Unknown:{fallbackName}",
                TargetName = fallbackName
            };
        }

        var stableId = $"{named.AssemblyName}:{named.FullName}";

        var typeArgs = named.TypeArguments.Count > 0
            ? named.TypeArguments.Select(EncodeHeritageTypeArgument).ToList()
            : null;

        return new HeritageTypeBinding
        {
            StableId = stableId,
            TargetName = named.FullName,
            TypeArguments = typeArgs
        };
    }

    private static string EncodeHeritageTypeArgument(tsbindgen.Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            tsbindgen.Model.Types.GenericParameterReference gp => gp.Name,
            tsbindgen.Model.Types.NamedTypeReference named => EncodeHeritageNamed(named),
            tsbindgen.Model.Types.NestedTypeReference nested => EncodeHeritageNamed(nested.FullReference),
            tsbindgen.Model.Types.ArrayTypeReference arr => $"{EncodeHeritageTypeArgument(arr.ElementType)}[]",
            tsbindgen.Model.Types.PointerTypeReference ptr => $"{EncodeHeritageTypeArgument(ptr.PointeeType)}*",
            tsbindgen.Model.Types.ByRefTypeReference byref => EncodeHeritageTypeArgument(byref.ReferencedType),
            _ => typeRef.ToString() ?? "<opaque>"
        };
    }

    private static string EncodeHeritageNamed(tsbindgen.Model.Types.NamedTypeReference named)
    {
        var baseName = named.Name.Replace("`", "_");

        if (named.TypeArguments.Count == 0)
            return baseName;

        // tsbindgen deterministic encoding for generic heritage arguments:
        //   KeyValuePair_2[[TKey,TValue]]
        var args = string.Join(",", named.TypeArguments.Select(EncodeHeritageTypeArgument));
        return $"{baseName}[[{args}]]";
    }
}

/// <summary>
/// Bindings for a namespace.
/// </summary>
public sealed record NamespaceBindings
{
    public required string Namespace { get; init; }
    public required List<string> ContributingAssemblies { get; init; }
    public required List<TypeBinding> Types { get; init; }

    /// <summary>
    /// Flattened named exports from static classes.
    ///
    /// Sources:
    /// - Tsonic module containers (marked with ModuleContainerAttribute), and
    /// - Explicit static classes listed via --flatten-class.
    ///
    /// Used by Tsonic to bind ESM named imports to CLR static members:
    ///   import { foo } from "@pkg/Namespace.js";
    ///   foo(...); // emits as global::<DeclaringType>.<member>(...)
    /// </summary>
    public Dictionary<string, ExportBinding>? Exports { get; init; }
}

/// <summary>
/// Kind of flattened named export.
///
/// NOTE: This intentionally does not reuse Plan.ExportKind (which describes
/// type/value exports in the facade surface) to avoid name collisions.
/// </summary>
public enum NamedExportKind
{
    Method,
    Property,
    Field
}

/// <summary>
/// A flattened named export - a public static member exposed at the namespace facade top level.
/// </summary>
public sealed record ExportBinding
{
    public required NamedExportKind Kind { get; init; }
    public required string TargetName { get; init; }
    public required string OwnerQualifiedName { get; init; }
    public required string OwnerIdentity { get; init; }
}

/// <summary>
/// Binding for a type.
/// </summary>
public sealed record TypeBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public required string OwnerIdentity { get; init; }
    public required int MetadataToken { get; init; }

    public required string Kind { get; init; }
    public required string Accessibility { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsSealed { get; init; }
    public required bool IsStatic { get; init; }
    public required int Arity { get; init; }

    /// <summary>
    /// Base type for this type (if any). Omitted for root types (System.Object, interfaces, etc.).
    /// </summary>
    public HeritageTypeBinding? BaseType { get; init; }

    /// <summary>
    /// Implemented interfaces for this type (if any).
    /// </summary>
    public List<HeritageTypeBinding>? Interfaces { get; init; }

    /// <summary>
    /// Generic parameter names (when available).
    /// </summary>
    public List<string>? TypeParameters { get; init; }

    // V1: Definitions (what CLR declares on this type)
    public required List<MethodBinding> Methods { get; init; }
    public required List<PropertyBinding> Properties { get; init; }
    public required List<FieldBinding> Fields { get; init; }
    public required List<EventBinding> Events { get; init; }
    public required List<ConstructorBinding> Constructors { get; init; }
}

/// <summary>
/// Binding for a method (definition).
/// </summary>
public sealed record MethodBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string EmitScope { get; init; }
    public required string Provenance { get; init; }
    public required int Arity { get; init; }
    public required int ParameterCount { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsSealed { get; init; }
    public required string Visibility { get; init; }

    // V2: Declaring type information
    public string? OwnerQualifiedName { get; init; }
    public string? OwnerIdentity { get; init; }
    public bool IsExtensionMethod { get; init; }
    public string? SourceInterface { get; init; }
    public EmitSemanticsSpec? EmitSemantics { get; init; }

    /// <summary>
    /// Parameter modifier vector for ref/out/in semantics.
    /// Only included if any parameter has a non-"none" modifier.
    /// Used by Tsonic compiler for ABI enforcement.
    /// </summary>
    public List<ParameterModifierMetadata>? ParameterModifiers { get; init; }
}

/// <summary>
/// Binding for a property (definition).
/// </summary>
public sealed record PropertyBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string EmitScope { get; init; }
    public required string Provenance { get; init; }
    public required bool IsIndexer { get; init; }
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required string Visibility { get; init; }
    public string? SourceInterface { get; init; }

    // V2: Declaring type information
    public string? OwnerQualifiedName { get; init; }
    public string? OwnerIdentity { get; init; }
    public EmitSemanticsSpec? EmitSemantics { get; init; }
}

/// <summary>
/// Binding for a field (definition).
/// </summary>
public sealed record FieldBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public required int MetadataToken { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsReadOnly { get; init; }
    public required bool IsLiteral { get; init; }
    public required string Visibility { get; init; }

    // V2: Declaring type information
    public string? OwnerQualifiedName { get; init; }
    public string? OwnerIdentity { get; init; }
    public EmitSemanticsSpec? EmitSemantics { get; init; }
}

/// <summary>
/// Binding for an event (definition).
/// </summary>
public sealed record EventBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public required int MetadataToken { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required string Visibility { get; init; }

    // V2: Declaring type information
    public string? OwnerQualifiedName { get; init; }
    public string? OwnerIdentity { get; init; }
}

/// <summary>
/// Binding for a constructor (definition).
/// </summary>
public sealed record ConstructorBinding
{
    public required string StableId { get; init; }
    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required int ParameterCount { get; init; }
    public required string Visibility { get; init; }

    // V2: Declaring type information
    public string? OwnerQualifiedName { get; init; }
    public string? OwnerIdentity { get; init; }

    /// <summary>
    /// Parameter modifier vector for ref/out/in semantics.
    /// Only included if any parameter has a non-"none" modifier.
    /// </summary>
    public List<ParameterModifierMetadata>? ParameterModifiers { get; init; }
}

/// <summary>
/// Parameter modifier vector entry for ref/out/in semantics.
/// </summary>
public sealed record ParameterModifierMetadata
{
    public required int Index { get; init; }
    public required ParameterModifier Modifier { get; init; }
}

/// <summary>
/// Heritage type reference stored in bindings.json (base type + interfaces).
/// Uses StableId (Assembly:FullName) as the canonical identity.
///
/// TypeArguments are encoded using tsbindgen's deterministic "TS surface name" encoding:
///   KeyValuePair_2[[TKey,TValue]]
/// This is consumed by Tsonic for instantiation/substitution along inheritance edges.
/// </summary>
public sealed record HeritageTypeBinding
{
    public required string StableId { get; init; }
    public required string TargetName { get; init; }
    public List<string>? TypeArguments { get; init; }
}

// ============================================================================
// V2 EXPOSURE TYPES
// ============================================================================

/// <summary>
/// Target of an exposure - where the actual CLR implementation lives.
/// </summary>
public sealed record ExposureTarget
{
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
    public required int MetadataToken { get; init; }
}

/// <summary>
/// Method exposure - a method visible on the TS surface that forwards to a CLR method.
/// </summary>
public sealed record MethodExposure
{
    public required string TsName { get; init; }
    public required bool IsStatic { get; init; }
    public required string TsSignatureId { get; init; }
    public required ExposureTarget Target { get; init; }
}

/// <summary>
/// Property exposure - a property visible on the TS surface that forwards to a CLR property.
/// </summary>
public sealed record PropertyExposure
{
    public required string TsName { get; init; }
    public required bool IsStatic { get; init; }
    public required string TsSignatureId { get; init; }
    public required ExposureTarget Target { get; init; }
}

/// <summary>
/// Field exposure - a field visible on the TS surface that forwards to a CLR field.
/// </summary>
public sealed record FieldExposure
{
    public required string TsName { get; init; }
    public required bool IsStatic { get; init; }
    public required string TsSignatureId { get; init; }
    public required ExposureTarget Target { get; init; }
}

/// <summary>
/// Event exposure - an event visible on the TS surface that forwards to a CLR event.
/// </summary>
public sealed record EventExposure
{
    public required string TsName { get; init; }
    public required bool IsStatic { get; init; }
    public required string TsSignatureId { get; init; }
    public required ExposureTarget Target { get; init; }
}

/// <summary>
/// Constructor exposure - a constructor visible on the TS surface that forwards to a CLR constructor.
/// </summary>
public sealed record ConstructorExposure
{
    public required bool IsStatic { get; init; }
    public required string TsSignatureId { get; init; }
    public required ExposureTarget Target { get; init; }
}
