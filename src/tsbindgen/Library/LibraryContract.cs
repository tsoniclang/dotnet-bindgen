using System.Collections.Immutable;
using tsbindgen.Emit;

namespace tsbindgen.Library;

/// <summary>
/// Represents the contract defined by an existing tsbindgen library package.
/// Loaded from metadata.json, bindings.json, and families.json files.
/// Used to filter emission to only symbols present in the library.
/// </summary>
public sealed record LibraryContract
{
    /// <summary>
    /// npm package name from package.json (e.g., "@tsonic/dotnet").
    /// Used for generating import specifiers for external library types.
    /// </summary>
    public required string PackageName { get; init; }

    /// <summary>
    /// Set of allowed type StableIds (format: "AssemblyName:ClrFullName").
    /// A type is emittable iff its StableId exists in this set.
    /// </summary>
    public required ImmutableHashSet<string> AllowedTypeStableIds { get; init; }

    /// <summary>
    /// Set of allowed member StableIds (format: "AssemblyName:DeclaringType::MemberNameSignature").
    /// A member is emittable iff its StableId exists in this set.
    /// </summary>
    public required ImmutableHashSet<string> AllowedMemberStableIds { get; init; }

    /// <summary>
    /// Set of binding StableIds from bindings.json.
    /// Used to validate that all emitted members have corresponding bindings.
    /// </summary>
    public required ImmutableHashSet<string> AllowedBindingStableIds { get; init; }

    /// <summary>
    /// Mapping from namespace name to set of type StableIds in that namespace.
    /// Used to preserve namespace structure from the library.
    /// </summary>
    public required ImmutableDictionary<string, ImmutableHashSet<string>> NamespaceToTypes { get; init; }

    /// <summary>
    /// Set of CLR full names (extracted from AllowedTypeStableIds).
    /// Format: "System.Exception", "System.Collections.Generic.List`1", etc.
    /// Used for per-type membership checks in import planning.
    /// Derived from AllowedTypeStableIds by stripping assembly prefix.
    /// </summary>
    public required ImmutableHashSet<string> AllowedClrFullNames { get; init; }

    /// <summary>
    /// Mapping from CLR full name to namespace.
    /// Used to determine which namespace facade to import a type from.
    /// Example: "System.Exception" → "System"
    /// </summary>
    public required ImmutableDictionary<string, string> ClrFullNameToNamespace { get; init; }

    /// <summary>
    /// Canonical facade family index.
    /// Maps CLR base name (e.g., "System.ValueTuple") to family entry with stem/namespace/arity info.
    /// ImportPlanner uses this to determine if a type belongs to a multi-arity family
    /// WITHOUT recomputing from AllowedClrFullNames (prevents drift).
    /// Empty if families.json doesn't exist.
    /// </summary>
    public required ImmutableDictionary<string, FacadeFamilyEntry> FacadeFamilies { get; init; }

    /// <summary>
    /// Total number of types in the contract.
    /// </summary>
    public int TypeCount => AllowedTypeStableIds.Count;

    /// <summary>
    /// Total number of members in the contract.
    /// </summary>
    public int MemberCount => AllowedMemberStableIds.Count;

    /// <summary>
    /// Total number of namespaces in the contract.
    /// </summary>
    public int NamespaceCount => NamespaceToTypes.Count;
}
