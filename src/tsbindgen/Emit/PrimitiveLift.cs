using System;
using System.Collections.Generic;

namespace tsbindgen.Emit;

/// <summary>
/// Defines the primitive type mappings for tsbindgen.
/// This is the single source of truth for which primitives get lifted to their CLR types in generic contexts.
///
/// Usage:
/// - TypeRefPrinter uses GetClrSimpleName to emit CLR type names in generic type arguments
/// - InternalIndexEmitter uses GetTsPrimitiveName to emit primitive type aliases
/// - Constraint relaxation uses GetTsCarrierKinds to build the union type that admits all primitives
/// </summary>
internal static class PrimitiveLift
{
    /// <summary>
    /// Allowed TypeScript carrier types for primitives.
    /// This is the exhaustive set per @tsonic/types - any other carrier is a contract violation.
    ///
    /// COORDINATION REQUIREMENT: This set is defined by @tsonic/types and must ONLY be updated
    /// alongside a coordinated @tsonic/types change. If you add a new carrier here (e.g., "bigint"),
    /// you must also update @tsonic/types to define the corresponding branded primitive types.
    /// </summary>
    private static readonly HashSet<string> AllowedCarriers = new() { "number", "string", "boolean" };

    /// <summary>
    /// Primitive lifting rules: TypeScript primitive name → CLR full type name → CLR simple name → TypeScript carrier type.
    ///
    /// TsCarrier is the underlying TypeScript type that the branded primitive extends.
    /// Per @tsonic/types contract:
    /// - "number" for ALL numeric types (including long, ulong, nint, nuint, int128, uint128, decimal)
    /// - "string" for char
    /// - "boolean" for bool
    /// </summary>
    internal static readonly (string TsName, string ClrFullName, string ClrSimpleName, string TsCarrier)[] Rules =
    {
        // Signed integers - ALL number-carried per @tsonic/types
        ("sbyte",   "System.SByte",   "SByte",   "number"),
        ("short",   "System.Int16",   "Int16",   "number"),
        ("int",     "System.Int32",   "Int32",   "number"),
        ("long",    "System.Int64",   "Int64",   "number"),
        ("int128",  "System.Int128",  "Int128",  "number"),
        ("nint",    "System.IntPtr",  "IntPtr",  "number"),

        // Unsigned integers - ALL number-carried per @tsonic/types
        ("byte",    "System.Byte",    "Byte",    "number"),
        ("ushort",  "System.UInt16",  "UInt16",  "number"),
        ("uint",    "System.UInt32",  "UInt32",  "number"),
        ("ulong",   "System.UInt64",  "UInt64",  "number"),
        ("uint128", "System.UInt128", "UInt128", "number"),
        ("nuint",   "System.UIntPtr", "UIntPtr", "number"),

        // Floating point - ALL number-carried per @tsonic/types
        ("half",    "System.Half",    "Half",    "number"),
        ("float",   "System.Single",  "Single",  "number"),
        ("double",  "System.Double",  "Double",  "number"),
        ("decimal", "System.Decimal", "Decimal", "number"),

        // Other
        ("char",    "System.Char",    "Char",    "string"),
        ("boolean", "System.Boolean", "Boolean", "boolean"),
        ("string",  "System.String",  "String",  "string"),
    };

    /// <summary>
    /// Get the CLR simple name (for emission) for a given TS primitive.
    /// Returns null if not a liftable primitive.
    /// </summary>
    internal static string? GetClrSimpleName(string tsName) =>
        Rules.FirstOrDefault(r => r.TsName == tsName).ClrSimpleName;

    /// <summary>
    /// Get the TypeScript primitive name for a given CLR full name.
    /// Returns null if not a liftable primitive.
    /// Used by InternalIndexEmitter to emit primitive type aliases.
    /// </summary>
    internal static string? GetTsPrimitiveName(string clrFullName) =>
        Rules.FirstOrDefault(r => r.ClrFullName == clrFullName).TsName;

    /// <summary>
    /// Check if a CLR type is a primitive that should get special emission treatment.
    /// Primitives are emitted with their TS carrier as the base type in the intersection.
    /// </summary>
    internal static bool IsPrimitive(string clrFullName) =>
        Rules.Any(r => r.ClrFullName == clrFullName);

    /// <summary>
    /// Get the TypeScript carrier type for a CLR primitive.
    /// Returns null if not a primitive.
    /// Used by ClassPrinter to emit the base type in primitive intersections.
    /// Example: System.String → "string", System.Int32 → "number"
    /// </summary>
    internal static string? GetTsCarrier(string clrFullName) =>
        Rules.FirstOrDefault(r => r.ClrFullName == clrFullName).TsCarrier;

    /// <summary>
    /// Get the set of unique TypeScript carrier types used by all primitives.
    /// Used by constraint relaxation to build the union type that admits all primitives.
    /// Returns a stable ordered set: { "number", "string", "boolean" }
    ///
    /// HARDENED: Validates all carriers are from the allowed set. If an unknown carrier
    /// appears (contract violation), throws immediately to fail the build.
    /// </summary>
    internal static IReadOnlyList<string> GetTsCarrierKinds()
    {
        var carriers = Rules.Select(r => r.TsCarrier).Distinct().ToArray();

        // Validate: all carriers must be in the allowed set
        foreach (var carrier in carriers)
        {
            if (!AllowedCarriers.Contains(carrier))
            {
                // Find which primitives introduced the unexpected carrier
                var offendingPrimitives = Rules
                    .Where(r => r.TsCarrier == carrier)
                    .Select(r => $"{r.TsName} ({r.ClrFullName})")
                    .ToArray();

                throw new InvalidOperationException(
                    $"PrimitiveLift contract violation: carrier '{carrier}' is not in the allowed set " +
                    $"{{ {string.Join(", ", AllowedCarriers)} }}. " +
                    $"Offending primitives: {{ {string.Join(", ", offendingPrimitives)} }}. " +
                    $"Update AllowedCarriers if this is intentional (requires coordinated @tsonic/types change).");
            }
        }

        // Return in stable order: number, string, boolean
        return carriers.OrderBy(c => c switch
        {
            "number" => 0,
            "string" => 1,
            "boolean" => 2,
            _ => 99  // Should never happen due to validation above
        }).ToArray();
    }
}
