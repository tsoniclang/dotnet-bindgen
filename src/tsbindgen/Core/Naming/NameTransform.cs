namespace tsbindgen.Core.Naming;

/// <summary>
/// Applies naming style transformations to identifiers.
/// </summary>
public static class NameTransform
{
    /// <summary>
    /// Apply JS-style naming to a C# member name.
    ///
    /// Only transforms pure C# PascalCase members into JS-style lowerFirst:
    ///   "Hello" → "hello"
    ///   "HelloWorld" → "helloWorld"
    ///   "GetValue" → "getValue"
    ///   "HTTPSConnection" → "httpsConnection"
    ///
    /// Leaves everything else unchanged (intentionally):
    ///   - ALL-UPPERCASE: "HELLO" → "HELLO", "CC_CDECL" → "CC_CDECL"
    ///   - Underscores: "Hello_World" → "Hello_World"
    ///   - CLR-reserved: "value__" → "value__" (anything ending with __)
    /// </summary>
    public static string ToJsStyle(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Leave CLR-reserved patterns unchanged (ending with __)
        if (name.EndsWith("__"))
            return name;

        // Leave names with underscores unchanged
        if (name.Contains('_'))
            return name;

        // Leave ALL-UPPERCASE names unchanged
        if (IsAllUpperCase(name))
            return name;

        // Transform PascalCase to lowerFirst (JS-style)
        return ToLowerFirst(name);
    }

    /// <summary>
    /// Convert PascalCase name to lowerFirst (JS-style camelCase).
    /// Handles leading acronyms: "HTTPSConnection" → "httpsConnection"
    /// </summary>
    private static string ToLowerFirst(string name)
    {
        if (name.Length == 1)
            return char.ToLowerInvariant(name[0]).ToString();

        // Find where the "first word" ends
        // For "GetValue" → lowercase 'G' → "getValue"
        // For "HTTPSConnection" → lowercase "HTTPS" → "httpsConnection"
        var i = 0;

        // Count consecutive uppercase letters at the start
        while (i < name.Length && char.IsUpper(name[i]))
        {
            i++;
        }

        if (i == 0)
        {
            // Already starts with lowercase - return as-is
            return name;
        }

        if (i == 1)
        {
            // Single uppercase letter at start: "GetValue" → "getValue"
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        if (i == name.Length)
        {
            // Entire name is uppercase (but not ALL-CAPS pattern, since that was caught earlier)
            // This shouldn't happen for pure PascalCase, but handle it
            return name.ToLowerInvariant();
        }

        // Multiple uppercase at start: "HTTPSConnection"
        // Lowercase all but the last uppercase (which starts the next word)
        // "HTTPSConnection" → "httpsConnection" (lowercase "HTTPS", keep "C" for "Connection")
        return name[..(i - 1)].ToLowerInvariant() + name[(i - 1)..];
    }

    /// <summary>
    /// Check if a name is ALL-UPPERCASE (only uppercase letters and digits).
    /// </summary>
    private static bool IsAllUpperCase(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsLetter(ch) && !char.IsUpper(ch))
                return false;
        }
        return true;
    }
}
