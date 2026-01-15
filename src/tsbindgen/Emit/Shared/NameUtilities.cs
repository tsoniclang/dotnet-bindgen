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
}
