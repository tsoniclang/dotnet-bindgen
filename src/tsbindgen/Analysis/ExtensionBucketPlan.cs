using System.Collections.Immutable;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;

namespace tsbindgen.Analysis;

/// <summary>
/// Key for grouping extension methods by their target type.
/// Uses the generic definition (not instantiated type) to group all variants together.
/// Example: IEnumerable&lt;int&gt; and IEnumerable&lt;string&gt; both map to (System.Collections.Generic.IEnumerable, 1)
/// </summary>
public sealed record ExtensionTargetKey
{
    /// <summary>
    /// CLR full name of the target type's generic definition.
    /// Example: "System.Collections.Generic.IEnumerable"
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Generic arity of the target type.
    /// Example: 1 for IEnumerable&lt;T&gt;, 2 for Dictionary&lt;TKey, TValue&gt;, 0 for string
    /// </summary>
    public required int Arity { get; init; }

    /// <summary>
    /// Equality based on FullName and Arity.
    /// </summary>
    public bool Equals(ExtensionTargetKey? other)
    {
        if (other is null) return false;
        return FullName == other.FullName && Arity == other.Arity;
    }

    /// <summary>
    /// Hash code based on FullName and Arity.
    /// </summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(FullName, Arity);
    }
}

/// <summary>
/// Plan for emitting a single bucket interface containing all extension methods for a specific target type.
/// Buckets are scoped by the declaring namespace of the extension methods (C#-like using semantics).
/// Example: __Ext_System_Linq_IEnumerable_1&lt;T&gt; containing Where, Select, etc.
/// </summary>
public sealed record ExtensionBucketPlan
{
    /// <summary>
    /// The key identifying this target type (FullName + Arity).
    /// </summary>
    public required ExtensionTargetKey Key { get; init; }

    /// <summary>
    /// Namespace that declares these extension methods (namespace of the static class).
    /// This is the scoping boundary for extension method availability.
    /// </summary>
    public required string DeclaringNamespace { get; init; }

    /// <summary>
    /// The target type symbol (generic definition).
    /// Example: IEnumerable&lt;T&gt; type symbol
    /// </summary>
    public required TypeSymbol TargetType { get; init; }

    /// <summary>
    /// All extension methods that target this type.
    /// Includes methods from all namespaces/assemblies.
    /// </summary>
    public required ImmutableArray<MethodSymbol> Methods { get; init; }

    /// <summary>
    /// TypeScript name for the bucket interface.
    /// Example: "__Ext_System_Linq_IEnumerable_1" for IEnumerable&lt;T&gt; extensions declared in System.Linq
    /// Generated from DeclaringNamespace + TargetType.TsEmitName.
    /// </summary>
    public string BucketInterfaceName =>
        $"__Ext_{(string.IsNullOrEmpty(DeclaringNamespace) ? "_root" : DeclaringNamespace.Replace('.', '_'))}_{TargetType.TsEmitName}";
}

/// <summary>
/// Complete plan for all extension method buckets across all target types.
/// </summary>
public sealed record ExtensionMethodsPlan
{
    /// <summary>
    /// All bucket plans, one per target type that has extension methods.
    /// </summary>
    public required ImmutableArray<ExtensionBucketPlan> Buckets { get; init; }

    /// <summary>
    /// Total number of extension methods across all buckets.
    /// </summary>
    public int TotalMethodCount => Buckets.Sum(b => b.Methods.Length);

    /// <summary>
    /// Empty plan (no extension methods).
    /// </summary>
    public static ExtensionMethodsPlan Empty { get; } = new ExtensionMethodsPlan
    {
        Buckets = ImmutableArray<ExtensionBucketPlan>.Empty
    };
}
