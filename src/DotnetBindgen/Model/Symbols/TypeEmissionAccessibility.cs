using System.Collections.Generic;

namespace DotnetBindgen.Model.Symbols;

/// <summary>
/// Canonical gate for which CLR types are representable in emitted declarations.
///
/// Policy:
/// - Top-level declarations stay public-only.
/// - Nested declarations may also be emitted when they are externally nameable from
///   protected/public signatures: public, protected, or protected internal.
/// </summary>
public static class TypeEmissionAccessibility
{
    public static bool IsEmittable(TypeSymbol type)
    {
        var isNested = type.DeclaringType != null;
        return IsEmittable(type.Accessibility, isNested);
    }

    public static bool IsEmittable(Accessibility accessibility, bool isNested)
    {
        if (!isNested)
        {
            return accessibility == Accessibility.Public;
        }

        return accessibility is
            Accessibility.Public or
            Accessibility.Protected or
            Accessibility.ProtectedInternal;
    }

    public static IEnumerable<TypeSymbol> EnumerateNamespaceTypes(NamespaceSymbol ns)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var type in ns.Types)
        {
            if (seen.Add(type.StableId.ToString()))
            {
                yield return type;
            }

            foreach (var nested in EnumerateNestedTypes(type, seen))
            {
                yield return nested;
            }
        }
    }

    public static IEnumerable<TypeSymbol> EnumerateEmittableNamespaceTypes(NamespaceSymbol ns)
    {
        foreach (var type in EnumerateNamespaceTypes(ns))
        {
            if (IsEmittable(type))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<TypeSymbol> EnumerateNestedTypes(TypeSymbol type, HashSet<string> seen)
    {
        foreach (var nested in type.NestedTypes)
        {
            if (seen.Add(nested.StableId.ToString()))
            {
                yield return nested;
            }

            foreach (var descendant in EnumerateNestedTypes(nested, seen))
            {
                yield return descendant;
            }
        }
    }
}
