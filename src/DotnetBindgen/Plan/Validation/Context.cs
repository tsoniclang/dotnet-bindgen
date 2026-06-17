using System;
using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Core.Diagnostics;

namespace DotnetBindgen.Plan.Validation;

/// <summary>
/// Validation context container and reporting functions.
/// </summary>
internal sealed class ValidationContext
{
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<string> Diagnostics { get; set; } = new();

    // Track diagnostic counts by code (e.g., TBG120, TBG211, etc.)
    public Dictionary<string, int> DiagnosticCountsByCode { get; set; } = new();

    // Step 1: Track sanitized names (reserved words that were properly escaped)
    public int SanitizedNameCount { get; set; }

    // Step 3: Aggregate interface conformance issues by type (for one-line summaries)
    public Dictionary<string, List<string>> InterfaceConformanceIssuesByType { get; set; } = new();

    /// <summary>
    /// Record a diagnostic with its code for tracking.
    /// </summary>
    public void RecordDiagnostic(string code, string severity, string message)
    {
        // Track count by code
        if (!DiagnosticCountsByCode.ContainsKey(code))
        {
            DiagnosticCountsByCode[code] = 0;
        }
        DiagnosticCountsByCode[code]++;

        // Update severity counters
        switch (severity.ToUpperInvariant())
        {
            case "ERROR":
                ErrorCount++;
                break;
            case "WARNING":
                WarningCount++;
                break;
            case "INFO":
                InfoCount++;
                break;
        }

        // Add to diagnostics list
        Diagnostics.Add($"{severity.ToUpperInvariant()}: [{code}] {message}");
    }
}

/// <summary>
/// Context operations for validation.
/// </summary>
internal static class Context
{
    internal static ValidationContext Create()
    {
        return new ValidationContext
        {
            ErrorCount = 0,
            WarningCount = 0,
            InfoCount = 0,
            Diagnostics = new List<string>(),
            SanitizedNameCount = 0,
            InterfaceConformanceIssuesByType = new Dictionary<string, List<string>>()
        };
    }


    internal static string GetDiagnosticDescription(string code)
    {
        return code switch
        {
            DiagnosticCodes.ValidationFailed => "Validation failed",
            DiagnosticCodes.DuplicateMember => "Duplicate members",
            DiagnosticCodes.AmbiguousOverload => "Ambiguous overloads",
            DiagnosticCodes.ReservedWordUnsanitized => "Reserved words not sanitized",
            DiagnosticCodes.CovarianceSummary => "Property covariance (TS limitation)",
            DiagnosticCodes.StructuralConformanceFailure => "Interface conformance failures",
            DiagnosticCodes.CircularInheritance => "Circular inheritance/dependencies",
            DiagnosticCodes.ViewCoverageMismatch => "ViewOnly member coverage issues",
            DiagnosticCodes.IndexerConflict => "Indexer conflicts",
            DiagnosticCodes.InterfaceNotFound => "External interface references",
            DiagnosticCodes.NameConflictUnresolved => "Name conflicts",
            DiagnosticCodes.UnrepresentableConstraint => "Unrepresentable constraints",
            // PhaseGate Hardening diagnostics
            DiagnosticCodes.PostSanitizerUnsanitizedReservedIdentifier => "Reserved identifier not sanitized",
            DiagnosticCodes.DuplicateErasedSurfaceSignature => "Duplicate erased signature",
            DiagnosticCodes.EmptyView => "Empty view (no members)",
            DiagnosticCodes.DuplicateViewForInterface => "Duplicate view for same interface",
            DiagnosticCodes.InvalidViewPropertyName => "Invalid/unsanitized view property name",
            DiagnosticCodes.NonBenignConstraintLoss => "Non-benign constraint loss",
            DiagnosticCodes.ConstructorConstraintLoss => "Constructor constraint loss (override)",
            DiagnosticCodes.InterfaceMethodNotAssignable => "Interface method not assignable (erased)",
            DiagnosticCodes.ViewMemberCollisionInViewScope => "View member collision within view scope",
            DiagnosticCodes.ViewMemberEqualsClassSurface => "View member name shadows class surface",
            DiagnosticCodes.DuplicatePropertyNamePostDedup => "Duplicate property name on class surface",
            DiagnosticCodes.MemberInBothClassAndView => "Member in both ClassSurface and ViewOnly",
            DiagnosticCodes.ClassSurfaceMemberHasSourceInterface => "ClassSurface member has SourceInterface",
            DiagnosticCodes.MissingEmitScopeOrIllegalCombo => "Member has no final placement or illegal combo",
            DiagnosticCodes.ViewOnlyWithoutExactlyOneExplicitView => "ViewOnly member not in exactly one ExplicitView",
            DiagnosticCodes.EmittingMemberMissingFinalName => "Member missing final name in scope after reservation",
            DiagnosticCodes.EmittingTypeMissingFinalName => "Type missing final name in namespace scope",
            DiagnosticCodes.InvalidOrEmptyViewMembership => "Empty/invalid view",
            DiagnosticCodes.DuplicateViewMembership => "Duplicate view membership",
            DiagnosticCodes.ClassViewDualRoleClash => "Class/View dual-role clash",
            DiagnosticCodes.RequiredViewMissingForInterface => "Interface requires view but type has none",
            DiagnosticCodes.PostSanitizerUnsanitizedIdentifier => "Unsanitized identifier post-sanitizer",
            DiagnosticCodes.MalformedScopeKey => "Empty/malformed scope key",
            DiagnosticCodes.ScopeKindMismatchWithEmitScope => "Scope kind doesn't match EmitScope",
            DiagnosticCodes.TypeNamePrinterRenamerMismatch => "Type name mismatch (Printer vs Renamer)",
            DiagnosticCodes.MissingImportForForeignType => "Type used but not imported",
            DiagnosticCodes.ImportedTypeNotExported => "Import references unexported type",
            DiagnosticCodes.HeritageTypeOnlyImport => "Heritage clause uses type-only import (needs value)",
            DiagnosticCodes.QualifiedExportPathInvalid => "Qualified name path doesn't exist in exports",
            DiagnosticCodes.TypeReferenceUnresolvable => "Type reference unresolvable (no import/local/built-in)",
            DiagnosticCodes.GenericArityInconsistent => "Generic arity mismatch (alias vs type)",
            DiagnosticCodes.PublicApiReferencesNonEmittedType => "Public API exposes internal/non-emitted type",
            DiagnosticCodes.GenericConstraintReferencesNonEmittedType => "Generic constraint references non-emitted type",
            DiagnosticCodes.UnsupportedClrSpecialForm => "Unsupported special form (pointer/byref/fnptr)",
            DiagnosticCodes.UnresolvedExternalType => "Unresolved external type reference",
            DiagnosticCodes.MixedPublicKeyTokenForSameName => "Mixed PublicKeyToken for same assembly",
            DiagnosticCodes.VersionDriftForSameIdentity => "Version drift (same assembly, different versions)",
            DiagnosticCodes.RetargetableOrContentTypeAssemblyRef => "Retargetable/ContentType assembly reference",
            _ => "Unknown diagnostic"
        };
    }
}
