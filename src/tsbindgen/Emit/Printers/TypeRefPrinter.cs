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
            // FIX 1: Reference type primitives (String, Object) must respect NRT
            // Only value types can skip nullability check
            if (named.Nullability == NrtState.Nullable && !named.IsValueType)
            {
                return $"{primitiveType} | undefined";
            }
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

        // LIBRARY MODE (InternalIndex): Multi-arity family base names must be lowered to arity-stable internal names.
        //
        // Example:
        //   NamedTypeReference.FullName = "System.Linq.IQueryable" (family base), TypeArguments = [TEntity]
        //   Facade surface uses: IQueryable<TEntity> (conditional alias)
        //   But internal/index.d.ts must use: IQueryable_1<TEntity> (real interface) so members propagate.
        //
        // Without this, interfaces can claim branded membership (via __tsonic_iface_*) but still fail
        // structural assignability (missing Expression/Provider/etc.), which breaks extension methods.
        if (resolver.LibraryImportStyle == Plan.LibraryImportStyle.InternalIndex &&
            ctx.LibraryContract != null &&
            named.TypeArguments.Count > 0)
        {
            var fullName = named.FullName;
            var commaIndex = fullName.IndexOf(',');
            if (commaIndex >= 0)
                fullName = fullName.Substring(0, commaIndex).Trim();

            if (ctx.LibraryContract.FacadeFamilies.ContainsKey(fullName))
            {
                baseName = $"{baseName}_{named.TypeArguments.Count}";
            }
        }

        string result;

        // Handle generic type arguments
        if (named.TypeArguments.Count == 0)
        {
            result = baseName;
        }
        else
        {
            // Special-case: some self-constrained parsing interfaces (IParsable/ISpanParsable/...) use
            // `TSelf extends IFoo<TSelf>` patterns. When a primitive like Boolean is modeled as a union
            // (`Boolean = boolean | Boolean$shape`), instantiating these interfaces with `Boolean`
            // triggers TS2344 (union includes raw boolean, which cannot satisfy the recursive constraint).
            //
            // To keep the ergonomic `Boolean` surface while remaining TypeScript-semantic-error-free,
            // we instantiate these parsing interfaces with the non-union shape alias `Boolean$shape`.
            var outerFullName = named.FullName;
            var outerCommaIndex = outerFullName.IndexOf(',');
            if (outerCommaIndex >= 0)
                outerFullName = outerFullName.Substring(0, outerCommaIndex).Trim();

            var useBooleanShapeInArgs =
                outerFullName is "System.IParsable`1" or "System.ISpanParsable`1" or "System.IUtf8SpanParsable`1";

            // Print generic type with arguments: Foo<T, U>
            // CRITICAL: Emit CLR type names directly for primitives in generic type arguments
            // This ensures generic constraints are satisfied with direct CLR type names
            // Example: List<int> → List_1<Int32> (Int32 is the CLR type, int is the TS alias)
            // Generic parameters (T, U, TKey) pass through unchanged
            var argParts = named.TypeArguments.Select(arg =>
            {
                var printed = Print(arg, resolver, ctx, allowedTypeParameterNames);
                // Lift primitives to their CLR type names (Int32, String, ...) only when
                // emitting internal-index shapes. Facade surfaces should preserve the
                // ergonomic primitive aliases (int, string, boolean, ...) and should
                // not introduce System_Internal-qualified primitive names.
                if (resolver.LibraryImportStyle != Plan.LibraryImportStyle.Facade)
                {
                    // Lift primitives to their CLR type names: int → Int32, char → Char, etc.
                    // Qualify with System_Internal when outside System namespace
                    var clrName = PrimitiveLift.GetClrSimpleName(printed);
                    if (clrName != null)
                    {
                        if (useBooleanShapeInArgs && clrName == "Boolean")
                        {
                            clrName = "Boolean$shape";
                        }

                        // CLR primitive types are defined in System namespace
                        // Qualify when not in System namespace
                        var currentNs = resolver.CurrentNamespace;
                        if (currentNs != null && currentNs != "System")
                        {
                            return $"System_Internal.{clrName}";
                        }
                        return clrName;
                    }
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
                result = baseName;
            }
            else
            {
                var args = string.Join(", ", nonEmptyArgs);
                result = $"{baseName}<{args}>";
            }
        }

        // NRT: Append | undefined for explicitly nullable REFERENCE types only
        // Value types use Nullable<T> (emitted separately), never "| undefined"
        // Guard: even if a bug sets Nullability=Nullable on a value type, don't emit union
        if (named.Nullability == NrtState.Nullable && !named.IsValueType)
        {
            return $"{result} | undefined";
        }

        return result;
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
        var result = gp.Name;

        // NRT: Emit T | undefined for nullable generic parameters in OUTPUT positions
        // Since parameters no longer read NRT (always Oblivious), this only triggers for:
        // - Return types
        // - Property types (getters)
        // - Field types
        // This is correct per Alice's analysis: outputs respect NRT, inputs are strict
        if (gp.Nullability == NrtState.Nullable)
        {
            return $"{result} | undefined";
        }

        return result;
    }

    private static string PrintArray(
        ArrayTypeReference arr,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParameterNames,
        bool forValuePosition = false)
    {
        var elementType = Print(arr.ElementType, resolver, ctx, allowedTypeParameterNames, forValuePosition);

        // FIX 7: Model-driven parenthesis decision
        // Check if element type will render as a union (requires parentheses for correct precedence)
        // Without this: "T | undefined[]" parses as "T | (undefined[])" - WRONG
        // With this: "(T | undefined)[]" - CORRECT
        if (RequiresParenthesesForArrayElement(arr.ElementType))
        {
            elementType = $"({elementType})";
        }

        string result;

        // Multi-dimensional arrays: T[][], T[][][]
        if (arr.Rank == 1)
        {
            result = $"{elementType}[]";
        }
        else
        {
            // For rank > 1, TypeScript doesn't have native syntax
            // Use Array<Array<T>> form
            result = elementType;
            for (int i = 0; i < arr.Rank; i++)
                result = $"Array<{result}>";
        }

        // NRT: Append | undefined for explicitly nullable array references (T[]?)
        if (arr.Nullability == NrtState.Nullable)
        {
            return $"{result} | undefined";
        }

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
        // Use ptr<T> from @tsonic/core/types.js (branded as unknown)
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

    /// <summary>
    /// FIX 7: Check if an array element type requires parentheses.
    /// This is a model-driven check rather than string-based detection.
    /// Returns true if the element type will render as a union (nullable reference type).
    /// </summary>
    private static bool RequiresParenthesesForArrayElement(TypeReference elementType)
    {
        // Check if element type has nullable NRT annotation (will render as " | undefined")
        return elementType switch
        {
            NamedTypeReference named => named.Nullability == NrtState.Nullable && !named.IsValueType,
            GenericParameterReference gp => gp.Nullability == NrtState.Nullable,
            ArrayTypeReference arr => arr.Nullability == NrtState.Nullable,
            _ => false
        };
    }
}
