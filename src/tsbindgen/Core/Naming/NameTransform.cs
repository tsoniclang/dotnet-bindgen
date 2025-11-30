using System.Text;
using tsbindgen.Core.Policy;

namespace tsbindgen.Core.Naming;

/// <summary>
/// Applies name transformations (camelCase, PascalCase, etc.) to identifiers.
/// </summary>
public static class NameTransform
{
    /// <summary>
    /// Apply the configured transformation strategy to a name.
    /// </summary>
    public static string Apply(string name, NameTransformStrategy strategy)
    {
        return strategy switch
        {
            NameTransformStrategy.None => name,
            NameTransformStrategy.CamelCase => ToCamelCase(name),
            NameTransformStrategy.PascalCase => ToPascalCase(name),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
    }

    /// <summary>
    /// Convert name to camelCase.
    /// Examples:
    ///   "GetValue" -> "getValue"
    ///   "URL" -> "url"
    ///   "HTTPSConnection" -> "httpsConnection"
    ///   "CC_CDECL" -> "ccCdecl" (underscore-separated)
    ///   "SOME_ENUM_VALUE" -> "someEnumValue"
    /// </summary>
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Handle special cases
        if (name.Length == 1)
            return char.ToLowerInvariant(name[0]).ToString();

        // Check if name contains underscores - handle as underscore-separated
        if (name.Contains('_'))
        {
            return UnderscoreSeparatedToCamelCase(name);
        }

        // Check if it's all uppercase (acronym without underscores)
        if (IsAllUpperCase(name))
            return name.ToLowerInvariant();

        // Find the first lowercase letter or end of consecutive uppercase letters
        var sb = new StringBuilder(name.Length);
        var i = 0;

        // Lowercase consecutive uppercase letters at the start
        while (i < name.Length && char.IsUpper(name[i]))
        {
            // If this is the last character, or the next is lowercase, keep this one uppercase
            // (except if it's the first character)
            if (i > 0 && (i == name.Length - 1 || (i < name.Length - 1 && char.IsLower(name[i + 1]))))
            {
                break;
            }

            sb.Append(char.ToLowerInvariant(name[i]));
            i++;
        }

        // Append the rest
        sb.Append(name[i..]);

        return sb.ToString();
    }

    /// <summary>
    /// Convert underscore-separated name to camelCase.
    /// "CC_CDECL" -> "ccCdecl", "SOME_VALUE" -> "someValue"
    /// </summary>
    private static string UnderscoreSeparatedToCamelCase(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return name;

        var sb = new StringBuilder();

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (string.IsNullOrEmpty(part))
                continue;

            if (i == 0)
            {
                // First part: all lowercase
                sb.Append(part.ToLowerInvariant());
            }
            else
            {
                // Subsequent parts: capitalize first letter, lowercase rest
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    sb.Append(part[1..].ToLowerInvariant());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Convert name to PascalCase.
    /// Examples: "getValue" -> "GetValue", "url" -> "Url"
    /// </summary>
    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        if (name.Length == 1)
            return char.ToUpperInvariant(name[0]).ToString();

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

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
