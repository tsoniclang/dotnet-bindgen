using System.Collections.Generic;

namespace tsbindgen.Plan;

/// <summary>
/// PR C: Plan for honest TypeScript emission of interface conformance.
/// Tracks interfaces that types claim to implement in CLR but cannot fully express in TypeScript.
/// These interfaces are omitted from TS 'implements' clauses but preserved in metadata.
/// </summary>
public sealed record HonestEmissionPlan
{
    /// <summary>
    /// Maps type CLR full name → list of interface CLR full names that cannot be satisfied in TypeScript.
    /// These interfaces will be:
    /// - Omitted from TypeScript 'implements' clause
    /// - Preserved in metadata.json with omission reason
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<UnsatisfiableInterface>> UnsatisfiableInterfaces { get; init; }

    /// <summary>
    /// Total count of type-interface pairs that cannot be satisfied.
    /// </summary>
    public int TotalUnsatisfiableCount { get; init; }
}

/// <summary>
/// Represents an interface that a type claims to implement but cannot satisfy in TypeScript.
/// </summary>
public sealed record UnsatisfiableInterface
{
    /// <summary>
    /// CLR full name of the interface (e.g., "System.Numerics.IBinaryNumber`1").
    /// </summary>
    public required string InterfaceClrName { get; init; }

    /// <summary>
    /// Reason why this interface cannot be satisfied in TypeScript.
    /// </summary>
    public required UnsatisfiableReason Reason { get; init; }

    /// <summary>
    /// Number of conformance issues detected (missing methods, incompatible signatures, etc.).
    /// </summary>
    public required int IssueCount { get; init; }
}

/// <summary>
/// Reason why an interface cannot be fully satisfied in TypeScript.
/// </summary>
public enum UnsatisfiableReason
{
    /// <summary>
    /// Interface has members that are missing or incompatible on the class surface.
    /// Common for generic math interfaces with static abstract members.
    /// </summary>
    MissingOrIncompatibleMembers,

    /// <summary>
    /// Interface members are implemented explicitly and routed to companion "views" interface.
    /// Views are not on the class surface, so TS `implements` cannot claim them.
    /// </summary>
    ExplicitImplementationMovedToView,

    /// <summary>
    /// Other TypeScript language limitations.
    /// </summary>
    LanguageLimitation
}
