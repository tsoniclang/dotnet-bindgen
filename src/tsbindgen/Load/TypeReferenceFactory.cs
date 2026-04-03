using System.Collections.Immutable;
using System.Reflection;
using tsbindgen.Model.Types;
using tsbindgen.Model.Symbols;

namespace tsbindgen.Load;

/// <summary>
/// Converts System.Type to our TypeReference model.
/// Handles all type constructs: named, generic, array, pointer, byref, nested.
/// Uses memoization with cycle detection to prevent stack overflow on recursive constraints.
/// </summary>
public sealed class TypeReferenceFactory
{
    private readonly BuildContext _ctx;
    private readonly Dictionary<Type, TypeReference> _cache = new();
    private readonly HashSet<Type> _inProgress = new();

    public TypeReferenceFactory(BuildContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Convert a System.Type to TypeReference.
    /// Memoized with cycle detection to prevent infinite recursion.
    /// </summary>
    public TypeReference Create(Type type)
    {
        // Check cache first
        if (_cache.TryGetValue(type, out var cached))
            return cached;

        // Detect cycle - return placeholder to break recursion
        if (_inProgress.Contains(type))
        {
            return new PlaceholderTypeReference
            {
                DebugName = _ctx.Intern(type.FullName ?? type.Name)
            };
        }

        // Mark as in-progress
        _inProgress.Add(type);
        try
        {
            var result = CreateInternal(type);
            _cache[type] = result;
            return result;
        }
        finally
        {
            _inProgress.Remove(type);
        }
    }

    private TypeReference CreateInternal(Type type)
    {
        // Handle special cases first
        if (type.IsByRef)
        {
            return new ByRefTypeReference
            {
                ReferencedType = Create(type.GetElementType()!)
            };
        }

        if (type.IsPointer)
        {
            var depth = 1;
            var elementType = type.GetElementType()!;
            while (elementType.IsPointer)
            {
                depth++;
                elementType = elementType.GetElementType()!;
            }

            return new PointerTypeReference
            {
                PointeeType = Create(elementType),
                Depth = depth
            };
        }

        if (type.IsFunctionPointer)
        {
            return new FunctionPointerTypeReference
            {
                ReturnType = Create(type.GetFunctionPointerReturnType()),
                ParameterTypes = type
                    .GetFunctionPointerParameterTypes()
                    .Select(Create)
                    .ToArray(),
                CallingConventionTypes = type
                    .GetFunctionPointerCallingConventions()
                    .Select(Create)
                    .ToArray()
            };
        }

        if (type.IsArray)
        {
            return new ArrayTypeReference
            {
                ElementType = Create(type.GetElementType()!),
                Rank = type.GetArrayRank()
            };
        }

        if (type.IsGenericParameter)
        {
            return CreateGenericParameter(type);
        }

        // Named type (class, struct, interface, enum, delegate)
        return CreateNamed(type);
    }

    private TypeReference CreateNamed(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "Unknown";

        // CRITICAL: For constructed generic types (e.g., IEquatable<StandardFormat>),
        // use the open generic form's FullName, NOT the constructed form.
        // Constructed form includes assembly-qualified type args which breaks StableId lookup.
        // Example: "System.IEquatable`1" not "System.IEquatable`1[[System.Buffers.StandardFormat, ...]]"
        var fullName = type.IsGenericType && type.IsConstructedGenericType
            ? type.GetGenericTypeDefinition().FullName ?? type.Name
            : type.FullName ?? type.Name;

        var namespaceName = type.Namespace ?? "";
        var name = type.Name;

        // HARDENING: Guarantee Name is never empty
        // If type.Name is null/empty, derive from FullName (last segment after '.' or '+')
        if (string.IsNullOrWhiteSpace(name))
        {
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                // Extract last segment: "System.Foo.Bar+Nested" -> "Nested"
                var lastDot = fullName.LastIndexOf('.');
                var lastPlus = fullName.LastIndexOf('+');
                var lastSeparator = Math.Max(lastDot, lastPlus);

                name = lastSeparator >= 0
                    ? fullName.Substring(lastSeparator + 1)
                    : fullName;
            }
            else
            {
                // Last resort: use a synthetic name
                name = "UnknownType";
                _ctx.Log("TypeReferenceFactory",
                    $"WARNING: Type with empty Name and FullName from assembly {assemblyName}. Using synthetic name.");
            }
        }

        // Handle generic types
        var arity = 0;
        var typeArgs = new List<TypeReference>();

        if (type.IsGenericType)
        {
            arity = type.GetGenericArguments().Length;

            // For constructed generic types, get type arguments
            if (type.IsConstructedGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    typeArgs.Add(Create(arg));
                }
            }
        }

        // HARDENING: Stamp interface StableId at load time for interfaces
        // Format: AssemblyName:FullName (same as ScopeFactory.GetInterfaceStableId)
        // This eliminates repeated computation and graph lookups
        string? interfaceStableId = null;
        if (type.IsInterface)
        {
            interfaceStableId = _ctx.Intern($"{assemblyName}:{fullName}");
        }

        return new NamedTypeReference
        {
            AssemblyName = _ctx.Intern(assemblyName),
            FullName = _ctx.Intern(fullName),
            Namespace = _ctx.Intern(namespaceName),
            Name = _ctx.Intern(name),
            Arity = arity,
            TypeArguments = typeArgs,
            IsValueType = type.IsValueType,
            InterfaceStableId = interfaceStableId
        };
    }

    private TypeReference CreateGenericParameter(Type type)
    {
        var declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType;
        var declaringName = declaringType?.FullName ?? "Unknown";

        var id = new GenericParameterId
        {
            DeclaringTypeName = _ctx.Intern(declaringName),
            Position = type.GenericParameterPosition,
            IsMethodParameter = type.DeclaringMethod != null
        };

        // NOTE: Constraints are NOT resolved here to avoid infinite recursion
        // on recursive constraints like IComparable<T> where T : IComparable<T>.
        // ConstraintCloser will resolve constraints during Shape phase.

        return new GenericParameterReference
        {
            Id = id,
            Name = _ctx.Intern(type.Name),
            Position = type.GenericParameterPosition,
            Constraints = new List<TypeReference>() // Empty - filled by ConstraintCloser
        };
    }

    /// <summary>
    /// Create a GenericParameterSymbol from a Type.
    /// Stores variance and special constraints; ConstraintCloser resolves type constraints later.
    /// </summary>
    public GenericParameterSymbol CreateGenericParameterSymbol(Type type)
    {
        if (!type.IsGenericParameter)
            throw new ArgumentException("Type must be a generic parameter", nameof(type));

        var declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType;
        var declaringName = declaringType?.FullName ?? "Unknown";

        var id = new GenericParameterId
        {
            DeclaringTypeName = _ctx.Intern(declaringName),
            Position = type.GenericParameterPosition,
            IsMethodParameter = type.DeclaringMethod != null
        };

        // NOTE: Constraint types are NOT resolved here to avoid infinite recursion.
        // ConstraintCloser will resolve them during Shape phase.
        // We only store the raw System.Type[] for later resolution.

        var variance = Variance.None;
        var attrs = type.GenericParameterAttributes;
        if ((attrs & GenericParameterAttributes.Covariant) != 0)
            variance = Variance.Covariant;
        else if ((attrs & GenericParameterAttributes.Contravariant) != 0)
            variance = Variance.Contravariant;

        var specialConstraints = GenericParameterConstraints.None;
        if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.ReferenceType;
        if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.ValueType;
        if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.DefaultConstructor;

        return new GenericParameterSymbol
        {
            Id = id,
            Name = _ctx.Intern(type.Name),
            Position = type.GenericParameterPosition,
            Constraints = ImmutableArray<TypeReference>.Empty, // Empty - ConstraintCloser fills this
            RawConstraintTypes = type.GetGenericParameterConstraints(), // Raw for ConstraintCloser
            Variance = variance,
            SpecialConstraints = specialConstraints
        };
    }

    /// <summary>
    /// Clear the cache (for testing).
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Create a TypeReference with nullability information from NRT metadata.
    /// This is the entry point for creating type references with proper nullable handling.
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <param name="nullabilityFlags">Nullable attribute byte array (or null if using single-byte or context default).</param>
    /// <param name="singleNullability">Single nullability value when not using byte array.</param>
    /// <returns>TypeReference with Nullability set appropriately.</returns>
    public TypeReference CreateWithNullability(Type type, byte[]? nullabilityFlags, NrtState singleNullability)
    {
        // FIX 2: Validate byte[] length against expected position count
        // If misaligned, fall back to single value to prevent wrong nullability
        if (nullabilityFlags != null && nullabilityFlags.Length > 1)
        {
            var expectedCount = CountTypePositions(type);
            if (nullabilityFlags.Length != expectedCount)
            {
                // Misalignment detected - Roslyn's encoding doesn't match our traversal
                // Fall back to single value (first byte or context default)
                _ctx.Log("NRT", $"Position mismatch for {type.FullName}: " +
                    $"expected {expectedCount}, got {nullabilityFlags.Length}. Using first byte.");
                singleNullability = (NrtState)nullabilityFlags[0];
                nullabilityFlags = null;
            }
        }

        var position = 0;
        return CreateWithNullabilityInternal(type, nullabilityFlags, singleNullability, ref position);
    }

    /// <summary>
    /// Count the number of positions in NRT metadata for a type tree.
    /// Must use same traversal logic as CreateWithNullabilityInternal.
    /// </summary>
    private static int CountTypePositions(Type type)
    {
        // ByRef and Pointer don't consume positions
        if (type.IsByRef || type.IsPointer)
        {
            return CountTypePositions(type.GetElementType()!);
        }

        // Value types don't consume positions
        if (type.IsValueType)
        {
            return 0;
        }

        // This type consumes 1 position
        var count = 1;

        // Arrays: also count element type
        if (type.IsArray)
        {
            count += CountTypePositions(type.GetElementType()!);
            return count;
        }

        // Generic types: count each type argument
        if (type.IsGenericType && type.IsConstructedGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                count += CountTypePositions(arg);
            }
        }

        return count;
    }

    /// <summary>
    /// Internal method that tracks position through pre-order traversal of the type tree.
    /// </summary>
    private TypeReference CreateWithNullabilityInternal(Type type, byte[]? nullabilityFlags, NrtState singleNullability, ref int position)
    {
        // Handle ByRef types first - delegate to base Create and don't consume position
        // ByRef types (ref/out/in) are not tracked in NRT metadata
        if (type.IsByRef)
        {
            return new ByRefTypeReference
            {
                ReferencedType = CreateWithNullabilityInternal(type.GetElementType()!, nullabilityFlags, singleNullability, ref position)
            };
        }

        // Handle pointers - not tracked in NRT metadata
        if (type.IsPointer)
        {
            var depth = 1;
            var elementType = type.GetElementType()!;
            while (elementType.IsPointer)
            {
                depth++;
                elementType = elementType.GetElementType()!;
            }

            return new PointerTypeReference
            {
                PointeeType = CreateWithNullabilityInternal(elementType, nullabilityFlags, singleNullability, ref position),
                Depth = depth
            };
        }

        if (type.IsFunctionPointer)
        {
            var parameterTypes = new List<TypeReference>();
            foreach (var parameterType in type.GetFunctionPointerParameterTypes())
            {
                parameterTypes.Add(
                    CreateWithNullabilityInternal(
                        parameterType,
                        nullabilityFlags,
                        singleNullability,
                        ref position));
            }

            return new FunctionPointerTypeReference
            {
                ReturnType = CreateWithNullabilityInternal(
                    type.GetFunctionPointerReturnType(),
                    nullabilityFlags,
                    singleNullability,
                    ref position),
                ParameterTypes = parameterTypes,
                CallingConventionTypes = type
                    .GetFunctionPointerCallingConventions()
                    .Select(Create)
                    .ToArray()
            };
        }

        // Value types are not tracked in NRT metadata (except Nullable<T>)
        if (type.IsValueType)
        {
            return Create(type);
        }

        // Get nullability for this position
        NrtState nullability;
        if (nullabilityFlags != null && position < nullabilityFlags.Length)
        {
            nullability = (NrtState)nullabilityFlags[position];
        }
        else
        {
            nullability = singleNullability;
        }

        // Consume position for this type
        position++;

        // Handle arrays specially
        if (type.IsArray)
        {
            var elementType = CreateWithNullabilityInternal(type.GetElementType()!, nullabilityFlags, singleNullability, ref position);
            return new ArrayTypeReference
            {
                ElementType = elementType,
                Rank = type.GetArrayRank(),
                Nullability = nullability
            };
        }

        // Handle generic parameters
        if (type.IsGenericParameter)
        {
            var baseRef = CreateGenericParameter(type);
            if (baseRef is GenericParameterReference gpRef)
            {
                return gpRef with { Nullability = nullability };
            }
            return baseRef;
        }

        // Handle named types (potentially with generic arguments)
        var namedRef = CreateNamedWithNullability(type, nullabilityFlags, singleNullability, ref position, nullability);
        return namedRef;
    }

    /// <summary>
    /// Create a NamedTypeReference with nullability, handling generic type arguments.
    /// </summary>
    private NamedTypeReference CreateNamedWithNullability(Type type, byte[]? nullabilityFlags, NrtState singleNullability, ref int position, NrtState nullability)
    {
        // INVARIANT: This method should only be called for named types (class, struct, interface, enum, delegate)
        // ByRef, Pointer, Array, and GenericParameter types should be handled before reaching here
        if (type.IsByRef || type.IsPointer || type.IsArray || type.IsGenericParameter)
        {
            throw new InvalidOperationException(
                $"CreateNamedWithNullability called for non-named type: {type.FullName} " +
                $"(IsByRef={type.IsByRef}, IsPointer={type.IsPointer}, IsArray={type.IsArray}, IsGenericParameter={type.IsGenericParameter})");
        }

        var assemblyName = type.Assembly.GetName().Name ?? "Unknown";
        var fullName = type.IsGenericType && type.IsConstructedGenericType
            ? type.GetGenericTypeDefinition().FullName ?? type.Name
            : type.FullName ?? type.Name;

        var namespaceName = type.Namespace ?? "";
        var name = type.Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                var lastDot = fullName.LastIndexOf('.');
                var lastPlus = fullName.LastIndexOf('+');
                var lastSeparator = Math.Max(lastDot, lastPlus);
                name = lastSeparator >= 0 ? fullName.Substring(lastSeparator + 1) : fullName;
            }
            else
            {
                name = "UnknownType";
            }
        }

        var arity = 0;
        var typeArgs = new List<TypeReference>();

        if (type.IsGenericType)
        {
            arity = type.GetGenericArguments().Length;

            if (type.IsConstructedGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    // Recursively create type arguments with their nullability
                    typeArgs.Add(CreateWithNullabilityInternal(arg, nullabilityFlags, singleNullability, ref position));
                }
            }
        }

        string? interfaceStableId = null;
        if (type.IsInterface)
        {
            interfaceStableId = _ctx.Intern($"{assemblyName}:{fullName}");
        }

        return new NamedTypeReference
        {
            AssemblyName = _ctx.Intern(assemblyName),
            FullName = _ctx.Intern(fullName),
            Namespace = _ctx.Intern(namespaceName),
            Name = _ctx.Intern(name),
            Arity = arity,
            TypeArguments = typeArgs,
            IsValueType = type.IsValueType,
            InterfaceStableId = interfaceStableId,
            Nullability = nullability
        };
    }
}
