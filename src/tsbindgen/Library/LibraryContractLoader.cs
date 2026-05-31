using System.Collections.Immutable;
using System.Text.Json;
using tsbindgen.Emit;

namespace tsbindgen.Library;

/// <summary>
/// Loads a LibraryContract from an existing tsbindgen package directory.
/// Reads all bindings.json files to extract type and member StableIds.
/// </summary>
public static class LibraryContractLoader
{
    /// <summary>
    /// Load library contract from a package directory.
    /// </summary>
    /// <param name="packagePath">Path to existing tsbindgen package directory</param>
    /// <returns>Loaded contract</returns>
    /// <exception cref="DirectoryNotFoundException">Package directory not found</exception>
    /// <exception cref="FileNotFoundException">No metadata files or bindings.json found</exception>
    /// <exception cref="InvalidOperationException">Malformed JSON or missing required fields</exception>
    public static LibraryContract Load(string packagePath)
    {
        if (!Directory.Exists(packagePath))
        {
            throw new DirectoryNotFoundException($"Library package directory not found: {packagePath}");
        }

        // Read package name from package.json
        var packageJsonPath = Path.Combine(packagePath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new FileNotFoundException($"No package.json found in library package: {packagePath}");
        }

        var packageName = ReadPackageName(packageJsonPath);

        var allowedTypes = new HashSet<string>();
        var allowedMembers = new HashSet<string>();
        var namespaceToTypes = new Dictionary<string, HashSet<string>>();

        // Load all bindings.json files from namespace subdirectories
        //
        // Airplane-grade: the library contract must reflect ONLY the package's own emitted surface.
        // Repo checkouts often contain `node_modules/` with transitive dependencies that also include
        // bindings.json files; including them would silently merge multiple packages into one contract,
        // causing ambiguous type ownership (e.g. System.Array appears in both @tsonic/dotnet and a
        // consuming generated package).
        //
        // Therefore we enumerate bindings.json files ourselves and skip dependency/infra directories.
        var bindingsFiles = EnumerateBindingsJsonFiles(packagePath).ToArray();

        if (bindingsFiles.Length == 0)
        {
            throw new FileNotFoundException($"No bindings.json files found in library package: {packagePath}");
        }

        // In the unified bindings.json format, "bindings" are defined by the presence of
        // member entries in the bindings.json files. The binding set is therefore the
        // same as the member stable-id set.
        foreach (var bindingsFile in bindingsFiles)
        {
            ProcessBindingsFile(bindingsFile, allowedTypes, allowedMembers, namespaceToTypes);
        }

        // Load families.json if it exists (optional, enables multi-arity facade support)
        var familiesPath = Path.Combine(packagePath, "families.json");
        var facadeFamilies = File.Exists(familiesPath)
            ? LoadFamilies(familiesPath)
            : ImmutableDictionary<string, FacadeFamilyEntry>.Empty;

        // Build derived structures for per-type membership checks
        // AllowedClrFullNames: Extract CLR full name from StableId (format: "AssemblyName:ClrFullName")
        var allowedClrFullNames = new HashSet<string>();
        foreach (var stableId in allowedTypes)
        {
            var colonIndex = stableId.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < stableId.Length - 1)
            {
                allowedClrFullNames.Add(stableId.Substring(colonIndex + 1));
            }
        }

        // ClrFullNameToNamespace: Map each CLR full name to its namespace
        var clrFullNameToNamespace = new Dictionary<string, string>();
        foreach (var (namespaceName, typeStableIds) in namespaceToTypes)
        {
            foreach (var stableId in typeStableIds)
            {
                var colonIndex = stableId.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < stableId.Length - 1)
                {
                    var clrFullName = stableId.Substring(colonIndex + 1);
                    clrFullNameToNamespace[clrFullName] = namespaceName;
                }
            }
        }

        return new LibraryContract
        {
            PackageName = packageName,
            AllowedTypeStableIds = allowedTypes.ToImmutableHashSet(),
            AllowedMemberStableIds = allowedMembers.ToImmutableHashSet(),
            AllowedBindingStableIds = allowedMembers.ToImmutableHashSet(),
            NamespaceToTypes = namespaceToTypes.ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToImmutableHashSet()),
            AllowedClrFullNames = allowedClrFullNames.ToImmutableHashSet(),
            ClrFullNameToNamespace = clrFullNameToNamespace.ToImmutableDictionary(),
            FacadeFamilies = facadeFamilies,
            PackageNames = ImmutableHashSet.Create(StringComparer.Ordinal, packageName),
            ClrFullNameToPackage = allowedClrFullNames
                .ToImmutableDictionary(clr => clr, _ => packageName, StringComparer.Ordinal),
            AmbiguousClrFullNameToPackages = ImmutableDictionary<string, ImmutableArray<string>>.Empty,
            NamespaceToPackages = namespaceToTypes.Keys
                .ToImmutableDictionary(
                    ns => ns,
                    _ => ImmutableHashSet.Create(StringComparer.Ordinal, packageName),
                    StringComparer.Ordinal)
        };
    }

    private static IEnumerable<string> EnumerateBindingsJsonFiles(string packageRoot)
    {
        static bool ShouldSkipDirName(string name) =>
            name is "node_modules" or ".git" or ".tests" or "__build";

        var stack = new Stack<string>();
        stack.Push(packageRoot);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var file in Directory.EnumerateFiles(dir, "bindings.json", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (var subdir in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(subdir);
                if (ShouldSkipDirName(name))
                {
                    continue;
                }

                stack.Push(subdir);
            }
        }
    }

    private static string ReadPackageName(string packageJsonPath)
    {
        var json = File.ReadAllText(packageJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("name", out var nameElement))
        {
            throw new InvalidOperationException($"Missing 'name' field in package.json: {packageJsonPath}");
        }

        return nameElement.GetString() ?? throw new InvalidOperationException($"Null package name in {packageJsonPath}");
    }

    private static void ProcessBindingsFile(
        string filePath,
        HashSet<string> allowedTypes,
        HashSet<string> allowedMembers,
        Dictionary<string, HashSet<string>> namespaceToTypes)
    {
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("schema", out var schemaElement) ||
            schemaElement.GetString() != "tsonic.bindings")
        {
            throw new InvalidOperationException($"Missing canonical schema 'tsonic.bindings' in bindings file: {filePath}");
        }

        if (!root.TryGetProperty("provider", out var providerElement) ||
            providerElement.ValueKind != JsonValueKind.Object ||
            !providerElement.TryGetProperty("namespace", out var nsElement))
        {
            throw new InvalidOperationException($"Missing 'provider.namespace' field in bindings file: {filePath}");
        }
        var namespaceName = nsElement.GetString() ?? throw new InvalidOperationException($"Null namespace in {filePath}");

        if (!root.TryGetProperty("targetSurface", out var targetSurfaceElement) ||
            targetSurfaceElement.ValueKind != JsonValueKind.Object ||
            !targetSurfaceElement.TryGetProperty("types", out var typesElement) ||
            typesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Missing or invalid 'targetSurface.types' array in bindings file: {filePath}");
        }

        if (typesElement.GetArrayLength() == 0)
        {
            return;
        }

        var namespaceTypes = namespaceToTypes.TryGetValue(namespaceName, out var existing)
            ? existing
            : new HashSet<string>();

        foreach (var typeElement in typesElement.EnumerateArray())
        {
            if (!typeElement.TryGetProperty("stableId", out var stableIdElement))
            {
                throw new InvalidOperationException($"Missing 'stableId' field for type in bindings file: {filePath}");
            }

            var typeStableId = stableIdElement.GetString() ?? throw new InvalidOperationException($"Null stableId in {filePath}");
            allowedTypes.Add(typeStableId);
            namespaceTypes.Add(typeStableId);

            ProcessMemberArray(typeElement, "methods", allowedMembers);
            ProcessMemberArray(typeElement, "properties", allowedMembers);
            ProcessMemberArray(typeElement, "fields", allowedMembers);
            ProcessMemberArray(typeElement, "events", allowedMembers);
            ProcessMemberArray(typeElement, "constructors", allowedMembers);
        }

        namespaceToTypes[namespaceName] = namespaceTypes;
    }

    private static void ProcessMemberArray(JsonElement typeElement, string memberArrayName, HashSet<string> allowedMembers)
    {
        if (!typeElement.TryGetProperty(memberArrayName, out var memberArray) || memberArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var member in memberArray.EnumerateArray())
        {
            if (!member.TryGetProperty("stableId", out var stableIdElement))
            {
                continue;
            }

            var memberStableId = stableIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(memberStableId))
            {
                allowedMembers.Add(memberStableId);
            }
        }
    }

    private static ImmutableDictionary<string, FacadeFamilyEntry> LoadFamilies(string familiesPath)
    {
        var json = File.ReadAllText(familiesPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var families = new Dictionary<string, FacadeFamilyEntry>();

        foreach (var property in root.EnumerateObject())
        {
            var clrBaseName = property.Name;
            var entry = property.Value;

            // Parse family entry (camelCase from JSON)
            var stem = entry.GetProperty("stem").GetString()
                ?? throw new InvalidOperationException($"Missing 'stem' in families.json entry: {clrBaseName}");
            var ns = entry.GetProperty("namespace").GetString()
                ?? throw new InvalidOperationException($"Missing 'namespace' in families.json entry: {clrBaseName}");
            var minArity = entry.GetProperty("minArity").GetInt32();
            var maxArity = entry.GetProperty("maxArity").GetInt32();
            var isDelegate = entry.TryGetProperty("isDelegate", out var isDelegateElem) && isDelegateElem.GetBoolean();

            families[clrBaseName] = new FacadeFamilyEntry(
                Stem: stem,
                Namespace: ns,
                MinArity: minArity,
                MaxArity: maxArity,
                IsDelegate: isDelegate
            );
        }

        return families.ToImmutableDictionary();
    }
}
