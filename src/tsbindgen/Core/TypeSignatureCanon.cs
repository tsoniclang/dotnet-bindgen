using tsbindgen.Model.Types;

namespace tsbindgen.Core;

/// <summary>
/// Canonical TypeScript signature computation for overload detection.
/// Used by both Reservation (name allocation) and Names (validation).
///
/// CRITICAL: This must match the emitted TypeScript surface, not CLR names.
/// - Primitives map to their TS branded types (int, decimal, NOT number)
/// - Generic type arguments for primitives get CLR type names (int → Int32)
/// - Generic parameters stay as their names (T, TKey, etc.)
///
/// This is the SINGLE SOURCE OF TRUTH for signature canonicalization.
/// Both Reservation.cs and Names.cs MUST use this to avoid drift.
/// </summary>
public static class TypeSignatureCanon
{
    /// <summary>
    /// Compute a canonical TypeScript-level parameter signature for overload grouping.
    /// Methods with the same erased signature would be duplicate overloads in TypeScript.
    /// </summary>
    public static string ComputeMethodSignature(
        int arity,
        IEnumerable<TypeReference> parameterTypes)
    {
        var paramParts = parameterTypes.Select(t => CanonicalizeType(t, isGenericArg: false));
        return $"<{arity}>({string.Join(",", paramParts)})";
    }

    /// <summary>
    /// Canonicalize a TypeReference to its TypeScript-level representation.
    /// This matches what TypeRefPrinter.Print() would produce.
    /// </summary>
    /// <param name="typeRef">The type reference to canonicalize</param>
    /// <param name="isGenericArg">If true, lift primitives to their CLR type names (int → Int32)</param>
    public static string CanonicalizeType(TypeReference typeRef, bool isGenericArg = false)
    {
        return typeRef switch
        {
            NamedTypeReference named => CanonicalizeNamed(named, isGenericArg),
            NestedTypeReference nested => CanonicalizeNamed(nested.FullReference, isGenericArg),
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"ReadonlyArray<{CanonicalizeType(arr.ElementType, isGenericArg: true)}>",
            PointerTypeReference ptr => $"ptr<{CanonicalizeType(ptr.PointeeType, isGenericArg: true)}>",
            ByRefTypeReference byref => $"ref<{CanonicalizeType(byref.ReferencedType, isGenericArg: true)}>",
            _ => "__OpaqueClrType<\"unhandled-type-ref\">"
        };
    }

    private static string CanonicalizeNamed(NamedTypeReference named, bool isGenericArg)
    {
        // Map CLR primitives to TypeScript types (must match TypeMap.TryMapBuiltin)
        var tsType = MapPrimitiveType(named.FullName);
        if (tsType != null)
        {
            // If in generic position, lift to CLR type name (int → Int32)
            if (isGenericArg)
            {
                var clrName = GetClrSimpleName(tsType);
                return clrName ?? tsType;
            }
            return tsType;
        }

        // Non-primitive named type
        var baseName = named.FullName.Replace("`", "_");

        if (named.TypeArguments.Count == 0)
            return baseName;

        // Generic type with arguments - canonicalize each arg with isGenericArg=true
        var argParts = named.TypeArguments.Select(arg => CanonicalizeType(arg, isGenericArg: true));
        return $"{baseName}<{string.Join(",", argParts)}>";
    }

    /// <summary>
    /// Map CLR primitive type to TypeScript type.
    /// MUST match TypeMap.TryMapBuiltin exactly.
    /// </summary>
    private static string? MapPrimitiveType(string clrFullName)
    {
        return clrFullName switch
        {
            // Void
            "System.Void" => "void",

            // Boolean
            "System.Boolean" => "boolean",

            // String
            "System.String" => "string",

            // Object
            "System.Object" => "JsValue",

            // Signed integers (branded types)
            "System.SByte" => "sbyte",
            "System.Int16" => "short",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Int128" => "int128",
            "System.IntPtr" => "nint",

            // Unsigned integers (branded types)
            "System.Byte" => "byte",
            "System.UInt16" => "ushort",
            "System.UInt32" => "uint",
            "System.UInt64" => "ulong",
            "System.UInt128" => "uint128",
            "System.UIntPtr" => "nuint",

            // Floating point (branded types)
            "System.Half" => "half",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Decimal" => "decimal",

            // Char (branded type)
            "System.Char" => "char",

            // Value type base
            "System.ValueType" => "NonNullable<JsValue>",

            // Enum base
            "System.Enum" => "number",

            // Delegate base
            "System.Delegate" => "Function",
            "System.MulticastDelegate" => "Function",

            _ => null
        };
    }

    /// <summary>
    /// Get the CLR simple name for a TypeScript primitive type.
    /// Returns null if not a liftable primitive.
    /// MUST match PrimitiveLift.GetClrSimpleName exactly.
    /// </summary>
    private static string? GetClrSimpleName(string tsType)
    {
        return tsType switch
        {
            // Signed integers
            "sbyte" => "SByte",
            "short" => "Int16",
            "int" => "Int32",
            "long" => "Int64",
            "int128" => "Int128",
            "nint" => "IntPtr",

            // Unsigned integers
            "byte" => "Byte",
            "ushort" => "UInt16",
            "uint" => "UInt32",
            "ulong" => "UInt64",
            "uint128" => "UInt128",
            "nuint" => "UIntPtr",

            // Floating point
            "half" => "Half",
            "float" => "Single",
            "double" => "Double",
            "decimal" => "Decimal",

            // Other
            "char" => "Char",
            "boolean" => "Boolean",
            "string" => "String",

            _ => null
        };
    }
}
