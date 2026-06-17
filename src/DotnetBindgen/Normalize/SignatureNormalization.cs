using System.Linq;
using System.Text;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Normalize;

/// <summary>
/// Creates normalized, canonical signatures for complete member matching.
/// Includes: name, arity, parameter kinds, optionality, static flag, accessor type.
///
/// This is the SINGLE canonical format used across:
/// - BindingEmitter (bindings.json)
/// - StructuralConformance (interface matching)
/// - ViewPlanner (member filtering)
/// </summary>
public static class SignatureNormalization
{
    /// <summary>
    /// Normalize a method signature to canonical form.
    /// Format: "MethodName|signature|static=bool"
    /// Uses the StableId's CanonicalSignature which preserves exact CLR metadata.
    /// CRITICAL: StableId captures raw reflection data before TypeReference normalization.
    /// </summary>
    public static string NormalizeMethod(MethodSymbol method)
    {
        var sb = new StringBuilder();

        // Method name
        sb.Append(method.ClrName);

        // Use the StableId's canonical signature which preserves the exact CLR metadata
        // This avoids issues where TypeReference normalization might lose distinctions
        sb.Append("|");
        sb.Append(method.StableId.CanonicalSignature);

        // Static flag
        sb.Append("|static=");
        sb.Append(method.IsStatic ? "true" : "false");

        return sb.ToString();
    }

    /// <summary>
    /// Normalize a property signature to canonical form.
    /// Format: "PropertyName|signature|static=bool|accessor=get/set/getset"
    /// Uses the StableId's CanonicalSignature which preserves exact CLR metadata.
    /// </summary>
    public static string NormalizeProperty(PropertySymbol property)
    {
        var sb = new StringBuilder();

        // Property name
        sb.Append(property.ClrName);

        // Use the StableId's canonical signature
        sb.Append("|");
        sb.Append(property.StableId.CanonicalSignature);

        // Static flag
        sb.Append("|static=");
        sb.Append(property.IsStatic ? "true" : "false");

        // Accessor type
        sb.Append("|accessor=");
        if (property.HasGetter && property.HasSetter)
            sb.Append("getset");
        else if (property.HasGetter)
            sb.Append("get");
        else if (property.HasSetter)
            sb.Append("set");
        else
            sb.Append("none");

        return sb.ToString();
    }

    /// <summary>
    /// Normalize a field signature to canonical form.
    /// Format: "FieldName|signature|static=bool|const=bool"
    /// Uses the StableId's CanonicalSignature which preserves exact CLR metadata.
    /// </summary>
    public static string NormalizeField(FieldSymbol field)
    {
        var sb = new StringBuilder();

        // Field name
        sb.Append(field.ClrName);

        // Use the StableId's canonical signature
        sb.Append("|");
        sb.Append(field.StableId.CanonicalSignature);

        // Static flag
        sb.Append("|static=");
        sb.Append(field.IsStatic ? "true" : "false");

        // Const flag
        sb.Append("|const=");
        sb.Append(field.IsConst ? "true" : "false");

        return sb.ToString();
    }

    /// <summary>
    /// Normalize an event signature to canonical form.
    /// Format: "EventName|signature|static=bool"
    /// Uses the StableId's CanonicalSignature which preserves exact CLR metadata.
    /// </summary>
    public static string NormalizeEvent(EventSymbol evt)
    {
        var sb = new StringBuilder();

        // Event name
        sb.Append(evt.ClrName);

        // Use the StableId's canonical signature
        sb.Append("|");
        sb.Append(evt.StableId.CanonicalSignature);

        // Static flag
        sb.Append("|static=");
        sb.Append(evt.IsStatic ? "true" : "false");

        return sb.ToString();
    }

    /// <summary>
    /// Normalize a constructor signature to canonical form.
    /// Format: "constructor|signature|static=bool"
    /// Uses the StableId's CanonicalSignature which preserves exact CLR metadata.
    /// </summary>
    public static string NormalizeConstructor(ConstructorSymbol ctor)
    {
        var sb = new StringBuilder();

        // Constructor keyword
        sb.Append("constructor");

        // Use the StableId's canonical signature
        sb.Append("|");
        sb.Append(ctor.StableId.CanonicalSignature);

        // Static flag (static constructors exist)
        sb.Append("|static=");
        sb.Append(ctor.IsStatic ? "true" : "false");

        return sb.ToString();
    }

    /// <summary>
    /// Normalize a TypeReference to canonical form with FULL type arguments.
    /// This is critical for distinguishing overloads like TryFormat(Span&lt;char&gt;) vs TryFormat(Span&lt;byte&gt;).
    /// </summary>
    private static string NormalizeType(TypeReference type)
    {
        switch (type)
        {
            case NamedTypeReference named:
                var sb = new StringBuilder();

                // Base identity: Namespace.Name
                if (!string.IsNullOrEmpty(named.Namespace))
                {
                    sb.Append(named.Namespace);
                    sb.Append('.');
                }
                sb.Append(named.Name.Replace('`', '_')); // List`1 -> List_1

                // Include type arguments recursively
                if (named.TypeArguments.Count > 0)
                {
                    sb.Append('[');
                    sb.Append(string.Join(",", named.TypeArguments.Select(NormalizeType)));
                    sb.Append(']');
                }

                // Mark value types to distinguish from reference types if needed
                if (named.IsValueType)
                    sb.Append("#vt");

                return sb.ToString();

            case NestedTypeReference nested:
                // Use full reference for nested types
                return NormalizeType(nested.FullReference);

            case ArrayTypeReference arr:
                // Array with rank
                var rankStr = arr.Rank == 1 ? "[]" : $"[{new string(',', arr.Rank - 1)}]";
                return $"{NormalizeType(arr.ElementType)}{rankStr}";

            case PointerTypeReference ptr:
                return $"{NormalizeType(ptr.PointeeType)}*";

            case ByRefTypeReference byref:
                // ByRef-ness is encoded separately via parameter refKind
                return NormalizeType(byref.ReferencedType);

            case GenericParameterReference gp:
                // Type parameters: use position to distinguish them
                return $"`{gp.Position}";

            case PlaceholderTypeReference:
                return "?placeholder";

            default:
                // Fallback for opaque/unhandled types
                return type.ToString()?.Replace(" ", "").Replace('`', '_') ?? "<opaque>";
        }
    }

}
