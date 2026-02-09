using tsbindgen.Core;

namespace tsbindgen.Emit.Shared;

/// <summary>
/// Utilities for TypeScript identifier sanitization.
/// NOTE: This is a SANITIZER only - it does NOT apply naming transforms (camelCase/PascalCase).
/// For final member names, use ctx.Renamer.GetFinalMemberName() which applies the policy transform.
/// </summary>
public static class NameUtilities
{
    /// <summary>
    /// Sanitize a TypeScript identifier by escaping reserved words and invalid characters.
    /// This is the FINAL step after getting the name from Renamer.
    ///
    /// IMPORTANT: This does NOT apply naming transforms (camelCase/PascalCase).
    /// The Renamer applies transforms during name reservation.
    /// </summary>
    public static string SanitizeTsIdentifier(string name)
    {
        return SanitizeIdentifier(name);
    }

    /// <summary>
    /// Sanitize a TypeScript identifier by appending '_' if it's a reserved word.
    /// </summary>
    private static string SanitizeIdentifier(string name)
    {
        var result = TypeScriptReservedWords.SanitizeTypeName(name);
        return result.Sanitized;
    }

    /// <summary>
    /// Check if the renamed name is a non-numeric override of the CLR name.
    /// Returns true if Renamer applied a semantic override (not a numeric suffix).
    /// </summary>
    private static bool IsNonNumericOverride(string clrName, string renamedName)
    {
        // Same name - no override
        if (clrName == renamedName)
            return false;

        // Check if renamed name ends with just digits (e.g., equals2, equals3)
        // Pattern: originalName + one or more digits
        if (renamedName.StartsWith(clrName) && renamedName.Length > clrName.Length)
        {
            var suffix = renamedName.Substring(clrName.Length);
            // If suffix is all digits, this is a numeric override (ignore it)
            if (suffix.All(char.IsDigit))
                return false;
        }

        // Otherwise this is a semantic override (e.g., ToString -> ToString_)
        return true;
    }

    /// <summary>
    /// Check if a name ends with a numeric suffix (for PhaseGate validation).
    /// </summary>
    public static bool HasNumericSuffix(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        // Check if name ends with one or more digits
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i]))
            i--;

        // If we found digits at the end
        return i < name.Length - 1;
    }

    /// <summary>
    /// Generate the nominal branding property name for a CLR interface type.
    ///
    /// This is intentionally derived from the CLR full name (not the TS emit name) so the
    /// brand is deterministic and stable across renaming transforms and conflict suffixes.
    ///
    /// Example:
    ///   "System.Collections.Generic.IAsyncEnumerable`1"
    ///     → "__tsonic_iface_System_Collections_Generic_IAsyncEnumerable_1"
    /// </summary>
    public static string GetClrInterfaceBrandPropertyName(string clrFullName)
    {
        if (string.IsNullOrWhiteSpace(clrFullName))
            return "__tsonic_iface_";

        // Strip any assembly qualification if present (some callers may pass it through).
        var commaIndex = clrFullName.IndexOf(',');
        if (commaIndex >= 0)
            clrFullName = clrFullName.Substring(0, commaIndex).Trim();

        // CLR naming quirks:
        // - Namespace/type separators: "."
        // - Nested types: "+"
        // - Generic arity: "`N" (e.g., IEnumerable`1)
        // Convert all of these into a stable TS identifier component.
        var sb = new System.Text.StringBuilder(clrFullName.Length);
        foreach (var ch in clrFullName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (ch is '.' or '+' or '`')
            {
                sb.Append('_');
                continue;
            }

            // Fallback for any unexpected character.
            sb.Append('_');
        }

        return "__tsonic_iface_" + sb;
    }

    /// <summary>
    /// Generate the nominal branding property name for a CLR class/struct type.
    ///
    /// This is intentionally derived from the CLR full name (not the TS emit name) so the
    /// brand is deterministic and stable across renaming transforms and conflict suffixes.
    ///
    /// Example:
    ///   "System.Linq.ParallelQuery`1"
    ///     → "__tsonic_type_System_Linq_ParallelQuery_1"
    /// </summary>
    public static string GetClrTypeBrandPropertyName(string clrFullName)
    {
        if (string.IsNullOrWhiteSpace(clrFullName))
            return "__tsonic_type_";

        // Strip any assembly qualification if present (some callers may pass it through).
        var commaIndex = clrFullName.IndexOf(',');
        if (commaIndex >= 0)
            clrFullName = clrFullName.Substring(0, commaIndex).Trim();

        var sb = new System.Text.StringBuilder(clrFullName.Length);
        foreach (var ch in clrFullName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (ch is '.' or '+' or '`')
            {
                sb.Append('_');
                continue;
            }

            sb.Append('_');
        }

        return "__tsonic_type_" + sb;
    }
}
