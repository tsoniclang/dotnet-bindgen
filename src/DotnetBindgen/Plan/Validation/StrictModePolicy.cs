using System.Collections.Generic;

namespace DotnetBindgen.Plan.Validation;

/// <summary>
/// Strict mode diagnostic policy.
/// Defines which diagnostic codes are allowed in strict mode validation.
///
/// Strict mode philosophy (ZERO WARNINGS):
/// - ERROR: Always forbidden (blocks emission)
/// - WARNING: Always forbidden (strict mode = zero tolerance)
/// - INFO: Always allowed (informational only, doesn't count toward totals)
///
/// When strict mode is enabled, PhaseGate validation fails if ANY WARNING exists.
/// All warnings must be either eliminated or downgraded to INFO with justification.
/// </summary>
public static class StrictModePolicy
{
    /// <summary>
    /// Diagnostic severity levels for strict mode policy.
    /// </summary>
    public enum AllowedLevel
    {
        /// <summary>
        /// Always fails validation (ERROR level).
        /// </summary>
        Forbidden,

        /// <summary>
        /// Always allowed (INFO level).
        /// Informational only - doesn't count toward warning totals.
        /// </summary>
        Informational
    }

    /// <summary>
    /// Central policy mapping: diagnostic code → allowed level in strict mode.
    /// Currently only tracks ERROR codes (Forbidden).
    /// WARNING codes are unconditionally forbidden.
    /// INFO codes are unconditionally allowed.
    /// </summary>
    private static readonly Dictionary<string, (AllowedLevel Level, string Justification)> Policy = new()
    {
        // ============================================================================
        // ERRORS - Always forbidden (strict mode = zero tolerance)
        // ============================================================================

        ["TBG900"] = (AllowedLevel.Forbidden, "StaticFlatteningPlan validity - must be zero"),
        ["TBG901"] = (AllowedLevel.Forbidden, "StaticConflictPlan validity - must be zero"),
        ["TBG902"] = (AllowedLevel.Forbidden, "OverrideConflictPlan validity - must be zero"),
        ["TBG903"] = (AllowedLevel.Forbidden, "PropertyOverridePlan validity - must be zero"),
        ["TBG904"] = (AllowedLevel.Forbidden, "ExtensionMethodsPlan validity - must be zero"),
        ["TBG905"] = (AllowedLevel.Forbidden, "Extension method 'any' erasures - must be zero"),
        ["TBG906"] = (AllowedLevel.Forbidden, "Extension bucket name validity - must be zero"),
        ["TBG907"] = (AllowedLevel.Forbidden, "Extension import resolution - must be zero"),

        // ============================================================================
        // WARNINGS - Unconditionally forbidden (zero tolerance achieved)
        // ============================================================================
        // All previous WARNING codes have been eliminated or downgraded to INFO:
        //
        // TBG120: Reserved word collisions - DOWNGRADED TO INFO
        // (8 instances: System.Enum, System.String, System.Type, System.Boolean, System.Void,
        //  System.Diagnostics.Switch, System.Diagnostics.Debugger, System.Reflection.Module)
        // Always used in qualified contexts - no collision risk
        //
        // TBG201: Circular namespace dependencies - ELIMINATED
        // (was 267 instances, now 0 via SCC bucketing)
        //
        // TBG203: Interface conformance failures - ELIMINATED
        // (was 87 instances, now 0 via honest emission + fixes)

        // ============================================================================
        // INFO - Unconditionally allowed (doesn't count toward totals)
        // ============================================================================
        // INFO diagnostics don't need policy entries - always allowed
        // Current INFO codes:
        // - TBG120: Reserved word collisions (8 core BCL types in qualified contexts)
        // - TBG310: Property covariance (TypeScript language limitation)
        // - TBG410: Narrowed generic constraints (valid TypeScript pattern)
    };

    /// <summary>
    /// Check if a diagnostic code is allowed in strict mode.
    /// </summary>
    /// <param name="code">Diagnostic code (e.g., "TBG310")</param>
    /// <param name="level">Diagnostic level ("ERROR", "WARNING", "INFO")</param>
    /// <returns>True if allowed, false if should fail validation</returns>
    public static bool IsAllowed(string code, string level)
    {
        // INFO is always allowed (doesn't count toward warning totals)
        if (level == "INFO")
            return true;

        // ERROR always fails validation
        if (level == "ERROR")
            return false;

        // WARNING always fails validation (strict mode = zero tolerance)
        if (level == "WARNING")
        {
            // No exceptions - strict mode forbids ALL warnings
            return false;
        }

        // Unknown level - fail safe: disallow
        return false;
    }

    /// <summary>
    /// Get the justification for a diagnostic code.
    /// </summary>
    public static string GetJustification(string code)
    {
        if (Policy.TryGetValue(code, out var policy))
        {
            return policy.Justification;
        }

        return "Strict mode forbids warnings. Downgrade to INFO with justification or fix root cause.";
    }

    /// <summary>
    /// Get all whitelisted warning codes.
    /// INTENTIONALLY EMPTY - strict mode has zero tolerance for warnings.
    /// This method must return empty to maintain zero-warning invariant.
    /// </summary>
    public static IEnumerable<string> GetWhitelistedWarnings()
    {
        // Intentionally empty - zero tolerance policy
        yield break;
    }

    /// <summary>
    /// Get all informational codes currently defined in the policy.
    /// Note: INFO codes don't need policy entries - they're always allowed.
    /// This returns empty because INFO codes aren't stored in the policy dictionary.
    /// </summary>
    public static IEnumerable<string> GetInformationalCodes()
    {
        foreach (var (code, (level, _)) in Policy)
        {
            if (level == AllowedLevel.Informational)
            {
                yield return code;
            }
        }
    }
}
