using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using DotnetBindgen.Model;

namespace DotnetBindgen.Load;

/// <summary>
/// Result of loading transitive closure of assemblies.
/// </summary>
public sealed record LoadClosureResult(
    MetadataLoadContext LoadContext,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyDictionary<AssemblyKey, string> ResolvedPaths,
    IReadOnlyDictionary<AssemblyKey, IReadOnlyList<AssemblyKey>> References);

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

    private readonly record struct AssemblyIdentityKey(
        string Name,
        string PublicKeyToken,
        string Culture);

    private sealed record CandidateAssembly(
        AssemblyKey Key,
        Version Version,
        string Path);

    private static AssemblyIdentityKey IdentityOf(AssemblyKey key) =>
        new(key.Name, key.PublicKeyToken, key.Culture);

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
        var effectiveRefPaths = refPaths
            .Concat(GetRuntimeReferenceDirectories())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ctx.Log("AssemblyLoader", "=== Loading Transitive Closure ===");
        _ctx.Log("AssemblyLoader", $"Seed assemblies: {seedPaths.Count}");
        _ctx.Log("AssemblyLoader", $"Reference paths: {effectiveRefPaths.Length}");

        // Phase 1: Build candidate map from ref paths
        var candidateMap = BuildCandidateMap(effectiveRefPaths);
        _ctx.Log("AssemblyLoader", $"Candidate assemblies discovered: {candidateMap.Count}");

        // Phase 2: BFS closure resolution
        var (resolvedPaths, references) = ResolveClosure(seedPaths, candidateMap, strictVersions);
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

        return new LoadClosureResult(loadContext, assemblies, resolvedPaths, references);
    }

    private static IEnumerable<string> GetRuntimeReferenceDirectories()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is not null && Directory.Exists(runtimeDir))
        {
            yield return runtimeDir;
        }
    }

    /// <summary>
    /// Build map of available assemblies from reference directories.
    /// Maps (Name, PKT, Culture) → candidate assemblies (for version selection).
    /// </summary>
    private Dictionary<AssemblyIdentityKey, List<CandidateAssembly>> BuildCandidateMap(IReadOnlyList<string> refPaths)
    {
        var candidateMap = new Dictionary<AssemblyIdentityKey, List<CandidateAssembly>>();

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
                    var identity = IdentityOf(key);

                    if (!candidateMap.TryGetValue(identity, out var candidates))
                    {
                        candidates = new List<CandidateAssembly>();
                        candidateMap[identity] = candidates;
                    }

                    if (!Version.TryParse(key.Version, out var ver))
                        continue;

                    candidates.Add(new CandidateAssembly(key, ver, dllPath));
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
    /// Returns map of AssemblyKey → resolved file path.
    ///
    /// Important invariant: for each (Name, PKT, Culture) identity, we load exactly one
    /// assembly version into the closure. Version selection is deterministic:
    /// - Prefer exact match
    /// - Otherwise, allow minor/patch roll-forward within the same major version
    /// - If strictVersions=false, allow best-effort selection across majors (warn)
    /// </summary>
    private (
        Dictionary<AssemblyKey, string> ResolvedPaths,
        Dictionary<AssemblyKey, IReadOnlyList<AssemblyKey>> References
    ) ResolveClosure(
        IReadOnlyList<string> seedPaths,
        Dictionary<AssemblyIdentityKey, List<CandidateAssembly>> candidateMap,
        bool strictVersions)
    {
        static CandidateAssembly? PickCandidate(
            IReadOnlyList<CandidateAssembly> candidates,
            Version requiredVersion,
            bool strictVersions,
            BuildContext ctx,
            AssemblyIdentityKey identity,
            string? requestedBy)
        {
            // Prefer exact match.
            var exact = candidates.FirstOrDefault(c => c.Version == requiredVersion);
            if (exact is not null)
                return exact;

            // Strict mode: allow only same-major roll-forward (pick closest >= required).
            if (strictVersions)
            {
                var sameMajor = candidates
                    .Where(c => c.Version.Major == requiredVersion.Major)
                    .OrderBy(c => c.Version)
                    .ToList();

                var rollForward = sameMajor.FirstOrDefault(c => c.Version >= requiredVersion);
                if (rollForward is not null)
                {
                    return rollForward;
                }

                // If we have candidates for the same major but all are lower, this is a hard failure.
                if (sameMajor.Count > 0)
                {
                    var available = string.Join(", ", sameMajor.Select(c => c.Version.ToString()));
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                        $"No candidate for '{identity.Name}' satisfies required version {requiredVersion} (requested by {requestedBy ?? "unknown"}). " +
                        $"Available (major {requiredVersion.Major}): {available}");
                    return null;
                }

                // Major drift: strict mode forbids selecting a different major.
                var majors = candidates.Select(c => c.Version.Major).Distinct().OrderBy(m => m).ToArray();
                ctx.Diagnostics.Error(
                    Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                    $"Assembly '{identity.Name}' requires major version {requiredVersion.Major} but only majors [{string.Join(", ", majors)}] were found " +
                    $"(requested by {requestedBy ?? "unknown"}).");
                return null;
            }

            // Non-strict mode: pick highest available version (warn on major drift).
            var best = candidates
                .OrderByDescending(c => c.Version)
                .FirstOrDefault();

            if (best is null)
                return null;

            if (best.Version.Major != requiredVersion.Major)
            {
                ctx.Diagnostics.Warning(
                    Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                    $"Assembly '{identity.Name}' requested {requiredVersion} but resolved to {best.Version} (major drift) " +
                    $"(requested by {requestedBy ?? "unknown"}).");
            }

            return best;
        }

        var queue = new Queue<AssemblyIdentityKey>();
        var visited = new HashSet<AssemblyIdentityKey>();
        var resolved = new Dictionary<AssemblyIdentityKey, CandidateAssembly>();
        var required = new Dictionary<AssemblyIdentityKey, Version>();
        var directRefs = new Dictionary<AssemblyIdentityKey, List<AssemblyKey>>();

        foreach (var seedPath in seedPaths)
        {
            try
            {
                var seedName = AssemblyName.GetAssemblyName(seedPath);
                var seedKey = AssemblyKey.From(seedName);
                var seedIdentity = IdentityOf(seedKey);

                if (!Version.TryParse(seedKey.Version, out var seedVersion))
                    continue;

                // Airplane-grade: seeds define reality. Conflicting seed versions are a hard error.
                if (resolved.TryGetValue(seedIdentity, out var existingSeed))
                {
                    if (existingSeed.Version != seedVersion)
                    {
                        _ctx.Diagnostics.Error(
                            Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                            $"Conflicting seed assemblies for '{seedKey.Name}': {existingSeed.Version} vs {seedVersion}");
                    }
                    continue;
                }

                resolved[seedIdentity] = new CandidateAssembly(seedKey, seedVersion, seedPath);
                required[seedIdentity] = seedVersion;
                queue.Enqueue(seedIdentity);
            }
            catch (Exception ex)
            {
                _ctx.Log("AssemblyLoader", $"  Warning: Could not read seed {Path.GetFileName(seedPath)}: {ex.Message}");
            }
        }

        while (queue.Count > 0)
        {
            var currentIdentity = queue.Dequeue();

            if (!resolved.TryGetValue(currentIdentity, out var currentAsm))
                continue;

            // Skip if already visited (we only enqueue when we first resolve or upgrade).
            if (visited.Contains(currentIdentity))
                continue;

            visited.Add(currentIdentity);

            var currentPath = currentAsm.Path;

            // Load assembly to get references (lightweight - just metadata)
            try
            {
                var currentRefs = new List<AssemblyKey>();
                using var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var peReader = new System.Reflection.PortableExecutable.PEReader(fs);
                var metadataReader = peReader.GetMetadataReader();

                // Walk assembly references
                foreach (var refHandle in metadataReader.AssemblyReferences)
                {
                    var reference = metadataReader.GetAssemblyReference(refHandle);
                    var refName = metadataReader.GetString(reference.Name);
                    var refCulture = reference.Culture.IsNil ? "" : metadataReader.GetString(reference.Culture);
                    var refToken = reference.PublicKeyOrToken.IsNil
                        ? "null"
                        : BitConverter.ToString(metadataReader.GetBlobBytes(reference.PublicKeyOrToken)).Replace("-", "").ToLowerInvariant();

                    var refIdentity = new AssemblyIdentityKey(refName, refToken, refCulture);
                    var requestedVersion = reference.Version;

                    currentRefs.Add(new AssemblyKey(refName, refToken, refCulture, requestedVersion.ToString()));

                    if (!required.TryGetValue(refIdentity, out var requiredVersion))
                    {
                        requiredVersion = requestedVersion;
                    }
                    else
                    {
                        // Major drift detection at requirement level.
                        if (requiredVersion.Major != requestedVersion.Major)
                        {
                            if (strictVersions)
                            {
                                _ctx.Diagnostics.Error(
                                    Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                                    $"Assembly '{refName}' referenced with multiple major versions: {requiredVersion} vs {requestedVersion} (requested by {currentAsm.Key.Name})");
                                continue;
                            }

                            _ctx.Diagnostics.Warning(
                                Core.Diagnostics.DiagnosticCodes.VersionDriftForSameIdentity,
                                $"Assembly '{refName}' referenced with multiple major versions: {requiredVersion} vs {requestedVersion} (requested by {currentAsm.Key.Name})");
                        }

                        if (requestedVersion > requiredVersion)
                        {
                            requiredVersion = requestedVersion;
                        }
                    }

                    required[refIdentity] = requiredVersion;

                    // Look up in candidate map
                    if (!candidateMap.TryGetValue(refIdentity, out var candidates) || candidates.Count == 0)
                    {
                        // PG_LOAD_001: External reference not in candidate set
                        // This will be caught by PhaseGate validation later
                        continue;
                    }

                    var chosen = PickCandidate(
                        candidates,
                        requiredVersion,
                        strictVersions,
                        _ctx,
                        refIdentity,
                        currentAsm.Key.Name);

                    if (chosen is null)
                        continue;

                    // If already resolved, upgrade if needed to satisfy the (max) required version.
                    if (resolved.TryGetValue(refIdentity, out var existing))
                    {
                        if (existing.Version >= requiredVersion)
                            continue;

                        // Attempt to roll-forward to satisfy the requirement.
                        resolved[refIdentity] = chosen;
                        visited.Remove(refIdentity); // force reprocess with new version
                        queue.Enqueue(refIdentity);
                        continue;
                    }

                    resolved[refIdentity] = chosen;
                    queue.Enqueue(refIdentity);
                }

                directRefs[currentIdentity] = currentRefs;
            }
            catch (Exception ex)
            {
                _ctx.Log("AssemblyLoader", $"  Warning: Could not read references from {Path.GetFileName(currentPath)}: {ex.Message}");
            }
        }

        var resolvedPaths = resolved.Values.ToDictionary(v => v.Key, v => v.Path);

        var refsOut = new Dictionary<AssemblyKey, IReadOnlyList<AssemblyKey>>();
        foreach (var (identity, asm) in resolved)
        {
            refsOut[asm.Key] = directRefs.TryGetValue(identity, out var refs)
                ? refs
                : Array.Empty<AssemblyKey>();
        }

        return (resolvedPaths, refsOut);
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
