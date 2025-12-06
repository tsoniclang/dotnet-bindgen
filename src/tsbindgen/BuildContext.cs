using tsbindgen.Core.Canon;
using tsbindgen.Core.Diagnostics;
using tsbindgen.Core.Intern;
using tsbindgen.Core.Naming;
using tsbindgen.Core.Policy;
using tsbindgen.Renaming;
using tsbindgen.Library;

namespace tsbindgen;

/// <summary>
/// Central context for the entire build process.
/// Provides access to all shared services: policy, renamer, diagnostics, etc.
/// Immutable after construction.
/// </summary>
public sealed class BuildContext
{
    /// <summary>
    /// Generation policy controlling all behavior.
    /// </summary>
    public required GenerationPolicy Policy { get; init; }

    /// <summary>
    /// Central naming authority - all identifiers flow through this.
    /// </summary>
    public required SymbolRenamer Renamer { get; init; }

    /// <summary>
    /// String interning for reducing allocations.
    /// </summary>
    public required StringInterner Interner { get; init; }

    /// <summary>
    /// Diagnostic collection for errors/warnings.
    /// </summary>
    public required DiagnosticBag Diagnostics { get; init; }

    /// <summary>
    /// Optional logger for progress/debugging.
    /// </summary>
    public Action<string>? Logger { get; init; }

    /// <summary>
    /// Enable verbose logging (all categories).
    /// </summary>
    public bool VerboseLogging { get; init; }

    /// <summary>
    /// Specific log categories to enable (if VerboseLogging is false).
    /// Null means no category-based filtering.
    /// </summary>
    public HashSet<string>? LogCategories { get; init; }

    /// <summary>
    /// Enable strict mode validation.
    /// When true, PhaseGate enforces zero warnings unless explicitly whitelisted in StrictModePolicy.
    /// Informational messages (INFO) are always allowed and don't count toward warning totals.
    /// </summary>
    public bool StrictMode { get; init; }

    /// <summary>
    /// Library contract for library mode.
    /// When non-null, emission is filtered to only symbols present in the contract.
    /// Loaded from existing tsbindgen package (metadata.json + bindings.json).
    /// </summary>
    public LibraryContract? LibraryContract { get; init; }

    /// <summary>
    /// Library namespace index for import resolution in library mode.
    /// Maps CLR full name (e.g., "System.Exception") to namespace (e.g., "System").
    /// Built from the FULL graph before library filtering.
    /// Used by ImportGraph to resolve library type references.
    /// </summary>
    public Dictionary<string, string>? LibraryNamespaceIndex { get; set; }

    /// <summary>
    /// Create a new BuildContext with default services.
    /// </summary>
    public static BuildContext Create(
        GenerationPolicy? policy = null,
        Action<string>? logger = null,
        bool verboseLogging = false,
        HashSet<string>? logCategories = null,
        bool strictMode = false,
        LibraryContract? libraryContract = null)
    {
        policy ??= PolicyDefaults.Create();

        var renamer = new SymbolRenamer();
        var interner = new StringInterner();
        var diagnostics = new DiagnosticBag();

        // Apply explicit overrides from policy
        renamer.ApplyExplicitOverrides(policy.Renaming.ExplicitMap);

        // Adopt style transforms from policy
        // Type names: always preserve as-is (PascalCase from C#)
        // Member names: apply JS-style transform if configured
        Func<string, string> typeTransform = static name => name; // Identity - preserve type names

        Func<string, string> memberTransform = policy.Emission.Naming switch
        {
            NamingStyle.Js => NameTransform.ToJsStyle,
            NamingStyle.Clr => static name => name, // Identity - preserve CLR names
            _ => static name => name
        };

        renamer.AdoptTypeStyleTransform(typeTransform);
        renamer.AdoptMemberStyleTransform(memberTransform);

        return new BuildContext
        {
            Policy = policy,
            Renamer = renamer,
            Interner = interner,
            Diagnostics = diagnostics,
            Logger = logger,
            VerboseLogging = verboseLogging,
            LogCategories = logCategories,
            StrictMode = strictMode,
            LibraryContract = libraryContract
        };
    }

    /// <summary>
    /// Log a message with category (if logger is configured and category is enabled).
    /// </summary>
    /// <param name="category">Log category (e.g., "ViewPlanner", "PhaseGate")</param>
    /// <param name="message">Log message</param>
    public void Log(string category, string message)
    {
        // Only log if:
        // 1. VerboseLogging is enabled (log everything), OR
        // 2. LogCategories contains this category
        if (Logger != null && (VerboseLogging || (LogCategories?.Contains(category) ?? false)))
        {
            Logger($"[{category}] {message}");
        }
    }

    /// <summary>
    /// Create a canonical signature for a method.
    /// </summary>
    public string CanonicalizeMethod(
        string methodName,
        IReadOnlyList<string> parameterTypes,
        string returnType)
    {
        return SignatureCanonicalizer.CanonicalizeMethod(methodName, parameterTypes, returnType);
    }

    /// <summary>
    /// Create a canonical signature for a property.
    /// </summary>
    public string CanonicalizeProperty(
        string propertyName,
        IReadOnlyList<string> indexParameterTypes,
        string propertyType)
    {
        return SignatureCanonicalizer.CanonicalizeProperty(
            propertyName,
            indexParameterTypes,
            propertyType);
    }

    /// <summary>
    /// Intern a string.
    /// </summary>
    public string Intern(string value) => Interner.Intern(value);
}
