using tsbindgen.Emit;

namespace tsbindgen.Plan;

/// <summary>
/// Plans module specifiers for TypeScript imports.
/// Generates relative paths based on source/target namespaces and emission area.
/// Handles root namespace (_root) and nested namespace directories.
/// </summary>
public static class PathPlanner
{
    /// <summary>
    /// Gets the module specifier for importing from targetNamespace into sourceNamespace.
    /// Uses namespace mapping from BuildContext to resolve output names.
    /// Returns a relative path string suitable for TypeScript import statements.
    /// </summary>
    /// <param name="ctx">Build context containing namespace mappings</param>
    /// <param name="sourceNamespace">The CLR namespace doing the importing (empty string for root)</param>
    /// <param name="targetNamespace">The CLR namespace being imported from (empty string for root)</param>
    /// <returns>Relative module path including .js extension (e.g., "../../System/internal/index.js")</returns>
    public static string GetSpecifier(BuildContext ctx, string sourceNamespace, string targetNamespace)
    {
        // Map CLR namespace names to output names
        var sourceOutput = string.IsNullOrEmpty(sourceNamespace)
            ? sourceNamespace
            : NamespacePathMapper.GetOutputName(sourceNamespace, ctx);
        var targetOutput = string.IsNullOrEmpty(targetNamespace)
            ? targetNamespace
            : NamespacePathMapper.GetOutputName(targetNamespace, ctx);

        return GetSpecifier(sourceOutput, targetOutput);
    }

    /// <summary>
    /// Gets the module specifier for importing from targetNamespace into sourceNamespace.
    /// Returns a relative path string suitable for TypeScript import statements.
    /// NOTE: This overload uses raw names without namespace mapping. Prefer the overload
    /// with BuildContext when namespace mapping is configured.
    /// </summary>
    /// <param name="sourceNamespace">The namespace doing the importing (empty string for root)</param>
    /// <param name="targetNamespace">The namespace being imported from (empty string for root)</param>
    /// <returns>Relative module path including .js extension (e.g., "../../System/internal/index.js")</returns>
    public static string GetSpecifier(string sourceNamespace, string targetNamespace)
    {
        var isSourceRoot = string.IsNullOrEmpty(sourceNamespace);
        var isTargetRoot = string.IsNullOrEmpty(targetNamespace);

        // All imports target internal/index.js (or _root/index.js for root namespace)
        // Calculate the relative path from the source namespace's internal file location
        return (isSourceRoot, isTargetRoot) switch
        {
            // _root/index.d.ts → ../{target}/internal/index.js
            (true, false) => $"../{targetNamespace}/internal/index.js",

            // _root/index.d.ts → ./index.js (self) - not normally used, but keep consistent
            (true, true) => "./index.js",

            // {Namespace}/internal/index.d.ts → ../../_root/index.js
            (false, true) => "../../_root/index.js",

            // {Namespace}/internal/index.d.ts → ../../{target}/internal/index.js
            (false, false) => $"../../{targetNamespace}/internal/index.js"
        };
    }

    /// <summary>
    /// Gets the module specifier for importing from a facade file into another facade file.
    /// Facade files are at the root level (e.g., System.d.ts, System.Collections.Generic.d.ts).
    /// </summary>
    /// <param name="ctx">Build context containing namespace mappings</param>
    /// <param name="sourceNamespace">The CLR namespace doing the importing</param>
    /// <param name="targetNamespace">The CLR namespace being imported from</param>
    /// <returns>Relative module path to the facade file (e.g., "./System.js")</returns>
    public static string GetFacadeSpecifier(BuildContext ctx, string sourceNamespace, string targetNamespace)
    {
        // Map CLR namespace name to output name
        var targetOutput = string.IsNullOrEmpty(targetNamespace)
            ? "_root"
            : NamespacePathMapper.GetOutputName(targetNamespace, ctx);

        // Facade files are at root level, so it's just ./{targetName}.js
        return $"./{targetOutput}.js";
    }

    /// <summary>
    /// Gets the directory name for a namespace (handles root namespace).
    /// </summary>
    public static string GetNamespaceDirectory(string namespaceName)
    {
        return string.IsNullOrEmpty(namespaceName) ? "_root" : namespaceName;
    }

    /// <summary>
    /// Gets the subdirectory name for internal declarations (handles root namespace).
    /// </summary>
    public static string GetInternalSubdirectory(string namespaceName)
    {
        return string.IsNullOrEmpty(namespaceName) ? "_root" : "internal";
    }
}
