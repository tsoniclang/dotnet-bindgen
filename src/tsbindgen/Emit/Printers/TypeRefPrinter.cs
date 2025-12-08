using System.Text;
using tsbindgen.Model;
using tsbindgen.Model.Types;

namespace tsbindgen.Emit.Printers;

/// <summary>
/// Prints TypeScript type references from TypeReference model.
/// Handles all type constructs: named, generic parameters, arrays, pointers, byrefs, nested.
/// CRITICAL: Uses TypeNameResolver to ensure printed names match imports (single source of truth).
/// </summary>
public static class TypeRefPrinter
{
    /// <summary>
    /// Print a TypeReference to TypeScript syntax.
    /// CRITICAL: Always pass TypeNameResolver - never use CLR names directly.
    /// </summary>
    /// <param name="allowedTypeParameterNames">
    /// TS2304 FIX: Optional set of allowed generic parameter names (class + method level).
    /// If provided, any GenericParameterReference NOT in this set will be demoted to 'unknown'.
    /// This prevents "free type variables" from leaking into signatures.
    /// </param>
    /// <param name="forValuePosition">
    /// If true, this is for extends/implements (value position).
    /// Use qualified names for reflection types to avoid TS2693 errors.
    /// </param>
    public static string Print(
        TypeReference typeRef,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null,
        bool forValuePosition = false)
    {
        return typeRef switch
        {
            // Defensive guard: Placeholders should never reach output after ConstraintCloser
            PlaceholderTypeReference placeholder => PrintPlaceholder(placeholder, ctx),
            NamedTypeReference named => PrintNamed(named, resolver, ctx, allowedTypeParameterNames, forValuePosition),
            GenericParameterReference gp => PrintGenericParameter(gp, ctx, allowedTypeParameterNames),
            ArrayTypeReference arr => PrintArray(arr, resolver, ctx, allowedTypeParameterNames, forValuePosition),
            PointerTypeReference ptr => PrintPointer(ptr, resolver, ctx, allowedTypeParameterNames, forValuePosition),
            ByRefTypeReference byref => PrintByRef(byref, resolver, ctx, allowedTypeParameterNames, forValuePosition),
            NestedTypeReference nested => PrintNested(nested, resolver, ctx, allowedTypeParameterNames, forValuePosition),
            _ => "unknown" // Fallback for unknown types (never use 'any' - breaks type safety)
        };
    }

    private static string PrintPlaceholder(PlaceholderTypeReference placeholder, BuildContext ctx)
    {
        // PlaceholderTypeReference should never appear in final output
        // It's only used internally to break recursion cycles during type construction
        ctx.Diagnostics.Warning(
            Core.Diagnostics.DiagnosticCodes.UnresolvedType,
            $"Placeholder type reached output: {placeholder.DebugName}. " +
            $"This indicates a cycle that wasn't resolved. Emitting 'unknown'.");

        return "unknown";
    }

    private static string PrintNamed(
        NamedTypeReference named,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        // Map CLR primitive types to TypeScript built-in types (short-circuit)
        var primitiveType = TypeNameResolver.TryMapPrimitive(named.FullName);
        if (primitiveType != null)
        {
            return primitiveType;
        }

        // Handle TypeScript built-in types that we synthesize (not from CLR)
        if (named.FullName == "unknown")
        {
            return "unknown";
        }

        // CRITICAL: Get final TypeScript name from Renamer via resolver
        // This ensures printed names match import statements (single source of truth)
        // For types in graph: uses Renamer final name (may have suffix)
        // For external types: uses sanitized CLR simple name
        // Pass forValuePosition to distinguish extends/implements from signatures
        var baseName = resolver.For(named, forValuePosition);

        // HARDENING: Guarantee non-empty type names (defensive check)
        if (string.IsNullOrWhiteSpace(baseName))
        {
            ctx.Diagnostics.Warning(
                Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                $"Empty type name for {named.AssemblyName}:{named.FullName}. " +
                $"Emitting 'unknown' as fallback.");
            return "unknown";
        }

        // Handle generic type arguments
        if (named.TypeArguments.Count == 0)
            return baseName;

        // Print generic type with arguments: Foo<T, U>
        // CRITICAL: Emit CLR type names directly for primitives in generic type arguments
        // This ensures generic constraints are satisfied with direct CLR type names
        // Example: List<int> → List_1<Int32> (Int32 is the CLR type, int is the TS alias)
        // Generic parameters (T, U, TKey) pass through unchanged
        var argParts = named.TypeArguments.Select(arg =>
        {
            var printed = Print(arg, resolver, ctx, allowedTypeParameterNames);
            // Lift primitives to their CLR type names: int → Int32, char → Char, etc.
            // Qualify with System_Internal when outside System namespace
            var clrName = PrimitiveLift.GetClrSimpleName(printed);
            if (clrName != null)
            {
                // CLR primitive types are defined in System namespace
                // Qualify when not in System namespace
                var currentNs = resolver.CurrentNamespace;
                if (currentNs != null && currentNs != "System")
                {
                    return $"System_Internal.{clrName}";
                }
                return clrName;
            }
            return printed;
        }).ToList();
        var nonEmptyArgs = argParts.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        if (nonEmptyArgs.Count == 0)
        {
            // All type arguments erased - emit without generics
            ctx.Diagnostics.Warning(
                Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                $"All type arguments erased for {named.FullName}. Emitting non-generic form.");
            return baseName;
        }

        var args = string.Join(", ", nonEmptyArgs);
        return $"{baseName}<{args}>";
    }

    private static string PrintGenericParameter(
        GenericParameterReference gp,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames)
    {
        // TS2304 FIX: Check if this generic parameter is allowed in current scope
        // If allowedTypeParameterNames is provided and this parameter is NOT in the set,
        // it's a "free type variable" that leaked from an interface implementation.
        // Demote to 'unknown' to prevent TS2304 errors.
        if (allowedTypeParameterNames != null && !allowedTypeParameterNames.Contains(gp.Name))
        {
            ctx.Log("TS2304Fix", $"Demoting unbound generic parameter '{gp.Name}' to 'unknown'");
            return "unknown";
        }

        // Generic parameters use their declared name: T, U, TKey, TValue
        return gp.Name;
    }

    private static string PrintArray(
        ArrayTypeReference arr,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        var elementType = Print(arr.ElementType, resolver, ctx, allowedTypeParameterNames, forValuePosition);

        // Multi-dimensional arrays: T[][], T[][][]
        if (arr.Rank == 1)
            return $"{elementType}[]";

        // For rank > 1, TypeScript doesn't have native syntax
        // Use Array<Array<T>> form
        var result = elementType;
        for (int i = 0; i < arr.Rank; i++)
            result = $"Array<{result}>";

        return result;
    }

    private static string PrintPointer(
        PointerTypeReference ptr,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        // TypeScript has no pointer types
        // Use ptr<T> from @tsonic/types (branded as unknown)
        // This preserves type information while being type-safe (forces explicit handling)
        var pointeeType = Print(ptr.PointeeType, resolver, ctx, allowedTypeParameterNames, forValuePosition);
        return $"ptr<{pointeeType}>";
    }

    private static string PrintByRef(
        ByRefTypeReference byref,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        // Emit element type only - ref/out/in are ABI modifiers tracked in metadata, not TS types
        // Parameter modifier semantics are enforced by Tsonic compiler using metadata
        return Print(byref.ReferencedType, resolver, ctx, allowedTypeParameterNames, forValuePosition);
    }

    private static string PrintNested(
        NestedTypeReference nested,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        // CRITICAL: Nested types use resolver just like named types
        // The FullReference is a NamedTypeReference that the resolver will handle correctly
        return PrintNamed(nested.FullReference, resolver, ctx, allowedTypeParameterNames, forValuePosition);
    }

    /// <summary>
    /// Sanitize CLR type name for TypeScript.
    /// Handles generic arity (`1 → _1) and special characters.
    /// </summary>
    private static string SanitizeClrName(string clrName)
    {
        // Replace generic arity backtick with underscore: List`1 → List_1
        var sanitized = clrName.Replace('`', '_');

        // Remove any remaining invalid TypeScript identifier characters
        sanitized = sanitized.Replace('+', '_'); // Nested type separator
        sanitized = sanitized.Replace('<', '_');
        sanitized = sanitized.Replace('>', '_');
        sanitized = sanitized.Replace('[', '_');
        sanitized = sanitized.Replace(']', '_');

        return sanitized;
    }

    /// <summary>
    /// Print a list of type references separated by commas.
    /// Used for generic parameter lists, method parameters, etc.
    /// </summary>
    public static string PrintList(
        IEnumerable<TypeReference> typeRefs,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        return string.Join(", ", typeRefs.Select(t => Print(t, resolver, ctx, allowedTypeParameterNames)));
    }

    /// <summary>
    /// Print a type reference with optional nullability.
    /// Used for nullable value types and reference types.
    /// </summary>
    public static string PrintNullable(
        TypeReference typeRef,
        bool isNullable,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var baseType = Print(typeRef, resolver, ctx, allowedTypeParameterNames);
        return isNullable ? $"{baseType} | null" : baseType;
    }

    /// <summary>
    /// Print a readonly array type.
    /// Used for ReadonlyArray<T> mappings from IEnumerable<T>, etc.
    /// </summary>
    public static string PrintReadonlyArray(
        TypeReference elementType,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var element = Print(elementType, resolver, ctx, allowedTypeParameterNames);
        return $"ReadonlyArray<{element}>";
    }

    /// <summary>
    /// Print a Promise type for Task<T> mappings.
    /// </summary>
    public static string PrintPromise(
        TypeReference resultType,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var result = Print(resultType, resolver, ctx, allowedTypeParameterNames);
        return $"Promise<{result}>";
    }

    /// <summary>
    /// Print a tuple type for ValueTuple mappings.
    /// </summary>
    public static string PrintTuple(
        IReadOnlyList<TypeReference> elementTypes,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var elements = string.Join(", ", elementTypes.Select(t => Print(t, resolver, ctx, allowedTypeParameterNames)));
        return $"[{elements}]";
    }

    /// <summary>
    /// Print a union type for TypeScript union types.
    /// </summary>
    public static string PrintUnion(
        IReadOnlyList<TypeReference> types,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var parts = string.Join(" | ", types.Select(t => Print(t, resolver, ctx, allowedTypeParameterNames)));
        return parts;
    }

    /// <summary>
    /// Print an intersection type for TypeScript intersection types.
    /// </summary>
    public static string PrintIntersection(
        IReadOnlyList<TypeReference> types,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var parts = string.Join(" & ", types.Select(t => Print(t, resolver, ctx, allowedTypeParameterNames)));
        return parts;
    }

    /// <summary>
    /// Print a typeof expression for static class references.
    /// Used for: typeof ClassName → (typeof ClassName)
    /// </summary>
    public static string PrintTypeof(
        TypeReference typeRef,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames = null)
    {
        var typeName = Print(typeRef, resolver, ctx, allowedTypeParameterNames);
        return $"typeof {typeName}";
    }
}
