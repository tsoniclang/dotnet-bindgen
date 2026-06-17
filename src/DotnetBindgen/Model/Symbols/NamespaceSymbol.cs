using System.Collections.Immutable;
using DotnetBindgen.Renaming;

namespace DotnetBindgen.Model.Symbols;

/// <summary>
/// Represents a namespace containing types.
/// Created during aggregation phase.
/// IMMUTABLE - use 'with' expressions to create modified copies.
/// </summary>
public sealed record NamespaceSymbol
{
    /// <summary>
    /// Namespace name (e.g., "System.Collections.Generic").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// All types in this namespace.
    /// </summary>
    public required ImmutableArray<TypeSymbol> Types { get; init; }

    /// <summary>
    /// Stable identifier for this namespace.
    /// </summary>
    public required StableId StableId { get; init; }

    /// <summary>
    /// Assembly names contributing to this namespace.
    /// Multiple assemblies can contribute to the same namespace.
    /// </summary>
    public required ImmutableHashSet<string> ContributingAssemblies { get; init; }

    /// <summary>
    /// True if this is the root/global namespace (empty name).
    /// Root namespace types are emitted at module level in TypeScript (no namespace wrapper).
    /// </summary>
    public bool IsRoot => string.IsNullOrEmpty(Name);

    /// <summary>
    /// Returns namespace name or null if this is the root namespace.
    /// Used by emitters to avoid printing empty namespace tokens.
    /// </summary>
    public string? SafeNameOrNull => IsRoot ? null : Name;
}
