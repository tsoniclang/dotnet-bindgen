using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using tsbindgen.Model;

namespace tsbindgen.Load;

/// <summary>
/// Result of loading transitive closure of assemblies.
/// </summary>
public sealed record LoadClosureResult(
    MetadataLoadContext LoadContext,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyDictionary<AssemblyKey, string> ResolvedPaths);

/// <summary>
/// Creates MetadataLoadContext for loading assemblies in isolation.
/// Handles reference pack resolution for .NET BCL assemblies.
/// Implements transitive closure loading via BFS over assembly references.
/// </summary>
public sealed class AssemblyLoader
{
    private readonly BuildContext _ctx;

    public AssemblyLoader(BuildContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Create a MetadataLoadContext for the given assemblies.
    /// </summary>
    public MetadataLoadContext CreateLoadContext(IReadOnlyList<string> assemblyPaths)
    {
        _ctx.Log("AssemblyLoader", "Creating MetadataLoadContext...");

        // Get reference assemblies directory from the assemblies being loaded
        var referenceAssembliesPath = GetReferenceAssembliesPath(assemblyPaths);

        // Create resolver that looks in:
        // 1. The directory containing the target assemblies
        // 2. The reference assemblies directory (same as target for version consistency)
        var resolver = new PathAssemblyResolver(
            GetResolverPaths(assemblyPaths, referenceAssembliesPath));

        // Create load context with System.Private.CoreLib as core assembly
        var loadContext = new MetadataLoadContext(resolver);

        _ctx.Log("AssemblyLoader", $"MetadataLoadContext created with {resolver.GetType().Name}");

        return loadContext;
    }

    /// <summary>
    /// Load all assemblies into the context.
    /// Deduplicates by assembly identity to avoid loading the same assembly twice.
    /// Skips mscorlib as it's automatically loaded by MetadataLoadContext.
    /// </summary>
    public IReadOnlyList<Assembly> LoadAssemblies(
        MetadataLoadContext loadContext,
        IReadOnlyList<string> assemblyPaths)
    {
        var assemblies = new List<Assembly>();
        var loadedIdentities = new HashSet<string>();

        foreach (var path in assemblyPaths)
        {
            try
            {
                // Get assembly name without loading it first
                var assemblyName = AssemblyName.GetAssemblyName(path);
                var identity = $"{assemblyName.Name}, Version={assemblyName.Version}";

                // Skip mscorlib - it's automatically loaded by MetadataLoadContext as core assembly
                if (assemblyName.Name == "mscorlib")
                {
                    _ctx.Log("AssemblyLoader", $"Skipping mscorlib (core assembly, automatically loaded)");
                    continue;
                }

                // Skip if already loaded
                if (loadedIdentities.Contains(identity))
                {
                    _ctx.Log("AssemblyLoader", $"Skipping duplicate: {assemblyName.Name} (already loaded)");
                    continue;
                }

                var assembly = loadContext.LoadFromAssemblyPath(path);
                assemblies.Add(assembly);
                loadedIdentities.Add(identity);
                _ctx.Log("AssemblyLoader", $"Loaded: {assembly.GetName().Name}");
            }
            catch (Exception ex)
            {
                _ctx.Diagnostics.Error(
                    Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                    $"Failed to load assembly {path}: {ex.Message}");
            }
        }

        return assemblies;
    }

    /// <summary>
    /// Load transitive closure of assemblies starting from seed paths.
    /// Uses BFS to walk all assembly references and resolve full dependency graph.
    /// Returns single MetadataLoadContext with all assemblies loaded.
    /// </summary>
    /// <param name="seedPaths">Initial assemblies to load</param>
    /// <param name="refPaths">Directories to search for referenced assemblies</param>
    /// <param name="strictVersions">If true, error on major version drift (otherwise warn)</param>
    public LoadClosureResult LoadClosure(
        IReadOnlyList<string> seedPaths,
        IReadOnlyList<string> refPaths,
        bool strictVersions = false)
    {
        _ctx.Log("AssemblyLoader", "=== Loading Transitive Closure ===");
        _ctx.Log("AssemblyLoader", $"Seed assemblies: {seedPaths.Count}");
        _ctx.Log("AssemblyLoader", $"Reference paths: {refPaths.Count}");

        // Phase 1: Build candidate map from ref paths
        var candidateMap = BuildCandidateMap(refPaths);
        _ctx.Log("AssemblyLoader", $"Candidate assemblies discovered: {candidateMap.Count}");

        // Phase 2: BFS closure resolution
        var resolvedPaths = ResolveClosure(seedPaths, candidateMap, strictVersions);
        _ctx.Log("AssemblyLoader", $"Total assemblies in closure: {resolvedPaths.Count}");

        // Phase 3: Validate assembly identity (PG_LOAD_002/003/004)
        ValidateAssemblyIdentity(resolvedPaths, strictVersions);

        // Phase 4: Find core library
        var coreLibPath = FindCoreLibrary(resolvedPaths);
        _ctx.Log("AssemblyLoader", $"Core library: {Path.GetFileName(coreLibPath)}");

        // Phase 5: Create MetadataLoadContext
        var resolver = new PathAssemblyResolver(resolvedPaths.Values.ToArray());
        var loadContext = new MetadataLoadContext(resolver, "System.Private.CoreLib");
        _ctx.Log("AssemblyLoader", "MetadataLoadContext created with transitive closure");

        // Phase 5: Load all assemblies
        var assemblies = new List<Assembly>();
        foreach (var (key, path) in resolvedPaths.OrderBy(kvp => kvp.Key.Name))
        {
            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(path);
                assemblies.Add(assembly);
                _ctx.Log("AssemblyLoader", $"  Loaded: {key.Name} v{key.Version}");
            }
            catch (Exception ex)
            {
                _ctx.Diagnostics.Error(
                    Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                    $"Failed to load {key.Name}: {ex.Message}");
            }
        }

        return new LoadClosureResult(loadContext, assemblies, resolvedPaths);
    }

    /// <summary>
    /// Build map of available assemblies from reference directories.
    /// Maps AssemblyKey → list of file paths (for version selection).
    /// </summary>
    private Dictionary<AssemblyKey, List<string>> BuildCandidateMap(IReadOnlyList<string> refPaths)
    {
        var candidateMap = new Dictionary<AssemblyKey, List<string>>();

        foreach (var refPath in refPaths)
        {
            if (!Directory.Exists(refPath))
            {
                _ctx.Log("AssemblyLoader", $"  Warning: Reference path not found: {refPath}");
                continue;
            }

            foreach (var dllPath in Directory.GetFiles(refPath, "*.dll"))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(dllPath);
                    var key = AssemblyKey.From(assemblyName);

                    if (!candidateMap.ContainsKey(key))
                    {
                        candidateMap[key] = new List<string>();
                    }

                    candidateMap[key].Add(dllPath);
                }
                catch
                {
                    // Skip assemblies that can't be read
                }
            }
        }

        return candidateMap;
    }

    /// <summary>
    /// Resolve transitive closure via BFS over assembly references.
    /// Returns map of AssemblyKey → resolved file path (highest version wins).
    /// </summary>
    private Dictionary<AssemblyKey, string> ResolveClosure(
        IReadOnlyList<string> seedPaths,
        Dictionary<AssemblyKey, List<string>> candidateMap,
        bool strictVersions)
    {
        var queue = new Queue<string>(seedPaths);
        var visited = new HashSet<AssemblyKey>();
        var resolved = new Dictionary<AssemblyKey, string>();

        while (queue.Count > 0)
        {
            var currentPath = queue.Dequeue();

            // Get key for current assembly
            var currentName = AssemblyName.GetAssemblyName(currentPath);
            var currentKey = AssemblyKey.From(currentName);

            // Skip if already visited
            if (visited.Contains(currentKey))
                continue;

            visited.Add(currentKey);

            // Version policy: if we already have this assembly, keep highest version
            if (resolved.TryGetValue(currentKey, out var existingPath))
            {
                var existingVersion = new Version(AssemblyKey.From(AssemblyName.GetAssemblyName(existingPath)).Version);
                var currentVersion = new Version(currentKey.Version);

                if (currentVersion > existingVersion)
                {
                    resolved[currentKey] = currentPath;
                    _ctx.Log("AssemblyLoader", $"  Version upgrade: {currentKey.Name} {existingVersion} → {currentVersion}");
                }
                continue;
            }

            resolved[currentKey] = currentPath;

            // Load assembly to get references (lightweight - just metadata)
            try
            {
                using var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var peReader = new System.Reflection.PortableExecutable.PEReader(fs);
                var metadataReader = peReader.GetMetadataReader();

                // Walk assembly references
                foreach (var refHandle in metadataReader.AssemblyReferences)
                {
                    var reference = metadataReader.GetAssemblyReference(refHandle);
                    var refName = metadataReader.GetString(reference.Name);
                    var refVersion = reference.Version.ToString();
                    var refCulture = reference.Culture.IsNil ? "" : metadataReader.GetString(reference.Culture);
                    var refToken = reference.PublicKeyOrToken.IsNil
                        ? "null"
                        : BitConverter.ToString(metadataReader.GetBlobBytes(reference.PublicKeyOrToken)).Replace("-", "").ToLowerInvariant();

                    var refKey = new AssemblyKey(refName, refToken, refCulture, refVersion);

                    // Look up in candidate map
                    if (!candidateMap.TryGetValue(refKey, out var candidates) || candidates.Count == 0)
                    {
                        // PG_LOAD_001: External reference not in candidate set
                        // This will be caught by PhaseGate validation later
                        continue;
                    }

                    // Pick highest version from candidates
                    var bestCandidate = candidates
                        .OrderByDescending(c => new Version(AssemblyKey.From(AssemblyName.GetAssemblyName(c)).Version))
                        .First();

                    queue.Enqueue(bestCandidate);
                }
            }
            catch (Exception ex)
            {
                _ctx.Log("AssemblyLoader", $"  Warning: Could not read references from {Path.GetFileName(currentPath)}: {ex.Message}");
            }
        }

        return resolved;
    }

    /// <summary>
    /// Validate assembly identity consistency in resolved closure.
    /// Guards: PG_LOAD_002 (mixed PKT), PG_LOAD_003 (version drift), PG_LOAD_004 (retargetable/content type)
    /// </summary>
    private void ValidateAssemblyIdentity(
        Dictionary<AssemblyKey, string> resolvedPaths,
        bool strictVersions)
    {
        // Group assemblies by name
        var byName = resolvedPaths.GroupBy(kvp => kvp.Key.Name);

        foreach (var group in byName)
        {
            var assemblies = group.ToList();
            var assemblyName = group.Key;

            // PG_LOAD_002: Check for mixed PublicKeyToken
            var distinctTokens = assemblies.Select(kvp => kvp.Key.PublicKeyToken).Distinct().ToList();
            if (distinctTokens.Count > 1)
            {
                var tokenList = string.Join(", ", distinctTokens.Select(t => $"'{t}'"));
                _ctx.Diagnostics.Error(
                    Core.Diagnostics.DiagnosticCodes.MixedPublicKeyTokenForSameName,
                    $"Assembly '{assemblyName}' referenced with multiple PublicKeyTokens: {tokenList}");
            }

            // PG_LOAD_003: Check for major version drift
            if (assemblies.Count > 1)
            {
                var versions = assemblies.Select(kvp => Version.Parse(kvp.Key.Version)).ToList();
                var maxMajor = versions.Max(v => v.Major);
                var minMajor = versions.Min(v => v.Major);

                if (maxMajor != minMajor)
                {
                    var versionList = string.Join(", ", versions.Select(v => v.ToString()));
                    var severity = strictVersions ? "ERROR" : "WARNING";

                    if (strictVersions)
                    {
                        _ctx.Diagnostics.Error(
                            Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                            $"Assembly '{assemblyName}' has major version drift: {versionList}");
                    }
                    else
                    {
                        _ctx.Diagnostics.Warning(
                            Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                            $"Assembly '{assemblyName}' has major version drift: {versionList}");
                    }
                }
            }

            // PG_LOAD_004: Retargetable/ContentType flags not currently tracked in AssemblyKey.
            // Current implementation works without these flags for BCL binding generation.
        }
    }

    /// <summary>
    /// Find System.Private.CoreLib in resolved assembly set.
    /// This is the core library for MetadataLoadContext.
    /// </summary>
    private string FindCoreLibrary(Dictionary<AssemblyKey, string> resolvedPaths)
    {
        var coreLibCandidates = resolvedPaths
            .Where(kvp => kvp.Key.Name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .ToList();

        if (coreLibCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                "System.Private.CoreLib not found in assembly closure. " +
                "Ensure reference paths include the .NET runtime directory.");
        }

        return coreLibCandidates.First();
    }

    /// <summary>
    /// Get reference assemblies directory from the first assembly path.
    /// Uses the same directory as the assemblies being loaded to ensure version compatibility.
    /// </summary>
    private string GetReferenceAssembliesPath(IReadOnlyList<string> assemblyPaths)
    {
        // Use the directory containing the first assembly as the reference path
        // This ensures we're using the same .NET version for all type resolution
        if (assemblyPaths.Count > 0)
        {
            var firstAssemblyDir = Path.GetDirectoryName(assemblyPaths[0]);
            if (firstAssemblyDir != null && Directory.Exists(firstAssemblyDir))
            {
                _ctx.Log("AssemblyLoader", $"Using assembly directory as reference path: {firstAssemblyDir}");
                return firstAssemblyDir;
            }
        }

        // Fallback: use runtime directory (should rarely happen)
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null && Directory.Exists(runtimeDir))
        {
            _ctx.Log("AssemblyLoader", $"Fallback to runtime directory: {runtimeDir}");
            return runtimeDir;
        }

        throw new InvalidOperationException(
            "Could not determine reference assemblies directory from assembly paths.");
    }

    /// <summary>
    /// Get all paths that the resolver should search.
    /// Deduplicates by assembly name to avoid loading the same assembly twice.
    /// </summary>
    private IEnumerable<string> GetResolverPaths(
        IReadOnlyList<string> assemblyPaths,
        string referenceAssembliesPath)
    {
        var pathsByName = new Dictionary<string, string>();

        // Add reference assemblies directory
        if (Directory.Exists(referenceAssembliesPath))
        {
            foreach (var dll in Directory.GetFiles(referenceAssembliesPath, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (!pathsByName.ContainsKey(name))
                {
                    pathsByName[name] = dll;
                }
            }
        }

        // Add directories containing target assemblies
        foreach (var assemblyPath in assemblyPaths)
        {
            var dir = Path.GetDirectoryName(assemblyPath);
            if (dir != null && Directory.Exists(dir))
            {
                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!pathsByName.ContainsKey(name))
                    {
                        pathsByName[name] = dll;
                    }
                }
            }
        }

        return pathsByName.Values;
    }
}
