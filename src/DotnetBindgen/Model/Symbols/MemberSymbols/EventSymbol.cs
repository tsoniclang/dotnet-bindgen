using DotnetBindgen.Renaming;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Model.Symbols.MemberSymbols;

/// <summary>
/// Represents an event member.
/// IMMUTABLE record.
/// </summary>
public sealed record EventSymbol
{
    /// <summary>
    /// Stable identifier for this event.
    /// </summary>
    public required MemberStableId StableId { get; init; }

    /// <summary>
    /// CLR event name.
    /// </summary>
    public required string ClrName { get; init; }

    /// <summary>
    /// TypeScript emit name (set by NameApplication after reservation).
    /// </summary>
    public string TsEmitName { get; init; } = "";

    /// <summary>
    /// Event handler type (delegate type).
    /// </summary>
    public required TypeReference EventHandlerType { get; init; }

    /// <summary>
    /// True if this is a static event.
    /// </summary>
    public required bool IsStatic { get; init; }

    /// <summary>
    /// True if this is virtual.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// True if this overrides a base event.
    /// </summary>
    public bool IsOverride { get; init; }

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

    /// <summary>
    /// Source interface (for interface-sourced events).
    /// </summary>
    public TypeReference? SourceInterface { get; init; }

    /// <summary>
    /// Create a new EventSymbol with updated SourceInterface.
    /// Wither method for immutability.
    /// </summary>
    public EventSymbol WithSourceInterface(TypeReference? sourceInterface)
    {
        return this with { SourceInterface = sourceInterface };
    }
}
