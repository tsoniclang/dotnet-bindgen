using DotnetBindgen.Renaming;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Model.Symbols.MemberSymbols;

/// <summary>
/// Represents a field member.
/// IMMUTABLE record.
/// </summary>
public sealed record FieldSymbol
{
    /// <summary>
    /// Stable identifier for this field.
    /// </summary>
    public required MemberStableId StableId { get; init; }

    /// <summary>
    /// CLR field name.
    /// </summary>
    public required string ClrName { get; init; }

    /// <summary>
    /// TypeScript emit name (set by NameApplication after reservation).
    /// </summary>
    public string TsEmitName { get; init; } = "";

    /// <summary>
    /// Field type.
    /// </summary>
    public required TypeReference FieldType { get; init; }

    /// <summary>
    /// True if this is a static field.
    /// </summary>
    public required bool IsStatic { get; init; }

    /// <summary>
    /// True if this is readonly.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// True if this is a constant (const).
    /// </summary>
    public bool IsConst { get; init; }

    /// <summary>
    /// Constant value (if IsConst is true).
    /// </summary>
    public object? ConstValue { get; init; }

    /// <summary>
    /// Visibility.
    /// </summary>
    public required Visibility Visibility { get; init; }

    /// <summary>
    /// Provenance.
    /// </summary>
    public required MemberProvenance Provenance { get; init; }

    /// <summary>
    /// Where this member should be emitted (class surface, view, or omitted).
    /// MUST be explicitly set during Shape phase - defaults to Unspecified.
    /// </summary>
    public EmitScope EmitScope { get; init; }

    /// <summary>
    /// Documentation.
    /// </summary>
    public string? Documentation { get; init; }
}
