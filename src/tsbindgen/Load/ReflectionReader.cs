using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;

namespace tsbindgen.Load;

/// <summary>
/// Reads assemblies via reflection and builds the SymbolGraph.
/// Pure CLR facts - no TypeScript concepts yet.
/// </summary>
public sealed class ReflectionReader
{
    private readonly BuildContext _ctx;
    private readonly TypeReferenceFactory _typeFactory;

    public ReflectionReader(BuildContext ctx)
    {
        _ctx = ctx;
        _typeFactory = new TypeReferenceFactory(ctx);
    }

    /// <summary>
    /// Read assemblies and build the complete SymbolGraph.
    /// </summary>
    public SymbolGraph ReadAssemblies(
        MetadataLoadContext loadContext,
        IReadOnlyList<string> assemblyPaths)
    {
        var loader = new AssemblyLoader(_ctx);
        var assemblies = loader.LoadAssemblies(loadContext, assemblyPaths);

        // Group types by namespace
        var namespaceGroups = new Dictionary<string, List<TypeSymbol>>();
        var sourceAssemblies = new HashSet<string>();

        // Sort assemblies by name for deterministic iteration
        foreach (var assembly in assemblies.OrderBy(a => a.GetName().FullName))
        {
            sourceAssemblies.Add(assembly.Location);
            _ctx.Log("ReflectionReader", $"Reading types from {assembly.GetName().Name}...");

            foreach (var type in assembly.GetTypes())
            {
                // Skip compiler-generated types first
                // Common patterns: <Name>e__FixedBuffer, <>c__DisplayClass, <>d__Iterator, <>f__AnonymousType
                if (IsCompilerGenerated(type.Name))
                {
                    _ctx.Log("ReflectionReader", $"Skipping compiler-generated type: {type.FullName}");
                    continue;
                }

                // Only process public types (correctly handling nested types)
                var accessibility = ComputeAccessibility(type);
                if (accessibility != Accessibility.Public)
                    continue;

                var typeSymbol = ReadType(type);
                var ns = typeSymbol.Namespace;

                if (!namespaceGroups.ContainsKey(ns))
                    namespaceGroups[ns] = new List<TypeSymbol>();

                namespaceGroups[ns].Add(typeSymbol);
            }
        }

        // Build namespace symbols
        var namespaces = new List<NamespaceSymbol>();
        foreach (var (ns, types) in namespaceGroups.OrderBy(kvp => kvp.Key))
        {
            var nsStableId = new TypeStableId
            {
                AssemblyName = "Namespace",
                ClrFullName = ns
            };

            var contributingAssemblies = types
                .Select(t => t.StableId.AssemblyName)
                .Distinct()
                .OrderBy(name => name)
                .ToImmutableHashSet();

            namespaces.Add(new NamespaceSymbol
            {
                Name = ns,
                Types = types.ToImmutableArray(),
                StableId = nsStableId,
                ContributingAssemblies = contributingAssemblies
            });
        }

        return new SymbolGraph
        {
            Namespaces = namespaces.ToImmutableArray(),
            SourceAssemblies = sourceAssemblies.ToImmutableHashSet()
        };
    }

    private TypeSymbol ReadType(Type type)
    {
        var stableId = new TypeStableId
        {
            AssemblyName = _ctx.Intern(type.Assembly.GetName().Name ?? "Unknown"),
            ClrFullName = _ctx.Intern(type.FullName ?? type.Name)
        };

        var kind = DetermineTypeKind(type);
        var accessibility = ComputeAccessibility(type);
        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(_typeFactory.CreateGenericParameterSymbol).ToImmutableArray()
            : ImmutableArray<GenericParameterSymbol>.Empty;

        var baseType = type.BaseType != null ? _typeFactory.Create(type.BaseType) : null;
        var interfaces = type.GetInterfaces().Select(_typeFactory.Create).ToImmutableArray();

        // Read members
        var members = ReadMembers(type);

        // Read nested types (filter out compiler-generated)
        var nestedTypes = type.GetNestedTypes(BindingFlags.Public)
            .Where(t => !IsCompilerGenerated(t.Name))
            .Select(ReadType)
            .ToImmutableArray();

        return new TypeSymbol
        {
            StableId = stableId,
            ClrFullName = _ctx.Intern(type.FullName ?? type.Name),
            ClrName = _ctx.Intern(type.Name),
            Accessibility = accessibility,
            Namespace = _ctx.Intern(type.Namespace ?? ""),
            Kind = kind,
            Arity = type.IsGenericType ? type.GetGenericArguments().Length : 0,
            GenericParameters = genericParams,
            BaseType = baseType,
            Interfaces = interfaces,
            Members = members,
            NestedTypes = nestedTypes,
            IsValueType = type.IsValueType,
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsAbstract && type.IsSealed && !type.IsValueType
        };
    }

    /// <summary>
    /// Compute accessibility for a type, correctly handling nested types.
    /// For nested types, accessibility is the intersection of the declaring type's
    /// accessibility and the nested type's visibility.
    /// </summary>
    private static Accessibility ComputeAccessibility(Type type)
    {
        // Top-level types: simply check IsPublic
        if (!type.IsNested)
        {
            return type.IsPublic ? Accessibility.Public : Accessibility.Internal;
        }

        // Nested types: combine declaring type's accessibility with nested visibility
        // A nested public type is only truly public if its declaring type is also public
        if (type.IsNestedPublic)
        {
            var declaringAccessibility = ComputeAccessibility(type.DeclaringType!);
            return declaringAccessibility == Accessibility.Public
                ? Accessibility.Public
                : Accessibility.Internal;
        }

        // Any other nested visibility (family, assembly, famandassem, famorassem, private)
        // is not public - mark as Internal
        return Accessibility.Internal;
    }

    private TypeKind DetermineTypeKind(Type type)
    {
        if (type.IsEnum) return TypeKind.Enum;
        if (type.IsInterface) return TypeKind.Interface;
        // CRITICAL: Use name-based comparison for delegates because typeof() doesn't work
        // with MetadataLoadContext types (they're from different assembly contexts)
        if (IsDelegate(type))
            return TypeKind.Delegate;
        if (type.IsAbstract && type.IsSealed && !type.IsValueType)
            return TypeKind.StaticNamespace;
        if (type.IsValueType) return TypeKind.Struct;
        return TypeKind.Class;
    }

    /// <summary>
    /// Check if a type is a concrete delegate using name-based comparison.
    /// CRITICAL: typeof(Delegate) comparison fails with MetadataLoadContext types.
    /// NOTE: System.Delegate and System.MulticastDelegate themselves are NOT delegates
    /// for our purposes - they don't have a proper Invoke method signature.
    /// Only concrete delegate types (Func, Action, custom delegates) should be classified
    /// as TypeKind.Delegate.
    /// </summary>
    private static bool IsDelegate(Type type)
    {
        // System.Delegate and System.MulticastDelegate are NOT concrete delegates -
        // they're the abstract base types that don't have an Invoke signature.
        // They should be emitted as classes, not callable function types.
        if (type.FullName == "System.Delegate" || type.FullName == "System.MulticastDelegate")
            return false;

        // Walk the inheritance chain looking for Delegate or MulticastDelegate
        var baseType = type.BaseType;
        while (baseType != null)
        {
            if (baseType.FullName == "System.Delegate" || baseType.FullName == "System.MulticastDelegate")
                return true;
            baseType = baseType.BaseType;
        }
        return false;
    }

    private TypeMembers ReadMembers(Type type)
    {
        var methods = new List<MethodSymbol>();
        var properties = new List<PropertySymbol>();
        var fields = new List<FieldSymbol>();
        var events = new List<EventSymbol>();
        var constructors = new List<ConstructorSymbol>();

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Read methods
        var seenMethods = new HashSet<string>();
        foreach (var method in type.GetMethods(publicInstance | publicStatic))
        {
            // Skip property/event accessors and special methods
            if (method.IsSpecialName) continue;

            // DEBUG: Track method tokens to detect duplicates from reflection
            var methodKey = $"{method.Name}|{method.MetadataToken}";
            if (seenMethods.Contains(methodKey))
            {
                _ctx.Log("ReflectionReader", $"WARNING: GetMethods returned duplicate method: {type.FullName}::{method.Name} (token: {method.MetadataToken})");
                continue; // Skip duplicate
            }
            seenMethods.Add(methodKey);

            var methodSymbol = ReadMethod(method, type);

            // DEBUG: Log if we're about to add a duplicate StableId
            if (methods.Any(m => m.StableId.Equals(methodSymbol.StableId)))
            {
                _ctx.Log("ReflectionReader", $"ERROR: About to add duplicate StableId: {methodSymbol.StableId}");
                _ctx.Log("ReflectionReader", $"  Method name: {method.Name}, MetadataToken: {method.MetadataToken}");
                _ctx.Log("ReflectionReader", $"  Type: {type.FullName}");
                continue; // Skip to prevent duplicate
            }

            methods.Add(methodSymbol);
        }

        // Read properties
        foreach (var property in type.GetProperties(publicInstance | publicStatic))
        {
            properties.Add(ReadProperty(property, type));
        }

        // Read fields
        foreach (var field in type.GetFields(publicInstance | publicStatic))
        {
            fields.Add(ReadField(field, type));
        }

        // Read events
        foreach (var evt in type.GetEvents(publicInstance | publicStatic))
        {
            events.Add(ReadEvent(evt, type));
        }

        // Read constructors
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            constructors.Add(ReadConstructor(ctor, type));
        }

        return new TypeMembers
        {
            Methods = methods.ToImmutableArray(),
            Properties = properties.ToImmutableArray(),
            Fields = fields.ToImmutableArray(),
            Events = events.ToImmutableArray(),
            Constructors = constructors.ToImmutableArray()
        };
    }

    private MethodSymbol ReadMethod(MethodInfo method, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.Collections.ICollection.SyncRoot" vs "SyncRoot"
        var clrName = method.Name;
        var memberName = method.Name;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.Collections.ICollection.SyncRoot"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = CreateMethodSignature(method),
            MetadataToken = method.MetadataToken
        };

        var parameters = method.GetParameters().Select(p => ReadParameter(p, method, declaringType)).ToImmutableArray();
        var genericParams = method.IsGenericMethod
            ? method.GetGenericArguments().Select(_typeFactory.CreateGenericParameterSymbol).ToImmutableArray()
            : ImmutableArray<GenericParameterSymbol>.Empty;

        // Detect extension methods (static method in static class with ExtensionAttribute on method)
        bool isExtensionMethod = false;
        TypeReference? extensionTarget = null;
        if (method.IsStatic && parameters.Length > 0)
        {
            // Check for ExtensionAttribute on the method itself (not the parameter)
            var hasExtensionAttr = method.CustomAttributes.Any(attr =>
                attr.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");

            if (hasExtensionAttr)
            {
                isExtensionMethod = true;
                extensionTarget = parameters[0].Type;  // The type of the first 'this' parameter
            }
        }

        // Read return type with NRT nullability
        // Use the return-specific method which includes method-level NullableAttribute fallback
        var returnType = CreateTypeWithNullabilityForReturnType(method.ReturnType, method.ReturnParameter, method, declaringType);

        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            ReturnType = returnType,
            Parameters = parameters,
            GenericParameters = genericParams,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = IsMethodOverride(method),
            IsSealed = method.IsFinal,
            Visibility = GetVisibility(method),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface,  // All reflected members start on class surface
            IsExtensionMethod = isExtensionMethod,
            ExtensionTarget = extensionTarget
        };
    }

    private PropertySymbol ReadProperty(PropertyInfo property, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.Collections.ICollection.SyncRoot" vs "SyncRoot"
        var clrName = property.Name;
        var memberName = property.Name;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.Collections.ICollection.SyncRoot"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = CreatePropertySignature(property),
            MetadataToken = property.MetadataToken
        };

        var getter = property.GetGetMethod();
        var setter = property.GetSetMethod();
        // For indexer parameters, use the accessor method as the declaring member for NRT context
        var accessor = getter ?? setter;
        var indexParams = accessor != null
            ? property.GetIndexParameters().Select(p => ReadParameter(p, accessor, declaringType)).ToImmutableArray()
            : ImmutableArray<ParameterSymbol>.Empty;

        // Read property type with NRT nullability
        var propertyType = CreateTypeWithNullabilityFromProperty(property, declaringType);

        return new PropertySymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            PropertyType = propertyType,
            IndexParameters = indexParams,
            HasGetter = getter != null,
            HasSetter = setter != null,
            IsStatic = (getter ?? setter)?.IsStatic ?? false,
            IsVirtual = (getter ?? setter)?.IsVirtual ?? false,
            IsOverride = getter != null && IsMethodOverride(getter),
            IsAbstract = (getter ?? setter)?.IsAbstract ?? false,
            Visibility = GetPropertyVisibility(property),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private FieldSymbol ReadField(FieldInfo field, Type declaringType)
    {
        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(field.Name),
            CanonicalSignature = field.FieldType.FullName ?? field.FieldType.Name,
            MetadataToken = field.MetadataToken
        };

        // Read field type with NRT nullability
        var fieldType = CreateTypeWithNullabilityFromField(field, declaringType);

        return new FieldSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(field.Name),
            FieldType = fieldType,
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsInitOnly,
            IsConst = field.IsLiteral,
            ConstValue = field.IsLiteral ? field.GetRawConstantValue() : null,
            Visibility = GetFieldVisibility(field),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private EventSymbol ReadEvent(EventInfo evt, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.ComponentModel.INotifyPropertyChanged.PropertyChanged" vs "PropertyChanged"
        var clrName = evt.Name!;
        var memberName = evt.Name!;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.ComponentModel.INotifyPropertyChanged.PropertyChanged"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = evt.EventHandlerType?.FullName ?? "Unknown",
            MetadataToken = evt.MetadataToken
        };

        var addMethod = evt.GetAddMethod();

        return new EventSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            EventHandlerType = _typeFactory.Create(evt.EventHandlerType!),
            IsStatic = addMethod?.IsStatic ?? false,
            IsVirtual = addMethod?.IsVirtual ?? false,
            IsOverride = addMethod != null && IsMethodOverride(addMethod),
            Visibility = GetEventVisibility(evt),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private ConstructorSymbol ReadConstructor(ConstructorInfo ctor, Type declaringType)
    {
        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = ".ctor",
            CanonicalSignature = CreateConstructorSignature(ctor),
            MetadataToken = ctor.MetadataToken
        };

        return new ConstructorSymbol
        {
            StableId = stableId,
            Parameters = ctor.GetParameters().Select(p => ReadParameter(p, ctor, declaringType)).ToImmutableArray(),
            IsStatic = ctor.IsStatic,
            Visibility = GetConstructorVisibility(ctor)
        };
    }

    private ParameterSymbol ReadParameter(ParameterInfo param, MethodBase declaringMethod, Type declaringType)
    {
        // Sanitize parameter name for TypeScript reserved words
        var paramName = param.Name ?? $"arg{param.Position}";
        var sanitizedName = TypeScriptReservedWords.SanitizeParameterName(paramName);

        // Note: param.IsOut can be true for non-byref parameters (marshalling hint via [Out] attribute)
        // We only track actual C# `out` parameters (which require both IsOut AND IsByRef)
        var isOut = param.IsOut && param.ParameterType.IsByRef;

        // COMPILER-GRADE: 'in' detection with modreq as ground truth when present
        // The CLR marks readonly-byref ('in') with modreq(IsReadOnlyAttribute).
        // ParameterInfo.IsIn is a metadata flag that can also appear in interop scenarios.
        //
        // Detection strategy:
        // 1. If modreq(IsReadOnlyAttribute) IS FOUND → definitive 'in' (ground truth)
        // 2. If modreq is empty or throws → inconclusive (MetadataLoadContext returns empty
        //    arrays because it can't resolve modifier types) → fall back to IsIn flag
        //
        // This means: non-empty modreq is authoritative; empty/failed modreq defers to IsIn.
        var isIn = false;
        if (param.ParameterType.IsByRef && !isOut)
        {
            var modreqResult = TryGetIsReadOnlyModifier(param.ParameterType);
            if (modreqResult == ModreqResult.Found)
            {
                // Ground truth: modreq found, definitely 'in'
                isIn = true;
            }
            else
            {
                // Inconclusive (empty or exception): fall back to IsIn flag
                // Empty modreq in MetadataLoadContext doesn't mean "no modreq"
                isIn = param.IsIn;
            }
        }

        // Compute IsRef: byref but not out or in
        var isRef = param.ParameterType.IsByRef && !isOut && !isIn;

        // CRITICAL: Validate mutual exclusivity (CLR constraint)
        // At most one of ref/out/in can be true for any parameter
        var modifierCount = (isRef ? 1 : 0) + (isOut ? 1 : 0) + (isIn ? 1 : 0);
        if (modifierCount > 1)
        {
            throw new InvalidOperationException(
                $"Parameter '{paramName}' has multiple modifiers (IsRef={isRef}, IsOut={isOut}, IsIn={isIn}). " +
                "Only one of ref/out/in is allowed per CLR specification.");
        }

        // Note: IsByRef implications are guaranteed by construction above
        // (isOut/isIn/isRef all include IsByRef in their computation)

        // Check if this is a params array parameter
        var isParams = param.GetCustomAttributesData()
            .Any(attr => attr.AttributeType.Name == "ParamArrayAttribute");

        // Read parameter type with NRT nullability
        // For params arrays, use the original Create (no nullability) because:
        // 1. TypeScript rest parameters (...args) can't be undefined
        // 2. The array is constructed by the caller, not passed as null
        var paramType = isParams
            ? _typeFactory.Create(param.ParameterType)
            : CreateTypeWithNullabilityForParameter(param.ParameterType, param, declaringMethod, declaringType);

        return new ParameterSymbol
        {
            Name = _ctx.Intern(sanitizedName),
            Type = paramType,
            IsRef = isRef,
            IsOut = isOut,
            IsIn = isIn,
            IsParams = isParams,
            HasDefaultValue = param.HasDefaultValue,
            DefaultValue = param.HasDefaultValue ? param.RawDefaultValue : null
        };
    }

    /// <summary>
    /// Result of attempting to read IsReadOnlyAttribute modreq.
    /// </summary>
    /// <remarks>
    /// COMPILER-GRADE SEMANTICS:
    /// - Found: modreq(IsReadOnlyAttribute) present → definitive 'in' (ground truth)
    /// - NotFound: modreq array empty or doesn't contain it → treated as INCONCLUSIVE
    /// - Error: exception thrown → inconclusive
    ///
    /// NOTE on NotFound: In normal reflection, empty modreq IS informative (means "not readonly-byref").
    /// However, in MetadataLoadContext, empty modreq is often caused by type resolution failures
    /// (modifier types can't be resolved), NOT by the absence of modifiers.
    ///
    /// Since tsbindgen uses MetadataLoadContext for BCL assemblies, we conservatively treat
    /// NotFound as "inconclusive" and fall back to ParameterInfo.IsIn. This is safe because:
    /// 1. In MLC: NotFound may be false negative → IsIn provides correct answer
    /// 2. In normal reflection: NotFound is true negative, but IsIn also returns false → same result
    ///
    /// If a future version needs to distinguish "readable empty" (definitive not-in) from
    /// "MLC limitation" (inconclusive), we would need to detect the reflection context.
    /// </remarks>
    private enum ModreqResult
    {
        /// <summary>modreq(IsReadOnlyAttribute) was found - definitive 'in'</summary>
        Found,
        /// <summary>modreq array was empty or didn't contain IsReadOnlyAttribute - inconclusive (see remarks)</summary>
        NotFound,
        /// <summary>Exception thrown reading modreq - inconclusive</summary>
        Error
    }

    /// <summary>
    /// Try to check if a type has the IsReadOnlyAttribute required modifier.
    /// This is how C# marks 'in' parameters in metadata (modreq).
    ///
    /// Returns:
    /// - Found: modreq(IsReadOnlyAttribute) is present → definitive 'in'
    /// - NotFound: modreq array empty or doesn't contain it → inconclusive (could be MLC issue)
    /// - Error: exception thrown → inconclusive
    ///
    /// Note: MetadataLoadContext returns empty arrays (not exceptions) because modifier types
    /// can't be resolved. So NotFound is NOT the same as "definitely not 'in'".
    /// </summary>
    private static ModreqResult TryGetIsReadOnlyModifier(Type type)
    {
        try
        {
            var modifiers = type.GetRequiredCustomModifiers();
            var hasModreq = modifiers.Any(m =>
                m.Name == "IsReadOnlyAttribute" ||
                m.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");
            return hasModreq ? ModreqResult.Found : ModreqResult.NotFound;
        }
        catch
        {
            return ModreqResult.Error;
        }
    }

    private string CreateMethodSignature(MethodInfo method)
    {
        var paramTypes = method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        var returnType = method.ReturnType.FullName ?? method.ReturnType.Name;
        return _ctx.CanonicalizeMethod(method.Name, paramTypes, returnType);
    }

    private string CreatePropertySignature(PropertyInfo property)
    {
        var indexTypes = property.GetIndexParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        var propType = property.PropertyType.FullName ?? property.PropertyType.Name;
        return _ctx.CanonicalizeProperty(property.Name, indexTypes, propType);
    }

    private string CreateConstructorSignature(ConstructorInfo ctor)
    {
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        return _ctx.CanonicalizeMethod(".ctor", paramTypes, "void");
    }

    private Visibility GetVisibility(MethodInfo method)
    {
        if (method.IsPublic) return Visibility.Public;
        if (method.IsFamily) return Visibility.Protected;
        if (method.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (method.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (method.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    private Visibility GetPropertyVisibility(PropertyInfo property)
    {
        var getter = property.GetGetMethod(true);
        var setter = property.GetSetMethod(true);
        var method = getter ?? setter;
        return method != null ? GetVisibility(method) : Visibility.Private;
    }

    private Visibility GetFieldVisibility(FieldInfo field)
    {
        if (field.IsPublic) return Visibility.Public;
        if (field.IsFamily) return Visibility.Protected;
        if (field.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (field.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (field.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    private Visibility GetEventVisibility(EventInfo evt)
    {
        var addMethod = evt.GetAddMethod(true);
        return addMethod != null ? GetVisibility(addMethod) : Visibility.Private;
    }

    /// <summary>
    /// Check if a method is an override (vs new virtual or original virtual).
    /// Uses MethodAttributes flags which work with MetadataLoadContext.
    /// Overrides are virtual and do NOT have NewSlot set (they reuse vtable slot).
    /// </summary>
    private static bool IsMethodOverride(MethodInfo method)
    {
        return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
    }

    private Visibility GetConstructorVisibility(ConstructorInfo ctor)
    {
        if (ctor.IsPublic) return Visibility.Public;
        if (ctor.IsFamily) return Visibility.Protected;
        if (ctor.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (ctor.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (ctor.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    /// <summary>
    /// Check if a type name indicates compiler-generated code.
    /// Compiler-generated types have unspeakable names containing < or >
    /// Examples: "<Module>", "<PrivateImplementationDetails>", "<Name>e__FixedBuffer", "<>c__DisplayClass"
    /// </summary>
    private static bool IsCompilerGenerated(string typeName)
    {
        return typeName.Contains('<') || typeName.Contains('>');
    }

    /// <summary>
    /// Create a TypeReference with NRT nullability information from a parameter.
    /// Parameters do NOT get the NullableAttribute fallback - only return types do.
    /// </summary>
    private TypeReference CreateTypeWithNullabilityForParameter(Type type, ParameterInfo param, MethodBase declaringMethod, Type declaringType)
    {
        // Get nullability metadata from the parameter, using method-level context for fallback
        // This ensures parameters without explicit NullableAttribute inherit from the method's
        // NullableContextAttribute, not the type's (which was the bug)
        // Note: nullableAttributeFallbackAttributes is null for parameters
        var (nullabilityFlags, singleNullability) = GetNullabilityMetadata(
            param.CustomAttributes,
            declaringMethod.CustomAttributes,
            nullableAttributeFallbackAttributes: null,  // Parameters must NOT look at method-level NullableAttribute
            declaringType);
        return _typeFactory.CreateWithNullability(type, nullabilityFlags, singleNullability);
    }

    /// <summary>
    /// Create a TypeReference with NRT nullability information from a return type.
    /// Return types get the method's NullableAttribute as fallback because Roslyn can encode
    /// return nullability on the method metadata itself, not just ReturnParameter.
    /// </summary>
    private TypeReference CreateTypeWithNullabilityForReturnType(Type type, ParameterInfo returnParam, MethodBase declaringMethod, Type declaringType)
    {
        // For return types, pass the method's attributes as fallback for NullableAttribute
        // This is critical for methods like MalformedLineException.ToString() where the return
        // nullability is encoded on the method, not the return parameter.
        var (nullabilityFlags, singleNullability) = GetNullabilityMetadata(
            returnParam.CustomAttributes,
            declaringMethod.CustomAttributes,
            nullableAttributeFallbackAttributes: declaringMethod.CustomAttributes,  // Return types CAN look at method-level NullableAttribute
            declaringType);
        return _typeFactory.CreateWithNullability(type, nullabilityFlags, singleNullability);
    }

    /// <summary>
    /// Create a TypeReference with NRT nullability information from a property.
    /// For properties, the property's CustomAttributes contain both the NullableAttribute (target)
    /// and potentially a NullableContextAttribute (member context).
    /// </summary>
    private TypeReference CreateTypeWithNullabilityFromProperty(PropertyInfo property, Type declaringType)
    {
        // Property's CustomAttributes serve as both target and member context
        // No fallback needed for properties - NullableAttribute is directly on the property
        var (nullabilityFlags, singleNullability) = GetNullabilityMetadata(
            property.CustomAttributes,
            property.CustomAttributes,  // Property itself provides the context
            nullableAttributeFallbackAttributes: null,
            declaringType);
        return _typeFactory.CreateWithNullability(property.PropertyType, nullabilityFlags, singleNullability);
    }

    /// <summary>
    /// Create a TypeReference with NRT nullability information from a field.
    /// For fields, the field's CustomAttributes contain both the NullableAttribute (target)
    /// and potentially a NullableContextAttribute (member context).
    /// </summary>
    private TypeReference CreateTypeWithNullabilityFromField(FieldInfo field, Type declaringType)
    {
        // Field's CustomAttributes serve as both target and member context
        // No fallback needed for fields - NullableAttribute is directly on the field
        var (nullabilityFlags, singleNullability) = GetNullabilityMetadata(
            field.CustomAttributes,
            field.CustomAttributes,  // Field itself provides the context
            nullableAttributeFallbackAttributes: null,
            declaringType);
        return _typeFactory.CreateWithNullability(field.FieldType, nullabilityFlags, singleNullability);
    }

    /// <summary>
    /// Extract nullability metadata from custom attributes.
    /// Returns a byte array if NullableAttribute contains an array, otherwise returns single nullability state.
    ///
    /// The NRT context fallback chain is (per Roslyn spec):
    /// 1. NullableAttribute on the target (parameter/return/field/property)
    /// 1b. NullableAttribute on the declaring member (method) - ONLY for return types
    /// 2. NullableContextAttribute on the declaring member (method/ctor) - for parameters
    /// 3. NullableContextAttribute on the member (property/field) - for property/field types
    /// 4. NullableContextAttribute on the type/enclosing types/module/assembly
    /// </summary>
    private static (byte[]? flags, NrtState singleValue) GetNullabilityMetadata(
        IEnumerable<CustomAttributeData> targetAttributes,
        IEnumerable<CustomAttributeData>? memberContextAttributes,
        IEnumerable<CustomAttributeData>? nullableAttributeFallbackAttributes,
        Type declaringType)
    {
        // 1. Look for NullableAttribute on the target itself (parameter/return/property/field)
        var nullableAttr = FindNullableAttribute(targetAttributes);
        if (nullableAttr != null)
        {
            return ParseNullableAttribute(nullableAttr);
        }

        // 1b. NullableAttribute fallback (ONLY for return types)
        // Roslyn can encode return nullability on the method metadata itself, not just ReturnParameter.
        // This is critical for methods like MalformedLineException.ToString() where the return
        // nullability is encoded on the method, not the return parameter.
        if (nullableAttributeFallbackAttributes != null)
        {
            var fallbackNullableAttr = FindNullableAttribute(nullableAttributeFallbackAttributes);
            if (fallbackNullableAttr != null)
            {
                return ParseNullableAttribute(fallbackNullableAttr);
            }
        }

        // 2. Check NullableContextAttribute on the declaring member (method/ctor/property/field)
        // This is the key fix: parameters inherit from METHOD context, not TYPE context
        if (memberContextAttributes != null)
        {
            var memberContext = GetNullableContextFromAttributes(memberContextAttributes);
            if (memberContext.HasValue)
            {
                return (null, memberContext.Value);
            }
        }

        // 3. Fall back to NullableContextAttribute from declaring type/module/assembly
        var contextDefault = GetContextDefault(declaringType);
        return (null, contextDefault);
    }

    /// <summary>
    /// Find NullableAttribute in a collection of custom attributes.
    /// </summary>
    private static CustomAttributeData? FindNullableAttribute(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute")
            {
                return attr;
            }
        }
        return null;
    }

    /// <summary>
    /// Parse a NullableAttribute into flags or single value.
    /// </summary>
    private static (byte[]? flags, NrtState singleValue) ParseNullableAttribute(CustomAttributeData nullableAttr)
    {
        if (nullableAttr.ConstructorArguments.Count > 0)
        {
            var arg = nullableAttr.ConstructorArguments[0];

            // Single byte: NullableAttribute(byte)
            if (arg.Value is byte singleByte)
            {
                return (null, (NrtState)singleByte);
            }

            // Byte array: NullableAttribute(byte[])
            if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> byteArray)
            {
                var bytes = byteArray.Select(a => (byte)a.Value!).ToArray();
                return (bytes, NrtState.Oblivious);
            }
        }
        return (null, NrtState.Oblivious);
    }

    /// <summary>
    /// Get the default nullability from NullableContextAttribute.
    /// Searches: type → enclosing types → module → assembly.
    /// </summary>
    private static NrtState GetContextDefault(Type? type)
    {
        if (type == null) return NrtState.Oblivious;

        // 1. Check this type
        var typeContext = GetNullableContextFromAttributes(type.CustomAttributes);
        if (typeContext.HasValue) return typeContext.Value;

        // 2. Check enclosing types (for nested types)
        if (type.DeclaringType != null)
        {
            var declaringContext = GetContextDefault(type.DeclaringType);
            if (declaringContext != NrtState.Oblivious) return declaringContext;
        }

        // 3. Check module-level
        try
        {
            var moduleContext = GetNullableContextFromAttributes(type.Module.GetCustomAttributesData());
            if (moduleContext.HasValue) return moduleContext.Value;
        }
        catch
        {
            // Module attributes may not be accessible in MetadataLoadContext - ignore
        }

        // 4. Check assembly-level
        try
        {
            var asmContext = GetNullableContextFromAttributes(type.Assembly.GetCustomAttributesData());
            if (asmContext.HasValue) return asmContext.Value;
        }
        catch
        {
            // Assembly attributes may not be accessible in MetadataLoadContext - ignore
        }

        return NrtState.Oblivious;
    }

    /// <summary>
    /// Extract NullableContextAttribute value from a collection of custom attributes.
    /// </summary>
    private static NrtState? GetNullableContextFromAttributes(IEnumerable<CustomAttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute")
            {
                if (attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is byte b)
                    return (NrtState)b;
            }
        }
        return null;
    }
}
