using System.Collections.Generic;
using System.Linq;
using tsbindgen.Analysis;
using tsbindgen.Core.Diagnostics;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Types;
using tsbindgen.Renaming;
using tsbindgen.Shape;

namespace tsbindgen.Plan.Validation;

/// <summary>
/// Plan Integrity validation functions.
/// Validates that all Shape plans (StaticFlattening, StaticConflict, OverrideConflict,
/// PropertyOverride, ExtensionMethods) are consistent with the symbol graph.
/// Implements PG_PLAN_001 through PG_PLAN_005.
/// </summary>
internal static class PlanIntegrity
{
    /// <summary>
    /// Validates all Shape plans for integrity.
    /// Ensures plan references are valid and consistent with the symbol graph.
    /// </summary>
    internal static void Validate(
        BuildContext ctx,
        SymbolGraph graph,
        EmissionPlan emissionPlan,
        ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Running plan integrity validation (PG_PLAN_001-005)...");

        // PG_PLAN_001: StaticFlattening target validity
        ValidateStaticFlatteningPlan(ctx, graph, emissionPlan.StaticFlattening, validationCtx);

        // PG_PLAN_002: StaticConflict validity
        ValidateStaticConflictPlan(ctx, graph, emissionPlan.StaticConflicts, validationCtx);

        // PG_PLAN_003: OverrideConflict validity
        ValidateOverrideConflictPlan(ctx, graph, emissionPlan.OverrideConflicts, validationCtx);

        // PG_PLAN_004: PropertyOverride validity
        ValidatePropertyOverridePlan(ctx, graph, emissionPlan.PropertyOverrides, validationCtx);

        // PG_PLAN_005: ExtensionMethodsPlan validity
        ValidateExtensionMethodsPlan(ctx, graph, emissionPlan.ExtensionMethods, validationCtx);

        ctx.Log("PhaseGate", "Plan integrity validation complete");
    }

    /// <summary>
    /// PG_PLAN_001: Validates StaticFlatteningPlan target validity.
    /// Every type StableId in StaticFlatteningPlan must exist in graph.TypeIndex.
    /// Each referenced inherited static member must exist in some base type and be static.
    /// </summary>
    private static void ValidateStaticFlatteningPlan(
        BuildContext ctx,
        SymbolGraph graph,
        StaticFlatteningPlan plan,
        ValidationContext validationCtx)
    {
        foreach (var typeStableId in plan.TypesRequiringFlattening)
        {
            // Extract ClrFullName from StableId (format: "AssemblyName:ClrFullName")
            var clrFullName = typeStableId.Contains(':')
                ? typeStableId.Substring(typeStableId.IndexOf(':') + 1)
                : typeStableId;

            // Check type exists in TypeIndex (uses ClrFullName as key)
            if (!graph.TypeIndex.TryGetValue(clrFullName, out var type))
            {
                validationCtx.RecordDiagnostic(
                    "TBG900",
                    "ERROR",
                    $"[PG_PLAN_001] StaticFlatteningPlan references non-existent type: {typeStableId}");
                continue;
            }

            // Check inherited members exist and are static
            if (plan.InheritedStaticMembers.TryGetValue(typeStableId, out var inheritedMembers))
            {
                // Get the full base class hierarchy
                var baseTypes = GetBaseClassChain(type, graph);

                foreach (var method in inheritedMembers.Methods)
                {
                    if (!MemberExistsInBases(method.StableId, baseTypes, isStatic: true, out var foundInBase))
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references non-existent static method: {method.ClrName}");
                    }
                    else if (!foundInBase)
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references method {method.ClrName} which is not static");
                    }
                }

                foreach (var property in inheritedMembers.Properties)
                {
                    if (!MemberExistsInBases(property.StableId, baseTypes, isStatic: true, out var foundInBase))
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references non-existent static property: {property.ClrName}");
                    }
                    else if (!foundInBase)
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references property {property.ClrName} which is not static");
                    }
                }

                foreach (var field in inheritedMembers.Fields)
                {
                    if (!MemberExistsInBases(field.StableId, baseTypes, isStatic: true, out var foundInBase))
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references non-existent static field: {field.ClrName}");
                    }
                    else if (!foundInBase)
                    {
                        validationCtx.RecordDiagnostic(
                            "TBG900",
                            "ERROR",
                            $"[PG_PLAN_001] StaticFlatteningPlan for {type.ClrFullName} references field {field.ClrName} which is not static");
                    }
                }
            }
        }
    }

    /// <summary>
    /// PG_PLAN_002: Validates StaticConflictPlan validity.
    /// Every hybrid type StableId in StaticConflictPlan exists.
    /// Each suppressed name matches an actual static member on the derived type.
    /// </summary>
    private static void ValidateStaticConflictPlan(
        BuildContext ctx,
        SymbolGraph graph,
        StaticConflictPlan plan,
        ValidationContext validationCtx)
    {
        foreach (var (typeStableId, suppressedMemberIds) in plan.SuppressedMembers)
        {
            // Extract ClrFullName from StableId (format: "AssemblyName:ClrFullName")
            var clrFullName = typeStableId.Contains(':')
                ? typeStableId.Substring(typeStableId.IndexOf(':') + 1)
                : typeStableId;

            // Check type exists in TypeIndex (uses ClrFullName as key)
            if (!graph.TypeIndex.TryGetValue(clrFullName, out var type))
            {
                validationCtx.RecordDiagnostic(
                    "TBG901",
                    "ERROR",
                    $"[PG_PLAN_002] StaticConflictPlan references non-existent type: {typeStableId}");
                continue;
            }

            // Check each suppressed member exists and is static
            foreach (var memberStableId in suppressedMemberIds)
            {
                var found = FindMemberInType(type, memberStableId, out var isStatic);
                if (!found)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG901",
                        "ERROR",
                        $"[PG_PLAN_002] StaticConflictPlan for {type.ClrFullName} suppresses non-existent member: {memberStableId}");
                }
                else if (!isStatic)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG901",
                        "ERROR",
                        $"[PG_PLAN_002] StaticConflictPlan for {type.ClrFullName} suppresses non-static member: {memberStableId}");
                }
            }
        }
    }

    /// <summary>
    /// PG_PLAN_003: Validates OverrideConflictPlan validity.
    /// Every derived type StableId in OverrideConflictPlan exists.
    /// Each suppressed name matches an actual instance member on the derived type.
    /// </summary>
    private static void ValidateOverrideConflictPlan(
        BuildContext ctx,
        SymbolGraph graph,
        OverrideConflictPlan plan,
        ValidationContext validationCtx)
    {
        foreach (var (typeStableId, suppressedMemberIds) in plan.SuppressedMembers)
        {
            // Extract ClrFullName from StableId (format: "AssemblyName:ClrFullName")
            var clrFullName = typeStableId.Contains(':')
                ? typeStableId.Substring(typeStableId.IndexOf(':') + 1)
                : typeStableId;

            // Check type exists in TypeIndex (uses ClrFullName as key)
            if (!graph.TypeIndex.TryGetValue(clrFullName, out var type))
            {
                validationCtx.RecordDiagnostic(
                    "TBG902",
                    "ERROR",
                    $"[PG_PLAN_003] OverrideConflictPlan references non-existent type: {typeStableId}");
                continue;
            }

            // Check each suppressed member exists and is an instance member
            foreach (var memberStableId in suppressedMemberIds)
            {
                var found = FindMemberInType(type, memberStableId, out var isStatic);
                if (!found)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG902",
                        "ERROR",
                        $"[PG_PLAN_003] OverrideConflictPlan for {type.ClrFullName} suppresses non-existent member: {memberStableId}");
                }
                else if (isStatic)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG902",
                        "ERROR",
                        $"[PG_PLAN_003] OverrideConflictPlan for {type.ClrFullName} suppresses static member (should be instance): {memberStableId}");
                }
            }
        }
    }

    /// <summary>
    /// PG_PLAN_004: Validates PropertyOverridePlan validity.
    /// Every property StableId referenced exists and is a property in graph.
    /// Union type string must not contain bare generic params (T, TKey, etc.)
    /// </summary>
    private static void ValidatePropertyOverridePlan(
        BuildContext ctx,
        SymbolGraph graph,
        PropertyOverridePlan plan,
        ValidationContext validationCtx)
    {
        // Generic type parameter names that should be filtered (safety filter)
        var genericParamNames = new HashSet<string> { "T", "TKey", "TValue", "TResult", "TSource", "TElement", "TOutput", "TInput" };

        foreach (var ((typeStableId, propertyStableId), unionTypeString) in plan.PropertyTypeOverrides)
        {
            // Extract ClrFullName from StableId (format: "AssemblyName:ClrFullName")
            var clrFullName = typeStableId.Contains(':')
                ? typeStableId.Substring(typeStableId.IndexOf(':') + 1)
                : typeStableId;

            // Check type exists in TypeIndex (uses ClrFullName as key)
            if (!graph.TypeIndex.TryGetValue(clrFullName, out var type))
            {
                validationCtx.RecordDiagnostic(
                    "TBG903",
                    "ERROR",
                    $"[PG_PLAN_004] PropertyOverridePlan references non-existent type: {typeStableId}");
                continue;
            }

            // Check property exists and is a property
            var property = type.Members.Properties.FirstOrDefault(p => p.StableId.ToString() == propertyStableId);
            if (property == null)
            {
                validationCtx.RecordDiagnostic(
                    "TBG903",
                    "ERROR",
                    $"[PG_PLAN_004] PropertyOverridePlan for {type.ClrFullName} references non-existent property: {propertyStableId}");
                continue;
            }

            // Check union type doesn't contain bare generic parameters
            // Union format: "Type1 | Type2 | Type3"
            var unionParts = unionTypeString.Split('|').Select(s => s.Trim()).ToList();
            foreach (var part in unionParts)
            {
                if (genericParamNames.Contains(part))
                {
                    validationCtx.RecordDiagnostic(
                        "TBG903",
                        "ERROR",
                        $"[PG_PLAN_004] PropertyOverridePlan for {type.ClrFullName}.{property.ClrName} contains bare generic parameter '{part}' in union type: {unionTypeString}");
                }
            }
        }
    }

    /// <summary>
    /// PG_PLAN_005: Validates ExtensionMethodsPlan validity.
    /// Every bucket's target type key resolves to an existing type in TypeIndex or is a built-in/ambient external type.
    /// Every extension method in buckets has IsExtensionMethod = true and non-null ExtensionTarget.
    /// </summary>
    private static void ValidateExtensionMethodsPlan(
        BuildContext ctx,
        SymbolGraph graph,
        ExtensionMethodsPlan plan,
        ValidationContext validationCtx)
    {
        // Built-in/ambient types that extension methods might target (primitives, arrays, etc.)
        var builtInTypes = new HashSet<string>
        {
            "System.String",
            "System.Object",
            "System.Array",
            "System.Int32",
            "System.Int64",
            "System.Double",
            "System.Boolean",
            // Add other common built-ins as needed
        };

        foreach (var bucket in plan.Buckets)
        {
            // Check target type exists (either in graph or is built-in)
            var targetExists =
                graph.TypeIndex.ContainsKey(bucket.Key.FullName)
                || builtInTypes.Contains(bucket.Key.FullName)
                // Library mode: target types can be external (filtered) but still valid.
                || (ctx.LibraryNamespaceIndex?.ContainsKey(bucket.Key.FullName) ?? false)
                || (ctx.LibraryContract?.AllowedClrFullNames.Contains(bucket.Key.FullName) ?? false);

            if (!targetExists)
            {
                validationCtx.RecordDiagnostic(
                    "TBG904",
                    "WARNING",
                    $"[PG_PLAN_005] ExtensionMethodsPlan bucket for '{bucket.Key.FullName}' targets type not in graph (may be external)");
            }

            // Check each extension method
            foreach (var method in bucket.Methods)
            {
                if (!method.IsExtensionMethod)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG904",
                        "ERROR",
                        $"[PG_PLAN_005] ExtensionMethodsPlan bucket '{bucket.BucketInterfaceName}' contains method {method.ClrName} with IsExtensionMethod = false");
                }

                if (method.ExtensionTarget == null)
                {
                    validationCtx.RecordDiagnostic(
                        "TBG904",
                        "ERROR",
                        $"[PG_PLAN_005] ExtensionMethodsPlan bucket '{bucket.BucketInterfaceName}' contains method {method.ClrName} with null ExtensionTarget");
                }
            }
        }
    }

    // Helper methods

    private static List<TypeSymbol> GetBaseClassChain(TypeSymbol type, SymbolGraph graph)
    {
        var bases = new List<TypeSymbol>();
        var current = type;

        while (current.BaseType != null)
        {
            var baseType = FindTypeByReference(graph, current.BaseType);
            if (baseType == null)
                break;

            bases.Add(baseType);
            current = baseType;
        }

        return bases;
    }

    private static bool MemberExistsInBases(StableId memberStableId, List<TypeSymbol> baseTypes, bool isStatic, out bool matchesStaticRequirement)
    {
        matchesStaticRequirement = false;

        foreach (var baseType in baseTypes)
        {
            // Check methods
            var method = baseType.Members.Methods.FirstOrDefault(m => m.StableId == memberStableId);
            if (method != null)
            {
                matchesStaticRequirement = method.IsStatic == isStatic;
                return true;
            }

            // Check properties
            var property = baseType.Members.Properties.FirstOrDefault(p => p.StableId == memberStableId);
            if (property != null)
            {
                matchesStaticRequirement = property.IsStatic == isStatic;
                return true;
            }

            // Check fields
            var field = baseType.Members.Fields.FirstOrDefault(f => f.StableId == memberStableId);
            if (field != null)
            {
                matchesStaticRequirement = field.IsStatic == isStatic;
                return true;
            }
        }

        return false;
    }

    private static bool FindMemberInType(TypeSymbol type, string memberStableId, out bool isStatic)
    {
        isStatic = false;

        // Check methods
        var method = type.Members.Methods.FirstOrDefault(m => m.StableId.ToString() == memberStableId);
        if (method != null)
        {
            isStatic = method.IsStatic;
            return true;
        }

        // Check properties
        var property = type.Members.Properties.FirstOrDefault(p => p.StableId.ToString() == memberStableId);
        if (property != null)
        {
            isStatic = property.IsStatic;
            return true;
        }

        // Check fields
        var field = type.Members.Fields.FirstOrDefault(f => f.StableId.ToString() == memberStableId);
        if (field != null)
        {
            isStatic = field.IsStatic;
            return true;
        }

        return false;
    }

    private static TypeSymbol? FindTypeByReference(SymbolGraph graph, TypeReference typeRef)
    {
        if (typeRef is not NamedTypeReference namedRef)
            return null;

        return graph.TypeIndex.Values.FirstOrDefault(t => t.ClrFullName == namedRef.FullName);
    }
}
