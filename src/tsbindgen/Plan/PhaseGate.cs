using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;
using tsbindgen.Shape;
using tsbindgen.Plan.Validation;
using ValidationCore = tsbindgen.Plan.Validation.Core;
using VCtx = tsbindgen.Plan.Validation.Context;

namespace tsbindgen.Plan;

/// <summary>
/// Validates the symbol graph before emission.
/// Performs comprehensive validation checks and policy enforcement.
/// Acts as quality gate between Shape/Plan phases and Emit phase.
/// </summary>
public static class PhaseGate
{
    public static void Validate(BuildContext ctx, EmissionPlan plan, InterfaceConstraintFindings constraintFindings)
    {
        var graph = plan.Graph;
        var importsFacade = plan.Imports;
        var importsInternal = plan.ImportsInternal;

        ctx.Log("PhaseGate", "Validating symbol graph before emission...");

        var validationContext = new ValidationContext
        {
            ErrorCount = 0,
            WarningCount = 0,
            Diagnostics = new List<string>(),
            SanitizedNameCount = 0,
            InterfaceConformanceIssuesByType = new Dictionary<string, List<string>>()
        };

        // Run all validation checks (delegated to Validation modules)
        ValidationCore.ValidateTypeNames(ctx, graph, validationContext);
        ValidationCore.ValidateMemberNames(ctx, graph, validationContext);
        ValidationCore.ValidateGenericParameters(ctx, graph, validationContext);
        ValidationCore.ValidateInterfaceConformance(ctx, graph, plan.HonestEmission, validationContext);
        ValidationCore.ValidateInheritance(ctx, graph, validationContext);
        ValidationCore.ValidateEmitScopes(ctx, graph, validationContext);
        ValidationCore.ValidateImports(ctx, graph, importsFacade, plan.SCCBuckets, validationContext);
        ValidationCore.ValidatePolicyCompliance(ctx, graph, validationContext);

        // Step 9: PhaseGate Hardening - Additional validation checks
        Views.Validate(ctx, graph, validationContext);
        Names.ValidateFinalNames(ctx, graph, validationContext);
        Names.ValidateAliases(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M1: Identifier sanitization verification
        Names.ValidateIdentifiers(ctx, graph, validationContext);

        // PhaseGate Hardening - M2: Overload collision detection
        Names.ValidateOverloadCollisions(ctx, graph, validationContext);

        // PhaseGate Hardening - M3: View integrity validation (3 hard rules)
        Views.ValidateIntegrity(ctx, graph, validationContext);

        // PhaseGate Hardening - M4: Constraint findings from InterfaceConstraintAuditor
        Constraints.EmitDiagnostics(ctx, constraintFindings, validationContext);

        // PhaseGate Hardening - M5: View member name scoping (PG_NAME_003, PG_NAME_004)
        Views.ValidateMemberScoping(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: EmitScope invariants (PG_INT_002, PG_INT_003)
        Scopes.ValidateEmitScopeInvariants(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Scope mismatches (PG_SCOPE_003, PG_SCOPE_004) - Step 6
        Scopes.ValidateScopeMismatches(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Class surface uniqueness (PG_NAME_005)
        Names.ValidateClassSurfaceUniqueness(ctx, graph, validationContext);

        // PhaseGate Hardening - M6: Comprehensive finalization sweep (PG_FIN_001 through PG_FIN_010)
        // This is the FINAL validation before emission - catches any symbol without proper finalization
        Finalization.Validate(ctx, graph, plan.ExtensionMethods, validationContext);

        // PhaseGate Hardening - M6a: CLR surface name policy (PG_NAME_SURF_001)
        // Validates that class members match interface members using CLR-name contract
        Names.ValidateClrSurfaceNamePolicy(ctx, graph, validationContext);

        // NOTE: PG_NAME_SURF_002 (numeric suffix validation) is disabled in CLR-name contract model
        // With CLR-name contract, we emit CLR names directly (ToInt32, ToUInt16, etc.)
        // Numeric suffixes in CLR names are legitimate, not collision-resolution artifacts
        // Names.ValidateNoNumericSuffixesOnSurface(ctx, graph, validationContext);

        // PhaseGate Hardening - M6b: Alias/instance naming pattern (PG_NAME_ALIAS_001)
        // Validates Step B invariants: type positions use aliases, value positions use instances
        // Ensures universal T$instance naming is applied consistently across all classes/structs
        Names.ValidateAliasInstancePattern(ctx, graph, validationContext);

        // PhaseGate Hardening - M7: Printer name consistency (PG_PRINT_001)
        // Validates TypeRefPrinter→TypeNameResolver→Renamer chain integrity
        Types.ValidatePrinterNameConsistency(ctx, graph, validationContext);

        // PhaseGate Hardening - M7a: TypeMap validation (PG_TYPEMAP_001)
        // Validates no unsupported special forms (pointers, byrefs, function pointers)
        // MUST RUN EARLY - before other type reference validation
        Types.ValidateTypeMapCompliance(ctx, graph, validationContext);

        // PhaseGate Hardening - M7b: External type resolution (PG_LOAD_001)
        // Validates all external type references are either in TypeIndex or built-in
        // MUST RUN AFTER TypeMap check, BEFORE API surface validation
        Types.ValidateExternalTypeResolution(ctx, graph, validationContext);

        // PhaseGate Hardening - M7c: Type reference resolution (PG_REF_001)
        // Validates all type references can be resolved via import/local/built-in
        // Catches TS2304 "Cannot find name" at planning time
        Types.ValidateTypeReferenceResolution(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M7d: Generic arity consistency (PG_ARITY_001)
        // Validates generic type arity matches across aliases and type references
        // Catches TS2315 "Type is not generic" at planning time
        Types.ValidateGenericArityConsistency(ctx, graph, importsFacade, validationContext);

        // Validate internal import plan too (airplane-grade internal/index.d.ts correctness).
        // This catches library-mode issues where facade imports are type-correct but internal imports
        // (used by InternalIndexEmitter) would be missing or mismatched.
        ValidationCore.ValidateImports(ctx, graph, importsInternal, plan.SCCBuckets, validationContext);
        Names.ValidateAliases(ctx, graph, importsInternal, validationContext);
        Types.ValidateTypeReferenceResolution(ctx, graph, importsInternal, validationContext);
        Types.ValidateGenericArityConsistency(ctx, graph, importsInternal, validationContext);

        // PhaseGate Hardening - M8: Public API surface validation (PG_API_001, PG_API_002)
        // Validates public APIs don't reference non-emitted/internal types
        // THIS MUST RUN BEFORE PG_IMPORT_001 - it's more fundamental
        ImportExport.ValidatePublicApiSurface(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M9: Import completeness (PG_IMPORT_001)
        // Validates every foreign type used in signatures has a corresponding import
        ImportExport.ValidateImportCompleteness(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M10: Export completeness (PG_EXPORT_001)
        // Validates imported types are actually exported by source namespaces
        ImportExport.ValidateExportCompleteness(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M11: Extension import completeness (PG_EXT_IMPORT_001)
        // Validates extension bucket imports are resolvable
        ImportExport.ValidateExtensionImportCompleteness(ctx, graph, plan.ExtensionMethods, importsFacade, validationContext);

        // PhaseGate Hardening - M17: Heritage value imports (PG_IMPORT_002)
        // Validates base classes and interfaces in heritage clauses use value imports (not type-only)
        ImportExport.ValidateHeritageValueImports(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M18: Qualified export paths (PG_EXPORT_002)
        // Validates qualified names like 'System_Internal.System.Exception$instance' have valid export paths
        ImportExport.ValidateQualifiedExportPaths(ctx, graph, importsFacade, validationContext);

        // PhaseGate Hardening - M19: Plan integrity (PG_PLAN_001 through PG_PLAN_005)
        // Validates all Shape plans reference valid types/members and are consistent with graph
        PlanIntegrity.Validate(ctx, graph, plan, validationContext);

        // PhaseGate Hardening - M20: Extension method conformance (PG_EXT_001, PG_EXT_002)
        // Validates extension method buckets have correct arity and no erased 'any' types
        Validation.Extensions.Validate(ctx, graph, plan.ExtensionMethods, importsFacade, validationContext);

        // Library Mode Validation (LIB001-003)
        // Only run when in library mode (BuildContext.LibraryContract is non-null)
        if (ctx.LibraryContract != null)
        {
            ctx.Log("PhaseGate", "Running library mode validation...");
            // LIB001 is validated during contract loading (LibraryContractLoader.Load)
            LibraryMode.ValidateNoDanglingReferences(graph, ctx.LibraryContract, validationContext);
        }

        // Strict Mode Enforcement
        if (ctx.StrictMode)
        {
            EnforceStrictMode(ctx, validationContext);
        }

        // Report results
        ctx.Log("PhaseGate", $"Validation complete - {validationContext.ErrorCount} errors, {validationContext.WarningCount} warnings, {validationContext.InfoCount} info");
        ctx.Log("PhaseGate", $"Sanitized {validationContext.SanitizedNameCount} reserved word identifiers");

        // Print diagnostic summary table
        if (validationContext.DiagnosticCountsByCode.Count > 0)
        {
            ctx.Log("PhaseGate", "");
            ctx.Log("PhaseGate", "Diagnostic Summary by Code:");
            ctx.Log("PhaseGate", "─────────────────────────────────────────");

            foreach (var (code, count) in validationContext.DiagnosticCountsByCode.OrderByDescending(kvp => kvp.Value))
            {
                var description = VCtx.GetDiagnosticDescription(code);
                ctx.Log("PhaseGate", $"  {code}: {count,5} - {description}");
            }

            ctx.Log("PhaseGate", "─────────────────────────────────────────");
        }

        if (validationContext.ErrorCount > 0)
        {
            // Build sample diagnostics message for error output - show ERRORS first
            var errors = validationContext.Diagnostics.Where(d => d.StartsWith("ERROR:")).ToList();
            var warnings = validationContext.Diagnostics.Where(d => d.StartsWith("WARNING:")).ToList();

            var sample = errors.Take(20).ToList();
            var sampleText = string.Join("\n", sample.Select(d => $"  {d}"));

            if (errors.Count > 20)
            {
                sampleText += $"\n  ... and {errors.Count - 20} more errors";
            }

            if (warnings.Count > 0)
            {
                sampleText += $"\n\n  ({warnings.Count} warnings suppressed from this display)";
            }

            ctx.Diagnostics.Error(Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                $"PhaseGate validation failed with {validationContext.ErrorCount} errors\n\nSample errors (first 20):\n{sampleText}");
        }

        // Record all diagnostics for later analysis
        foreach (var diagnostic in validationContext.Diagnostics)
        {
            ctx.Log("PhaseGate", diagnostic);
        }
    }

    /// <summary>
    /// Enforce strict mode policy: fail validation if any non-whitelisted warnings exist.
    /// INFO diagnostics are always allowed and don't count toward warning totals.
    /// </summary>
    private static void EnforceStrictMode(BuildContext ctx, ValidationContext validationContext)
    {
        ctx.Log("PhaseGate", "Enforcing strict mode policy (zero non-whitelisted warnings)...");

        var violations = new List<(string Code, int Count, string Justification)>();

        // Check each diagnostic code
        foreach (var (code, count) in validationContext.DiagnosticCountsByCode)
        {
            // Determine level from diagnostics
            var levelFromDiagnostics = validationContext.Diagnostics
                .Where(d => d.Contains($"[{code}]"))
                .Select(d =>
                {
                    if (d.StartsWith("ERROR:")) return "ERROR";
                    if (d.StartsWith("WARNING:")) return "WARNING";
                    if (d.StartsWith("INFO:")) return "INFO";
                    return "UNKNOWN";
                })
                .FirstOrDefault() ?? "UNKNOWN";

            // Check if allowed in strict mode
            if (!StrictModePolicy.IsAllowed(code, levelFromDiagnostics))
            {
                var justification = StrictModePolicy.GetJustification(code);
                violations.Add((code, count, justification));
            }
        }

        if (violations.Count > 0)
        {
            var totalViolations = violations.Sum(v => v.Count);
            var violationText = string.Join("\n", violations.Select(v =>
                $"  {v.Code}: {v.Count} instances - {v.Justification}"));

            ctx.Log("PhaseGate", $"Strict mode violations: {totalViolations} total");
            ctx.Log("PhaseGate", violationText);

            // Convert to ERROR
            validationContext.RecordDiagnostic(
                Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                "ERROR",
                $"Strict mode enforced: {totalViolations} non-whitelisted warnings detected.\n\n" +
                $"Violations:\n{violationText}\n\n" +
                $"See docs/strict-mode-diagnostics.md for policy details.");
        }
        else
        {
            ctx.Log("PhaseGate", "✓ Strict mode: No non-whitelisted warnings");
        }
    }
}
