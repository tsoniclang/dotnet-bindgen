using System.Collections.Generic;
using System.Linq;
using tsbindgen.Library;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Renaming;

namespace tsbindgen.Plan;

/// <summary>
/// Plans import statements and aliasing for TypeScript declarations.
/// Generates import/export statements based on dependency graph.
/// Handles namespace-to-module mapping and name collision resolution.
/// </summary>
public static class ImportPlanner
{
    public static ImportPlan PlanImports(BuildContext ctx, SymbolGraph graph, ImportGraphData importGraph)
    {
        ctx.Log("ImportPlanner", "Planning import statements...");

        var plan = new ImportPlan
        {
            NamespaceImports = new Dictionary<string, List<ImportStatement>>(),
            NamespaceExports = new Dictionary<string, List<ExportStatement>>(),
            ImportAliases = new Dictionary<string, Dictionary<string, string>>()
        };

        // Plan imports for each namespace
        foreach (var ns in graph.Namespaces)
        {
            PlanNamespaceImports(ctx, ns, graph, importGraph, plan);
            PlanNamespaceExports(ctx, ns, plan);
        }

        ctx.Log("ImportPlanner", $"Planned imports for {plan.NamespaceImports.Count} namespaces");

        return plan;
    }

    private static void PlanNamespaceImports(
        BuildContext ctx,
        NamespaceSymbol ns,
        SymbolGraph graph,
        ImportGraphData importGraph,
        ImportPlan plan)
    {
        if (!importGraph.NamespaceDependencies.TryGetValue(ns.Name, out var dependencies))
        {
            // No dependencies, no imports needed
            return;
        }

        var imports = new List<ImportStatement>();
        var aliases = new Dictionary<string, string>();

        // TS2300 FIX: Track ALL imported local names across ALL modules in this file
        // Maps localName -> modulePath (import path) that first claimed this name
        // This enables cross-module collision detection and deterministic aliasing
        var globalImportedNames = new Dictionary<string, string>();

        foreach (var targetNamespace in dependencies.OrderBy(d => d))
        {
            // Get all types referenced from target namespace (CLR names)
            var referencedTypeClrNames = importGraph.CrossNamespaceReferences
                .Where(r => r.SourceNamespace == ns.Name && r.TargetNamespace == targetNamespace)
                .Select(r => r.TargetType)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            if (referencedTypeClrNames.Count == 0)
                continue;

            // Determine import path based on per-type membership in library contract
            // Check each referenced type individually - don't assume all types in a namespace are in library
            string importPath;
            if (ctx.LibraryContract != null)
            {
                // Check if ALL referenced types are in the library contract
                var allTypesInLibrary = referencedTypeClrNames.All(clrName =>
                    ctx.LibraryContract.AllowedClrFullNames.Contains(clrName));

                if (allTypesInLibrary && referencedTypeClrNames.Count > 0)
                {
                    // All types are from library - use package specifier facade
                    // e.g., "@tsonic/dotnet/System.Collections.Generic.js"
                    importPath = $"{ctx.LibraryContract.PackageName}/{targetNamespace}.js";
                }
                else
                {
                    // Some or all types are local - use relative path
                    importPath = PathPlanner.GetSpecifier(ns.Name, targetNamespace);
                }
            }
            else
            {
                // Normal mode (no library): relative path to internal index
                importPath = PathPlanner.GetSpecifier(ns.Name, targetNamespace);
            }

            // Check for name collisions and create aliases if needed
            var typeImports = new List<TypeImport>();

            // Check if this is a library import (facade names, not internal names)
            // Defined outside the loop so it's visible to alias resolution loops below
            var isLibraryImport = ctx.LibraryContract != null &&
                importPath.StartsWith(ctx.LibraryContract.PackageName + "/");

            foreach (var clrName in referencedTypeClrNames)
            {
                // PRE-EMIT GUARD: Catch assembly-qualified garbage in CLR names
                // This prevents the import garbage bug from ever reaching import planning
                if (clrName.Contains('[') || clrName.Contains("Culture=") || clrName.Contains("PublicKeyToken="))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"PRE-EMIT GUARD: CrossNamespaceReference contains assembly-qualified CLR name: '{clrName}' " +
                        $"(namespace {ns.Name} importing from {targetNamespace}). " +
                        $"This indicates CollectTypeReferences() failed to use GetOpenGenericClrKey().");
                    continue; // Skip this type reference
                }

                string tsName;

                // Try to look up TypeSymbol in local graph to get TypeScript emit name
                if (graph.TryGetType(clrName, out var typeSymbol) && typeSymbol != null)
                {
                    // Type is in local graph - use Renamer's final name
                    tsName = ctx.Renamer.GetFinalTypeName(typeSymbol);
                }
                else
                {
                    // Type is external (from another namespace) - construct TS name from CLR name
                    // CRITICAL: This handles cross-namespace generic types like IEnumerable_1, Func_2, etc.
                    // Apply same logic as TypeNameResolver for external types
                    tsName = GetTypeScriptNameForExternalType(clrName);
                    ctx.Log("ImportPlanner", $"External type {clrName} → {tsName}");
                }

                // LIBRARY FACADE FIX: When importing from library, use facade names (without arity)
                // Facades export: Dictionary_2 as Dictionary, Task_1 as Task, etc.
                // Non-generic types with generic siblings get _0 suffix: Task → Task_0
                if (isLibraryImport)
                {
                    tsName = GetFacadeExportName(tsName, clrName, ctx.LibraryContract!);
                    ctx.Log("ImportPlanner", $"Library facade name: {clrName} → {tsName}");
                }

                // PRE-EMIT GUARD: Detect assembly-qualified garbage before it reaches output
                // Prevents regressions of the import garbage bug (fixed in commit 70d21db)
                if (tsName.Contains('[') || tsName.Contains("Culture=") || tsName.Contains("PublicKeyToken="))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"PRE-EMIT GUARD: Import statement would contain assembly-qualified garbage: '{tsName}' " +
                        $"(from CLR name: '{clrName}' in namespace {ns.Name} importing from {targetNamespace}). " +
                        $"This must be fixed before emission.");
                    continue; // Skip this import to prevent emission
                }

                // LIBRARY FACADE FIX: Skip if this facade name was already imported FROM THE SAME MODULE
                // Multiple arity types (Action_1, Action_2, Action_3) all map to same facade name (Action)
                if (typeImports.Any(ti => ti.TypeName == tsName))
                {
                    ctx.Log("ImportPlanner", $"Skipping duplicate facade import: {tsName}");
                    continue;
                }

                // TS2300 FIX: Check for cross-module name collisions (global across all imports)
                // If this name was already imported from a DIFFERENT module, we need an alias
                string? crossModuleAlias = null;
                if (globalImportedNames.TryGetValue(tsName, out var existingModulePath))
                {
                    if (existingModulePath != importPath)
                    {
                        // Same name from different module - need deterministic alias
                        // Scheme: {name}__{SanitizedNamespace} e.g., IEnumerable__System_Collections
                        crossModuleAlias = $"{tsName}__{SanitizeNamespaceForAlias(targetNamespace)}";
                        ctx.Log("ImportPlanner", $"Cross-module collision: {tsName} (from {existingModulePath}) " +
                            $"vs {importPath} → aliasing to {crossModuleAlias}");
                    }
                }
                else
                {
                    // First time seeing this name - claim it for this module
                    globalImportedNames[tsName] = importPath;
                }

                // C.5.3 FIX: Pass namespace symbol to detect collisions with local types
                // Use cross-module alias if we detected a collision, otherwise normal alias detection
                var alias = crossModuleAlias ?? DetermineAlias(ctx, ns, targetNamespace, tsName, aliases);

                // TS2693 FIX: Determine if this type needs a value import (not just type import)
                // Base classes and interfaces used in extends/implements need to be imported as values
                var isValueImport = IsTypeUsedAsValue(importGraph, ns.Name, targetNamespace, clrName);

                typeImports.Add(new TypeImport(
                    TypeName: tsName,
                    Alias: alias,
                    IsValueImport: isValueImport));

                if (alias != null)
                {
                    aliases[tsName] = alias;
                }
            }

            if (typeImports.Count == 0)
                continue;

            // Generate namespace alias for this import module
            // Format: "System" → "System_Internal", "System.Collections.Generic" → "System_Collections_Generic_Internal"
            var namespaceAlias = GenerateNamespaceAlias(targetNamespace);

            var importStatement = new ImportStatement(
                ImportPath: importPath,
                TargetNamespace: targetNamespace,
                TypeImports: typeImports,
                NamespaceAlias: namespaceAlias);

            imports.Add(importStatement);

            // TS2693 FIX: Build qualified name mapping for value imports
            // This allows printers to qualify type names with namespace alias
            foreach (var ti in typeImports.Where(t => t.IsValueImport))
            {
                // Get the CLR name for this type (need to map back from TS name)
                var clrName = referencedTypeClrNames.FirstOrDefault(c =>
                {
                    var tsNameForClr = graph.TryGetType(c, out var ts) && ts != null
                        ? ctx.Renamer.GetFinalTypeName(ts)
                        : GetTypeScriptNameForExternalType(c);
                    // LIBRARY FACADE FIX: Apply facade transform for matching if this is a library import
                    if (isLibraryImport)
                        tsNameForClr = GetFacadeExportName(tsNameForClr, c, ctx.LibraryContract!);
                    return tsNameForClr == ti.TypeName;
                });

                if (!string.IsNullOrEmpty(clrName))
                {
                    // STEP 1 RE-EXPORT FIX: Use instance type name for all class-like types
                    // All classes/interfaces/structs emit as T$instance (not just those with views)
                    // Heritage clauses need the INSTANCE CLASS (value), not type alias
                    string emittedName = ti.TypeName;

                    // Check if this type exists in the graph to get correct instance name
                    if (graph.TryGetType(clrName, out var targetType) && targetType != null)
                    {
                        // STEP 1: Use GetInstanceTypeName for all classes/interfaces/structs
                        // This includes $instance suffix for all except enums/delegates
                        emittedName = ctx.Renamer.GetInstanceTypeName(targetType, Renaming.NamespaceArea.Internal);
                    }

                    // Flat ESM: qualify with namespace alias only (module namespace import)
                    // Example: System_Internal.Exception$instance
                    var qualifiedName = $"{namespaceAlias}.{emittedName}";
                    plan.ValueImportQualifiedNames[(ns.Name, clrName)] = qualifiedName;
                }
            }

            // TS2416 FIX: Build alias name mapping for ALL cross-namespace types
            // Even types used as value imports (extends/implements) may also appear in type positions (return types, parameters)
            // This enables `import type { Alias }` for cross-namespace type positions
            // Example: ClaimsIdentity used in BOTH "extends ClaimsIdentity$instance" AND "clone(): ClaimsIdentity"
            // LIBRARY FACADE FIX: Iterate over ALL CLR names (not just typeImports) to handle deduplicated facades
            // When Action_1, Action_2, Action_3 all map to facade name "Action", we need to record aliases for ALL of them
            foreach (var clrName in referencedTypeClrNames)
            {
                // Skip garbage CLR names (already filtered above, but be safe)
                if (clrName.Contains('[') || clrName.Contains("Culture="))
                    continue;

                // Compute the TypeScript name for this CLR type
                string tsName;
                if (graph.TryGetType(clrName, out var typeSymbol) && typeSymbol != null)
                {
                    tsName = ctx.Renamer.GetFinalTypeName(typeSymbol);
                }
                else
                {
                    tsName = GetTypeScriptNameForExternalType(clrName);
                }

                // Apply facade transform if this is a library import
                if (isLibraryImport)
                {
                    tsName = GetFacadeExportName(tsName, clrName, ctx.LibraryContract!);
                }

                // TS2300 FIX: Check if this type was aliased due to cross-module collision
                // Look up the actual imported name (which may be aliased)
                var typeImport = typeImports.FirstOrDefault(ti => ti.TypeName == tsName);
                var importedName = typeImport?.Alias ?? tsName;

                // Record the alias mapping: CLR full name → imported TypeScript name (with alias if applicable)
                plan.TypeImportAliasNames[(ns.Name, clrName)] = importedName;
            }

            ctx.Log("ImportPlanner", $"{ns.Name} imports {typeImports.Count} types from {targetNamespace}");
        }

        if (imports.Count > 0)
        {
            plan.NamespaceImports[ns.Name] = imports;
            plan.ImportAliases[ns.Name] = aliases;
        }
    }

    private static void PlanNamespaceExports(
        BuildContext ctx,
        NamespaceSymbol ns,
        ImportPlan plan)
    {
        var exports = new List<ExportStatement>();

        // Create namespace scope for name resolution
        // Export all public types in the namespace
        foreach (var type in ns.Types)
        {
            if (type.Accessibility == Model.Symbols.Accessibility.Public)
            {
                var finalName = ctx.Renamer.GetFinalTypeName(type);
                exports.Add(new ExportStatement(
                    ExportName: finalName,
                    ExportKind: DetermineExportKind(type),
                    Arity: type.Arity, // TS2314 FIX: Capture generic arity
                    SourceType: type)); // FACADE CONSTRAINTS: Store source type for constraint propagation
            }
        }

        if (exports.Count > 0)
        {
            plan.NamespaceExports[ns.Name] = exports;
            ctx.Log("ImportPlanner", $"{ns.Name} exports {exports.Count} types");
        }
    }

    private static string? DetermineAlias(
        BuildContext ctx,
        NamespaceSymbol sourceNamespace,
        string targetNamespace,
        string typeName,
        Dictionary<string, string> existingAliases)
    {
        // Avoid shadowing TypeScript built-ins (Array, String, Boolean, Object, Symbol, BigInt)
        // when importing CLR types with matching names.
        if (IsTypeScriptBuiltinIdentifier(typeName))
        {
            return $"Clr{typeName}";
        }

        // C.5.3 FIX: Check if imported type name collides with local type declaration
        // Example: System.Reflection imports AssemblyHashAlgorithm but also has local enum AssemblyHashAlgorithm
        var hasLocalCollision = sourceNamespace.Types.Any(localType =>
        {
            var localTypeName = ctx.Renamer.GetFinalTypeName(localType);
            return localTypeName == typeName;
        });

        if (hasLocalCollision)
        {
            // Name collision with local type - need alias
            // Use source namespace suffix to disambiguate (e.g., AssemblyHashAlgorithm_Assemblies)
            var targetNsShort = GetNamespaceShortName(targetNamespace);
            return $"{typeName}_{targetNsShort}";
        }

        // Check if alias is needed (name collision with other imports)
        if (existingAliases.ContainsKey(typeName))
        {
            // Name collision - need alias
            var targetNsShort = GetNamespaceShortName(targetNamespace);
            return $"{typeName}_{targetNsShort}";
        }

        // Check policy - always alias imports?
        var policy = ctx.Policy.Modules;
        if (policy.AlwaysAliasImports)
        {
            var targetNsShort = GetNamespaceShortName(targetNamespace);
            return $"{typeName}_{targetNsShort}";
        }

        // No alias needed
        return null;
    }

    private static string GetNamespaceShortName(string namespaceName)
    {
        // Get short name for namespace aliasing
        // "System.Collections.Generic" -> "Generic"
        var lastDot = namespaceName.LastIndexOf('.');
        return lastDot >= 0 ? namespaceName.Substring(lastDot + 1) : namespaceName;
    }

    /// <summary>
    /// TS2300 FIX: Sanitize namespace name for use in cross-module aliases.
    /// Converts dots to underscores to create valid TypeScript identifier.
    /// Examples:
    ///   "System.Collections" → "System_Collections"
    ///   "System.Collections.Generic" → "System_Collections_Generic"
    /// </summary>
    private static string SanitizeNamespaceForAlias(string namespaceName)
    {
        return namespaceName.Replace('.', '_');
    }

    private static bool IsTypeScriptBuiltinIdentifier(string typeName)
    {
        // TypeScript global types/constructors that we must not shadow with CLR imports
        return typeName is "Array" or "String" or "Boolean" or "Object" or "Symbol" or "BigInt";
    }

    /// <summary>
    /// TS2693 FIX: Generate a valid TypeScript identifier for namespace imports.
    /// Converts namespace to a safe identifier by replacing dots with underscores.
    /// Examples:
    ///   "System" → "System_Internal"
    ///   "System.Collections.Generic" → "System_Collections_Generic_Internal"
    ///   "Microsoft.Win32" → "Microsoft_Win32_Internal"
    /// </summary>
    private static string GenerateNamespaceAlias(string namespaceName)
    {
        // Replace dots with underscores to make valid TS identifier
        var safeName = namespaceName.Replace('.', '_');

        // Append _Internal suffix to avoid collisions with type names
        return $"{safeName}_Internal";
    }

    private static ExportKind DetermineExportKind(Model.Symbols.TypeSymbol type)
    {
        return type.Kind switch
        {
            Model.Symbols.TypeKind.Class => ExportKind.Class,
            Model.Symbols.TypeKind.Interface => ExportKind.Interface,
            Model.Symbols.TypeKind.Struct => ExportKind.Interface, // Structs emit as interfaces in TS
            Model.Symbols.TypeKind.Enum => ExportKind.Enum,
            Model.Symbols.TypeKind.Delegate => ExportKind.Type, // Delegates emit as type aliases
            _ => ExportKind.Type
        };
    }

    /// <summary>
    /// TS2693 FIX: Determines if a type is used as a value (not just a type).
    /// Types used in extends/implements clauses need value imports (not 'import type').
    /// Returns true if the type is referenced as BaseClass or Interface.
    ///
    /// NOTE: Generic constraints are TYPE-ONLY positions (type sites), not value sites.
    /// They get qualified names through TypeNameResolver, but use 'import type' (not namespace imports).
    /// </summary>
    private static bool IsTypeUsedAsValue(
        ImportGraphData importGraph,
        string sourceNamespace,
        string targetNamespace,
        string targetTypeClrName)
    {
        // Check if any cross-namespace reference for this type is BaseClass or Interface
        // Constraints are explicitly NOT included - they are type-only positions
        return importGraph.CrossNamespaceReferences.Any(r =>
            r.SourceNamespace == sourceNamespace &&
            r.TargetNamespace == targetNamespace &&
            r.TargetType == targetTypeClrName &&
            (r.ReferenceKind == ReferenceKind.BaseClass ||
             r.ReferenceKind == ReferenceKind.Interface));
    }

    /// <summary>
    /// Get TypeScript name for an external type (not in current graph).
    /// Mirrors TypeNameResolver logic for external types.
    /// CRITICAL: Handles generic arity and reserved words.
    /// </summary>
    private static string GetTypeScriptNameForExternalType(string clrFullName)
    {
        // Extract simple name from full CLR name
        // Example: "System.Collections.Generic.IEnumerable`1" → "IEnumerable`1"
        var simpleName = clrFullName.Contains('.')
            ? clrFullName.Substring(clrFullName.LastIndexOf('.') + 1)
            : clrFullName;

        // Sanitize: backtick to underscore (IEnumerable`1 → IEnumerable_1)
        var sanitized = simpleName.Replace('`', '_');

        // Handle nested types
        sanitized = sanitized.Replace('+', '$');

        // CRITICAL: Check if sanitized name is a TypeScript reserved word
        // Example: "Type" → "Type_", "Object" → "Object_"
        var result = TypeScriptReservedWords.Sanitize(sanitized);
        return result.Sanitized;
    }

    /// <summary>
    /// Convert internal TypeScript name to facade export name.
    /// Facade files strip arity from generic types:
    ///   Dictionary_2 → Dictionary
    ///   Action_1 → Action
    ///   IEnumerable_1 → IEnumerable
    /// Multi-arity families use conditional types, so both generic and non-generic
    /// members use the stem name:
    ///   Task (non-generic) → Task (if part of Task/Task`1 family)
    ///   Task_1 (generic) → Task
    /// </summary>
    private static string GetFacadeExportName(string internalName, string clrFullName, LibraryContract libraryContract)
    {
        // Check if CLR name is generic (has backtick)
        var isGeneric = clrFullName.Contains('`');

        if (isGeneric)
        {
            // Generic type: strip the arity suffix from TS name
            // E.g., Task_1 → Task, Dictionary_2 → Dictionary
            var lastUnderscore = internalName.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var suffix = internalName.Substring(lastUnderscore + 1);
                if (suffix.Length > 0 && suffix.All(char.IsDigit))
                {
                    return internalName.Substring(0, lastUnderscore);
                }
            }
            return internalName;
        }
        else
        {
            // Non-generic type: check if it's part of a multi-arity family
            // Only use stem name if facade emits a conditional type alias for this family
            if (IsPartOfMultiArityFamily(clrFullName, libraryContract))
            {
                // Multi-arity family: use stem name (conditional type handles routing)
                return internalName;
            }
            else
            {
                // Not part of a family: use original name as-is
                return internalName;
            }
        }
    }

    /// <summary>
    /// Check if a CLR type is part of a multi-arity family in the library.
    /// Uses the canonical FacadeFamilies index from the library contract
    /// rather than recomputing from AllowedClrFullNames.
    ///
    /// This prevents drift between what FacadeEmitter emits and what ImportPlanner assumes.
    /// </summary>
    private static bool IsPartOfMultiArityFamily(string clrFullName, LibraryContract libraryContract)
    {
        // Extract CLR base name (strip backtick-arity if present)
        var baseName = Emit.MultiArityFamilyDetect.ExtractClrBaseName(clrFullName);

        // Use canonical family index - no recomputation, no drift
        return libraryContract.FacadeFamilies.ContainsKey(baseName);
    }
}

/// <summary>
/// Import plan containing all import/export statements for the symbol graph.
/// </summary>
public sealed class ImportPlan
{
    /// <summary>
    /// Maps namespace name to list of import statements for that namespace.
    /// </summary>
    public Dictionary<string, List<ImportStatement>> NamespaceImports { get; init; } = new();

    /// <summary>
    /// Maps namespace name to list of export statements for that namespace.
    /// </summary>
    public Dictionary<string, List<ExportStatement>> NamespaceExports { get; init; } = new();

    /// <summary>
    /// Maps namespace name to dictionary of type aliases (original name -> alias).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ImportAliases { get; init; } = new();

    /// <summary>
    /// TS2693 FIX: Maps (source namespace, target type CLR name) → qualified TypeScript name.
    /// Used for value-imported types that must be qualified with namespace alias.
    /// Example: ("Microsoft.CSharp.RuntimeBinder", "System.Exception") → "System_Internal.Exception"
    /// </summary>
    public Dictionary<(string SourceNamespace, string TargetTypeCLRName), string> ValueImportQualifiedNames { get; init; } = new();

    /// <summary>
    /// TS2416 FIX: Maps (source namespace, target type CLR name) → simple alias name for type positions.
    /// Used for cross-namespace types in type positions (return types, property types, parameters).
    /// Enables `import type { Alias }` statements for alias-centric type references.
    /// Example: ("System.Security.Principal", "System.Security.Claims.ClaimsIdentity") → "ClaimsIdentity"
    /// </summary>
    public Dictionary<(string SourceNamespace, string TargetTypeCLRName), string> TypeImportAliasNames { get; init; } = new();

    /// <summary>
    /// Gets import statements for a specific namespace.
    /// Returns empty list if namespace has no imports.
    /// </summary>
    public IReadOnlyList<ImportStatement> GetImportsFor(string namespaceName)
    {
        return NamespaceImports.TryGetValue(namespaceName, out var imports)
            ? imports
            : new List<ImportStatement>();
    }
}

/// <summary>
/// Represents a TypeScript import statement.
/// </summary>
public sealed record ImportStatement(
    string ImportPath,
    string TargetNamespace,
    List<TypeImport> TypeImports,
    string NamespaceAlias); // Alias for namespace imports (e.g., "System_Internal")

/// <summary>
/// Represents a single type import within an import statement.
/// </summary>
public sealed record TypeImport(
    string TypeName,
    string? Alias,
    bool IsValueImport); // True for base classes/interfaces (needs 'import'), false for type-only (can use 'import type')

/// <summary>
/// Represents a TypeScript export statement.
/// </summary>
public sealed record ExportStatement(
    string ExportName,
    ExportKind ExportKind,
    int Arity, // Number of generic type parameters (0 for non-generic types)
    Model.Symbols.TypeSymbol SourceType); // Source type symbol (for constraint propagation)

/// <summary>
/// Kind of export.
/// </summary>
public enum ExportKind
{
    Class,
    Interface,
    Enum,
    Type, // Type alias
    Const // Const value
}
