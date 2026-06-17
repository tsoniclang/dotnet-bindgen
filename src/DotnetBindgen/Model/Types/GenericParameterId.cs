namespace DotnetBindgen.Model.Types;

/// <summary>
/// Unique identifier for a generic parameter.
/// Combines the declaring type and parameter position to create a stable identity.
/// Used for substitution when implementing closed generic interfaces.
/// </summary>
public sealed record GenericParameterId
{
    /// <summary>
    /// Full name of the type that declares this generic parameter.
    /// For method-level generics, includes the method signature.
    /// Example: "System.Collections.Generic.List`1" or "System.Linq.Enumerable.Select`2"
    /// </summary>
    public required string DeclaringTypeName { get; init; }

    /// <summary>
    /// Zero-based position in the generic parameter list.
    /// Example: In Dictionary<TKey, TValue>, TKey has position 0, TValue has position 1.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>
    /// True if this is a method-level generic parameter (rare in BCL).
    /// </summary>
    public bool IsMethodParameter { get; init; }

    public override string ToString() =>
        $"{DeclaringTypeName}#{Position}{(IsMethodParameter ? "M" : "")}";
}
