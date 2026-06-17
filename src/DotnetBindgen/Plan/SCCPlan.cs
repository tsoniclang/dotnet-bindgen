using System.Collections.Generic;

namespace DotnetBindgen.Plan;

/// <summary>
/// Plan for Strongly Connected Component (SCC) bucketing.
/// Groups namespaces by SCC to eliminate circular import dependencies.
/// </summary>
public sealed record SCCPlan
{
    /// <summary>
    /// List of all SCCs in the namespace dependency graph.
    /// Each SCC is a list of namespace names that form a cycle.
    /// Single-namespace SCCs indicate no circular dependencies.
    /// </summary>
    public required IReadOnlyList<SCCBucket> Buckets { get; init; }

    /// <summary>
    /// Maps namespace name to its SCC bucket index.
    /// </summary>
    public required IReadOnlyDictionary<string, int> NamespaceToBucket { get; init; }
}

/// <summary>
/// A single SCC bucket containing one or more namespaces.
/// </summary>
public sealed record SCCBucket
{
    /// <summary>
    /// Unique identifier for this SCC bucket.
    /// Format: "scc_{index}" for multi-namespace SCCs, or namespace name for singleton SCCs.
    /// </summary>
    public required string BucketId { get; init; }

    /// <summary>
    /// Namespaces in this SCC (sorted alphabetically for determinism).
    /// </summary>
    public required IReadOnlyList<string> Namespaces { get; init; }

    /// <summary>
    /// True if this SCC contains multiple namespaces (circular dependency).
    /// False if singleton (no circular dependency).
    /// </summary>
    public bool IsMultiNamespace => Namespaces.Count > 1;
}
