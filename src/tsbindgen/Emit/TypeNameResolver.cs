using tsbindgen.Core;
using tsbindgen.Model;
using tsbindgen.Model.Types;
using tsbindgen.Plan;
using tsbindgen.Renaming;

namespace tsbindgen.Emit;

/// <summary>
/// Single source of truth for resolving TypeScript identifiers from TypeReferences.
/// Uses the Renamer to ensure imports and declarations use identical names.
/// TS2693 FIX: Also uses ImportPlan to qualify value-imported types with namespace alias.
/// TS2304 FIX (Facade): In facade mode, always qualifies cross-namespace types with namespace alias.
/// </summary>
public sealed class TypeNameResolver
{
    private readonly BuildContext _ctx;
    private readonly SymbolGraph _graph;
    private readonly ImportPlan? _importPlan;
    private readonly string? _currentNamespace;
    private readonly bool _facadeMode;

    public TypeNameResolver(BuildContext ctx, SymbolGraph graph, ImportPlan? importPlan = null, string? currentNamespace = null, bool facadeMode = false)
    {
        _ctx = ctx;
        _graph = graph;
        _importPlan = importPlan;
        _currentNamespace = currentNamespace;
        _facadeMode = facadeMode;
    }

    public bool IsFacadeMode => _facadeMode;

    /// <summary>
    /// The current namespace being emitted (if known).
    /// Used by TypeRefPrinter to qualify CLR type names when needed.
    /// </summary>
    public string? CurrentNamespace => _currentNamespace;

    /// <summary>
    /// Resolve the final TypeScript identifier for a TypeSymbol.
    /// This directly queries the Renamer - the single source of truth.
    /// </summary>
    public string For(Model.Symbols.TypeSymbol type)
    {
        // TS2416 FIX: Use ALIAS name for type positions (properties, parameters, returns, constraints)
        // The alias includes views union: Foo_ = Foo_$instance | __Foo_$views
        // This ensures signatures match across inheritance hierarchy
        // Example: readonly module_: Module_ (not Module_$instance)
        return type.TsEmitName;
    }

    /// <summary>
    /// Resolve the final TypeScript identifier for a NamedTypeReference.
    /// This is the ONLY way to get type names during emission - never use CLR names directly.
    /// </summary>
    public string ResolveTypeName(NamedTypeReference named)
    {
        return For(named);
    }

    /// <summary>
    /// Resolve the final TypeScript identifier for a NamedTypeReference.
    /// Wrapper for ResolveTypeName to provide consistent API.
    /// CRITICAL: Uses TypeMap to short-circuit built-in types BEFORE graph lookup.
    /// This prevents PG_LOAD_001 false positives for primitives.
    /// Distinguishes between type positions (signatures) and value positions (extends/implements).
    /// </summary>
    /// <param name="named">The type reference to resolve.</param>
    /// <param name="forValuePosition">If true, this is for extends/implements (value position) - use qualified names to avoid TS2693.</param>
    public string For(NamedTypeReference named, bool forValuePosition = false)
    {
        // 1. Try TypeMap FIRST (short-circuit built-in types before graph lookup)
        if (TypeMap.TryMapBuiltin(named.FullName, out var builtinType))
        {
            return builtinType;
        }

        // 2. TS2416 FIX: For TYPE positions, check TypeImportAliasNames first
        //    This enables alias-centric type references to avoid cross-namespace signature mismatches
        //    Example: GenericIdentity.clone() returns "ClaimsIdentity" (alias) not "System_Security_Claims_Internal.System.Security.Claims.ClaimsIdentity$instance"
        if (!forValuePosition && _importPlan != null && _currentNamespace != null)
        {
            var clrFullName = named.FullName;
            if (_importPlan.TypeImportAliasNames.TryGetValue((_currentNamespace, clrFullName), out var aliasName))
            {
                // Use simple alias from `import type { Alias }` statement
                // This matches the base class signature exactly
                return aliasName;
            }
        }

        // 3. Check if this type needs qualification with namespace alias
        //    This happens when the type is used as a value (base class/interface)
        if (_importPlan != null && _currentNamespace != null)
        {
            var valueClrFullName = named.FullName;
            if (_importPlan.ValueImportQualifiedNames.TryGetValue((_currentNamespace, valueClrFullName), out var qualifiedName))
            {
                // For common reflection types, prefer simple names in TYPE positions only
                // In value positions (extends/implements), always use qualified names to avoid TS2693
                if (!forValuePosition && ReflectionTypes.IsCommonReflectionType(valueClrFullName))
                {
                    // Type position (return type, parameter, property): use simple name
                    // This prevents TS2416 errors when signatures need to match base class
                    // Fall through to step 3
                }
                else
                {
                    // Value position OR non-reflection type: use qualified name
                    // Return qualified name: "System_Reflection_Internal.MethodInfo"
                    return qualifiedName;
                }
            }
        }

        // 4. Look up TypeSymbol in graph using CLR full name (TypeIndex key)
        var graphClrFullName = named.FullName;

        if (!_graph.TypeIndex.TryGetValue(graphClrFullName, out var typeSymbol))
        {
            // If a nested type is referenced but not present in the graph, it is usually a non-public
            // nested type (e.g., PEBuilder+Section) that we intentionally do NOT emit. In type positions,
            // degrade these to 'unknown' to keep the .d.ts surface TypeScript-valid (no TS2304).
            //
            // IMPORTANT: We only do this when the *enclosing* type is in the graph, which indicates
            // this is an omitted nested type from the current emission set (not an external nested type
            // from a library the consumer is expected to provide via --lib).
            if (!forValuePosition)
            {
                var plusIndex = graphClrFullName.IndexOf('+');
                if (plusIndex > 0)
                {
                    var outerFullName = graphClrFullName.Substring(0, plusIndex);
                    if (_graph.TypeIndex.ContainsKey(outerFullName))
                    {
                        return "unknown";
                    }
                }
            }

            // Type not in graph - this is an EXTERNAL type from another assembly
            // Use the CLR name as-is (it will be in imports if needed)

            // Extract simple name from full name (e.g., "System.Collections.Generic.List`1" → "List`1")
            // IMPORTANT: Stop at first comma (assembly-qualified names have ", Version=...")
            var fullName = named.FullName;
            var commaIndex = fullName.IndexOf(',');
            if (commaIndex >= 0)
            {
                fullName = fullName.Substring(0, commaIndex).Trim();
            }

            // Extract namespace from full name (e.g., "System.Collections.Generic.List`1" → "System.Collections.Generic")
            var externalNamespace = fullName.Contains('.')
                ? fullName.Substring(0, fullName.LastIndexOf('.'))
                : "";

            var simpleName = fullName.Contains('.')
                ? fullName.Substring(fullName.LastIndexOf('.') + 1)
                : fullName;

            // Sanitize the name for TypeScript (handle generic arity, nested types, etc.)
            var sanitized = SanitizeClrName(simpleName);

            // CRITICAL: Check if sanitized name is a TypeScript reserved word
            // External types (not in current graph) still need reserved word handling
            // Example: System.Type referenced from another namespace → Type_
            var result = TypeScriptReservedWords.SanitizeTypeName(sanitized);
            var finalExternalName = result.Sanitized;

            // TS2304 FIX (Facade): In facade mode, qualify cross-namespace EXTERNAL types too.
            // Without this, facade/extension helper files that import namespace modules
            // would reference external types unqualified (e.g., "CancellationToken") which
            // is not in scope unless declared globally.
            if (_facadeMode && _currentNamespace != null)
            {
                if (!string.IsNullOrEmpty(externalNamespace) &&
                    externalNamespace != _currentNamespace &&
                    !finalExternalName.Contains('.'))
                {
                    var namespaceAlias = GetNamespaceAlias(externalNamespace);
                    return $"{namespaceAlias}.{finalExternalName}";
                }
            }

            return finalExternalName;
        }

        // IMPORTANT: We do not emit non-public types to TypeScript declarations.
        // If a non-public type appears in a signature position, degrade it to 'unknown'
        // to keep the .d.ts surface TypeScript-valid and prevent TS2304 missing symbol errors.
        // This is especially common for nested/internal types used in protected/internal APIs.
        if (!forValuePosition && typeSymbol.Accessibility != Model.Symbols.Accessibility.Public)
        {
            return "unknown";
        }

        // 5. Get final TypeScript name from Renamer (single source of truth)
        // TS2416 FIX: Type positions use ALIAS (e.g., Module_ = Module_$instance | __Module_$views)
        //             Value positions use INSTANCE (e.g., Module_$instance)
        // This ensures signatures match across inheritance hierarchy.
        string finalName;
        if (forValuePosition)
        {
            // VALUE POSITION (extends/implements): Use instance type name
            // Example: extends Foo_$instance
            finalName = _ctx.Renamer.GetInstanceTypeName(typeSymbol);
        }
        else
        {
            // TYPE POSITION (properties, parameters, returns, constraints): Use alias name
            // Example: readonly foo: Module_ (not Module_$instance)
            // The alias includes the views union: Module_ = Module_$instance | __Module_$views
            // This ensures derived class properties match base class property types
            finalName = typeSymbol.TsEmitName;
        }

        // 6. TS2304 FIX (Facade): In facade mode, qualify cross-namespace types with namespace alias
        //    This prevents "Cannot find name 'IEquatable_1'" errors in facade constraint clauses
        if (_facadeMode && _currentNamespace != null)
        {
            var targetNamespace = typeSymbol.Namespace;
            if (targetNamespace != _currentNamespace)
            {
                // Cross-namespace reference in facade - must qualify
                // Convert "System.Runtime.InteropServices" → "System_Runtime_InteropServices"
                var namespaceAlias = GetNamespaceAlias(targetNamespace);
                var facadeQualified = $"{namespaceAlias}.{finalName}";
                _ctx.Log("TypeNameResolver", $"  → Facade mode cross-namespace: {facadeQualified}");
                return facadeQualified;
            }
        }

        _ctx.Log("TypeNameResolver", $"  → Returning unqualified: {finalName}");
        return finalName;
    }


    /// <summary>
    /// Get the TypeScript import alias for a namespace.
    /// Converts "System.Collections.Generic" to "System_Collections_Generic".
    /// </summary>
    private static string GetNamespaceAlias(string namespaceName)
    {
        // Replace dots with underscores to make valid TS identifier
        return namespaceName.Replace('.', '_');
    }

    /// <summary>
    /// Sanitize CLR type name for TypeScript.
    /// Handles generic arity (`1 → _1) and special characters.
    /// </summary>
    private static string SanitizeClrName(string clrName)
    {
        // Replace generic arity backtick with underscore: List`1 → List_1
        var sanitized = clrName.Replace('`', '_');

        // Remove any remaining invalid TypeScript identifier characters
        sanitized = sanitized.Replace('+', '_'); // Nested type separator
        sanitized = sanitized.Replace('<', '_');
        sanitized = sanitized.Replace('>', '_');
        sanitized = sanitized.Replace('[', '_');
        sanitized = sanitized.Replace(']', '_');

        return sanitized;
    }

    /// <summary>
    /// Try to map a CLR primitive type to TypeScript built-in type.
    /// Returns null if not a primitive.
    /// </summary>
    public static string? TryMapPrimitive(string clrFullName)
    {
        TypeMap.TryMapBuiltin(clrFullName, out var tsType);
        return tsType;
    }

    /// <summary>
    /// Check if a type is a primitive that doesn't need imports.
    /// </summary>
    public static bool IsPrimitive(string clrFullName)
    {
        return TypeMap.TryMapBuiltin(clrFullName, out _);
    }
}
