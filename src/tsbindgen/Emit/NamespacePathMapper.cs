namespace tsbindgen.Emit;

/// <summary>
/// Maps CLR namespace names to output directory/file names.
/// Centralizes the namespace-to-path transformation logic used by all emitters.
/// </summary>
public static class NamespacePathMapper
{
    /// <summary>
    /// Get the output directory/file name for a CLR namespace.
    /// If a mapping is configured, returns the mapped name.
    /// Otherwise, returns the CLR namespace name unchanged.
    /// </summary>
    /// <param name="clrNamespace">The CLR namespace (e.g., "nodejs")</param>
    /// <param name="ctx">Build context containing the policy with namespace mappings</param>
    /// <returns>The output name to use for files/directories (e.g., "index")</returns>
    public static string GetOutputName(string clrNamespace, BuildContext ctx)
    {
        // Check for explicit mapping in policy
        if (ctx.Policy.Emission.NamespaceMappings.TryGetValue(clrNamespace, out var mapped))
        {
            return mapped;
        }

        // Default: use CLR namespace name as-is
        return clrNamespace;
    }

    /// <summary>
    /// Get the output directory/file name for a namespace symbol.
    /// Convenience overload that extracts the name from the symbol.
    /// </summary>
    /// <param name="ns">The namespace symbol</param>
    /// <param name="ctx">Build context containing the policy with namespace mappings</param>
    /// <returns>The output name to use for files/directories</returns>
    public static string GetOutputName(Model.Symbols.NamespaceSymbol ns, BuildContext ctx)
    {
        return GetOutputName(ns.Name, ctx);
    }
}
