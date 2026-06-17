using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotnetBindgen.Model;

namespace DotnetBindgen.Load;

/// <summary>
/// Resolves CLR type names to their declaring assemblies using reflection context.
/// Used for cross-assembly dependency resolution (Fix E).
/// </summary>
public sealed class DeclaringAssemblyResolver
{
    private readonly MetadataLoadContext _loadContext;
    private readonly BuildContext _ctx;
    private readonly Dictionary<string, string?> _cache = new();

    public DeclaringAssemblyResolver(MetadataLoadContext loadContext, BuildContext ctx)
    {
        _loadContext = loadContext;
        _ctx = ctx;
    }

    /// <summary>
    /// Resolves a CLR type full name (backtick form) to its declaring assembly name.
    /// Returns null if type cannot be found in reflection context.
    ///
    /// Example: "System.IO.Stream" → "System.Private.CoreLib"
    /// Example: "System.Collections.Generic.IEnumerable`1" → "System.Private.CoreLib"
    /// </summary>
    public string? ResolveAssembly(string clrFullName)
    {
        // Check cache first
        if (_cache.TryGetValue(clrFullName, out var cachedAssembly))
            return cachedAssembly;

        try
        {
            // Try to load type from reflection context
            // MetadataLoadContext doesn't have a global FindType method,
            // so we need to search through loaded assemblies
            foreach (var assembly in _loadContext.GetAssemblies())
            {
                var type = assembly.GetType(clrFullName, throwOnError: false);
                if (type != null)
                {
                    var assemblyName = assembly.GetName().Name ?? "Unknown";
                    _ctx.Log("AssemblyResolver", $"Resolved '{clrFullName}' → {assemblyName}");
                    _cache[clrFullName] = assemblyName;
                    return assemblyName;
                }
            }

            // Type not found in any loaded assembly
            _ctx.Log("AssemblyResolver", $"Could not resolve '{clrFullName}' (not in any loaded assembly)");
            _cache[clrFullName] = null;
            return null;
        }
        catch (Exception ex)
        {
            // If reflection fails, cache null to avoid repeated attempts
            _ctx.Log("AssemblyResolver", $"Error resolving '{clrFullName}': {ex.Message}");
            _cache[clrFullName] = null;
            return null;
        }
    }

    /// <summary>
    /// Batch resolve multiple CLR keys to assemblies.
    /// Returns dictionary of clrKey → assemblyName (only for successfully resolved types).
    /// </summary>
    public Dictionary<string, string> ResolveBatch(IEnumerable<string> clrKeys)
    {
        var results = new Dictionary<string, string>();

        foreach (var key in clrKeys)
        {
            var assembly = ResolveAssembly(key);
            if (assembly != null)
            {
                results[key] = assembly;
            }
        }

        _ctx.Log("AssemblyResolver", $"Batch resolved {results.Count} / {clrKeys.Count()} types");

        return results;
    }

    /// <summary>
    /// Groups resolved types by assembly name.
    /// </summary>
    public Dictionary<string, List<string>> GroupByAssembly(Dictionary<string, string> resolvedTypes)
    {
        return resolvedTypes
            .GroupBy(kv => kv.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(kv => kv.Key).ToList());
    }
}
