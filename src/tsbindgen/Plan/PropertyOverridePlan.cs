using System.Collections.Generic;

namespace tsbindgen.Plan;

/// <summary>
/// Plan for property override type unification.
/// When a property is overridden with a different type along an inheritance chain,
/// TypeScript requires all declarations to use the same type.
/// This plan records unified union types for such properties.
///
/// Example:
///   class RequestCachePolicy { readonly level: RequestCacheLevel; }
///   class HttpRequestCachePolicy : RequestCachePolicy { readonly level: HttpRequestCacheLevel; }
///
/// TypeScript sees this as TS2416 (incompatible property types).
/// Solution: Use union type in both classes:
///   readonly level: RequestCacheLevel | HttpRequestCacheLevel;
/// </summary>
public sealed class PropertyOverridePlan
{
    /// <summary>
    /// Maps (type stable ID, property stable ID) → unified TypeScript type string.
    /// When a property override chain has conflicting types, all properties in the chain
    /// get the same union type string to satisfy TypeScript's assignability rules.
    ///
    /// Key: (TypeSymbol.StableId, PropertySymbol.StableId)
    /// Value: TypeScript type string (e.g., "RequestCacheLevel | HttpRequestCacheLevel")
    /// </summary>
    public Dictionary<(string TypeStableId, string PropertyStableId), string> PropertyTypeOverrides { get; init; } = new();

    /// <summary>
    /// Maps (type stable ID, property stable ID) → set of referenced CLR type full names that must be
    /// considered during import planning for the override type string.
    ///
    /// Why this exists:
    /// - PropertyOverrideUnifier injects unified type strings AFTER import planning.
    /// - Those strings can mention types that were not otherwise referenced in the declaring namespace,
    ///   which would produce invalid .d.ts output (missing import, TS2304).
    ///
    /// This map lets the build pipeline augment ImportGraph before ImportPlanner runs, so any types
    /// introduced by override unification are imported deterministically and airplane-grade.
    /// </summary>
    public Dictionary<(string TypeStableId, string PropertyStableId), HashSet<string>> PropertyOverrideReferencedClrTypes { get; init; } = new();
}
