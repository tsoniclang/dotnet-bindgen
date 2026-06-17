namespace DotnetBindgen.Plan;

/// <summary>
/// Represents a structured conformance issue for an interface.
/// Used to track why an interface cannot be satisfied in TypeScript.
/// </summary>
public sealed record ConformanceIssue(
    /// <summary>
    /// The CLR full name of the interface (e.g., "System.Collections.Generic.IEnumerable`1").
    /// </summary>
    string InterfaceClrFullName,

    /// <summary>
    /// The short name of the interface for display (e.g., "IEnumerable`1").
    /// </summary>
    string InterfaceShortName,

    /// <summary>
    /// The reason this interface is unsatisfiable.
    /// </summary>
    UnsatisfiableReason Reason,

    /// <summary>
    /// Optional details about the issue (e.g., which member is missing).
    /// Used for diagnostics/logging only, not for logic.
    /// </summary>
    string? Details = null
);
