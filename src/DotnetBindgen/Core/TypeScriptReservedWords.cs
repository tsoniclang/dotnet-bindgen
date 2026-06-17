using System.Collections.Generic;

namespace DotnetBindgen.Core;

/// <summary>
/// TypeScript reserved word handling and sanitization.
/// Provides pure functions for detecting and escaping TypeScript keywords.
/// </summary>
public static class TypeScriptReservedWords
{
    // NOTE: TypeScript has multiple identifier contexts. For example:
    // - Binding identifiers (vars/params) disallow JS reserved words like `class`, `export`, `delete`, etc.
    // - Type positions additionally disallow primitive type keywords like `string`, `number`, etc.
    // - Member names (property/method names) allow keywords (IdentifierName), so we must NOT rename them.
    private static readonly HashSet<string> ReservedBindingIdentifiers = new(System.StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "enum", "export", "extends", "false", "finally",
        "for", "function", "if", "import", "in", "instanceof", "new", "null",
        "return", "super", "switch", "this", "throw", "true", "try", "typeof",
        "var", "void", "while", "with", "let"
    };

    // Type positions additionally disallow TypeScript's primitive type keywords as type alias names.
    // Example: `type string = ...` is illegal.
    private static readonly HashSet<string> ReservedTypeNames = new(System.StringComparer.Ordinal)
    {
        // Binding identifiers
        "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "enum", "export", "extends", "false", "finally",
        "for", "function", "if", "import", "in", "instanceof", "new", "null",
        "return", "super", "switch", "this", "throw", "true", "try", "typeof",
        "var", "void", "while", "with", "let",

        // Primitive type keywords
        "any", "unknown", "never", "boolean", "number", "string", "symbol", "object", "bigint"
    };

    /// <summary>
    /// Check if a name is reserved in a BindingIdentifier context (vars/params).
    /// Case-sensitive comparison.
     /// </summary>
    public static bool IsReservedBindingIdentifier(string name)
    {
        return ReservedBindingIdentifiers.Contains(name);
    }

    /// <summary>
    /// Check if a name is reserved in a type name context (e.g., type aliases).
    /// Case-sensitive comparison.
    /// </summary>
    public static bool IsReservedTypeName(string name)
    {
        return ReservedTypeNames.Contains(name);
    }

    /// <summary>
    /// Result of sanitization operation with metadata.
    /// </summary>
    public sealed record SanitizeResult
    {
        /// <summary>
        /// The sanitized identifier, safe for TypeScript emission.
        /// </summary>
        public required string Sanitized { get; init; }

        /// <summary>
        /// Original identifier before sanitization.
        /// </summary>
        public required string Original { get; init; }

        /// <summary>
        /// True if the identifier was modified during sanitization.
        /// </summary>
        public required bool WasSanitized { get; init; }

        /// <summary>
        /// Reason for sanitization (e.g., "ReservedWord").
        /// Null if no sanitization was needed.
        /// </summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Sanitize a type name for TypeScript emission (Identifier context).
    /// Reserved type names get a trailing underscore suffix.
     /// Returns metadata about the sanitization for diagnostics.
     /// </summary>
    public static SanitizeResult SanitizeTypeName(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return new SanitizeResult
            {
                Sanitized = identifier ?? string.Empty,
                Original = identifier ?? string.Empty,
                WasSanitized = false,
                Reason = null
            };
        }

        // Check if it's reserved in a type context
        if (IsReservedTypeName(identifier))
        {
            return new SanitizeResult
            {
                Sanitized = identifier + "_",
                Original = identifier,
                WasSanitized = true,
                Reason = "ReservedWord"
            };
        }

        // Not reserved - no sanitization needed
        return new SanitizeResult
        {
            Sanitized = identifier,
            Original = identifier,
            WasSanitized = false,
            Reason = null
        };
    }

    /// <summary>
    /// Sanitize a BindingIdentifier (vars/params) by appending underscore suffix if it's reserved.
    /// Returns metadata about the sanitization for diagnostics.
    /// </summary>
    public static SanitizeResult SanitizeBindingIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return new SanitizeResult
            {
                Sanitized = identifier ?? string.Empty,
                Original = identifier ?? string.Empty,
                WasSanitized = false,
                Reason = null
            };
        }

        if (IsReservedBindingIdentifier(identifier))
        {
            return new SanitizeResult
            {
                Sanitized = identifier + "_",
                Original = identifier,
                WasSanitized = true,
                Reason = "ReservedBindingIdentifier"
            };
        }

        return new SanitizeResult
        {
            Sanitized = identifier,
            Original = identifier,
            WasSanitized = false,
            Reason = null
        };
    }

    /// <summary>
    /// Member names (property/method identifiers) are emitted in IdentifierName positions
    /// and may legally use keywords (e.g., `delete()`, `export()`).
    /// </summary>
    public static SanitizeResult SanitizeMemberName(string name)
    {
        return new SanitizeResult
        {
            Sanitized = name ?? string.Empty,
            Original = name ?? string.Empty,
            WasSanitized = false,
            Reason = null
        };
    }

    /// <summary>
    /// Sanitize parameter name by appending underscore suffix if it's a reserved BindingIdentifier.
     /// Used for method/constructor parameters.
    /// Example: "switch" → "switch_", "type" → "type"
    /// </summary>
    public static string SanitizeParameterName(string name)
    {
        return IsReservedBindingIdentifier(name) ? $"{name}_" : name;
    }

    /// <summary>
    /// Escape identifier using $$name$$ format for Tsonic.
    /// Used for type/member names in TypeScript declarations.
    /// Example: "switch" → "$$switch$$"
    /// </summary>
    public static string EscapeIdentifier(string name)
    {
        return IsReservedTypeName(name) ? $"$${name}$$" : name;
    }
}
