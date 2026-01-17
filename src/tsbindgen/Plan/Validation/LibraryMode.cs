using tsbindgen.Library;
using tsbindgen.Model;
using tsbindgen.Model.Types;
using tsbindgen.Model.Symbols.MemberSymbols;

namespace tsbindgen.Plan.Validation;

/// <summary>
/// Validation rules for library mode.
/// Ensures library contract consistency and prevents dangling references.
/// </summary>
internal static class LibraryMode
{
    // LIB001: Library contract path validation
    // This validation is performed during contract loading (LibraryContractLoader.Load)
    // Checks: directory exists, has bindings.json files
    // If contract loading fails, build terminates before reaching PhaseGate

    /// <summary>
    /// LIB002: Validate that filtered output contains no dangling references.
    /// Library subset must be self-contained - all referenced types must be:
    /// 1. In the library contract (AllowedTypeStableIds), OR
    /// 2. Built-in/primitive types (mapped to TS primitives), OR
    /// 3. External types (not in current graph TypeIndex)
    /// </summary>
    internal static void ValidateNoDanglingReferences(
        SymbolGraph graph,
        LibraryContract contract,
        ValidationContext validationCtx)
    {
        var danglingCount = 0;

        // Walk all emitted types and their members
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check base type
                if (type.BaseType != null)
                {
                    CheckTypeReference(type.BaseType, type.StableId.ToString(), "base type", graph, contract, validationCtx, ref danglingCount);
                }

                // Check interfaces
                foreach (var iface in type.Interfaces)
                {
                    CheckTypeReference(iface, type.StableId.ToString(), "interface", graph, contract, validationCtx, ref danglingCount);
                }

                // Check generic parameters constraints
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                    {
                        CheckTypeReference(constraint, type.StableId.ToString(), $"generic constraint on {gp.Name}", graph, contract, validationCtx, ref danglingCount);
                    }
                }

                // Check all members
                foreach (var method in type.Members.Methods)
                {
                    CheckMemberSignature(method, graph, contract, validationCtx, ref danglingCount);
                }

                foreach (var prop in type.Members.Properties)
                {
                    CheckTypeReference(prop.PropertyType, prop.StableId.ToString(), "property type", graph, contract, validationCtx, ref danglingCount);
                }

                foreach (var field in type.Members.Fields)
                {
                    CheckTypeReference(field.FieldType, field.StableId.ToString(), "field type", graph, contract, validationCtx, ref danglingCount);
                }

                foreach (var evt in type.Members.Events)
                {
                    CheckTypeReference(evt.EventHandlerType, evt.StableId.ToString(), "event handler type", graph, contract, validationCtx, ref danglingCount);
                }

                foreach (var ctor in type.Members.Constructors)
                {
                    foreach (var param in ctor.Parameters)
                    {
                        CheckTypeReference(param.Type, ctor.StableId.ToString(), $"constructor parameter {param.Name}", graph, contract, validationCtx, ref danglingCount);
                    }
                }
            }
        }
    }

    private static void CheckMemberSignature(MethodSymbol method, SymbolGraph graph, LibraryContract contract, ValidationContext validationCtx, ref int danglingCount)
    {
        // Check return type
        CheckTypeReference(method.ReturnType, method.StableId.ToString(), "return type", graph, contract, validationCtx, ref danglingCount);

        // Check parameters
        foreach (var param in method.Parameters)
        {
            CheckTypeReference(param.Type, method.StableId.ToString(), $"parameter {param.Name}", graph, contract, validationCtx, ref danglingCount);
        }

        // Check generic parameters constraints
        foreach (var gp in method.GenericParameters)
        {
            foreach (var constraint in gp.Constraints)
            {
                CheckTypeReference(constraint, method.StableId.ToString(), $"generic constraint on {gp.Name}", graph, contract, validationCtx, ref danglingCount);
            }
        }
    }

    /// <summary>
    /// Determine actionable fix direction for a dangling reference.
    /// </summary>
    private static string DetermineFixDirection(string missingStableId)
    {
        // Check if it's a BCL type (common case)
        if (missingStableId.StartsWith("System.Private.CoreLib:") ||
            missingStableId.StartsWith("System.Runtime:") ||
            missingStableId.StartsWith("System.Collections:") ||
            missingStableId.Contains(":System."))
        {
            return "Add BCL types to --lib package OR remove dependency on this BCL type";
        }

        // User assembly dependency
        return "Add dependency assembly to --lib package OR remove/replace this dependency";
    }

    private static void CheckTypeReference(
        TypeReference typeRef,
        string referencingMember,
        string context,
        SymbolGraph graph,
        LibraryContract contract,
        ValidationContext validationCtx,
        ref int danglingCount)
    {
        // Walk type reference tree
        var visited = new HashSet<TypeReference>();
        CheckTypeReferenceRecursive(typeRef, referencingMember, context, graph, contract, validationCtx, ref danglingCount, visited);
    }

    private static void CheckTypeReferenceRecursive(
        TypeReference typeRef,
        string referencingMember,
        string context,
        SymbolGraph graph,
        LibraryContract contract,
        ValidationContext validationCtx,
        ref int danglingCount,
        HashSet<TypeReference> visited)
    {
        if (!visited.Add(typeRef))
            return; // Already checked this reference

        switch (typeRef)
        {
            case NamedTypeReference named:
                // Get StableId
                var stableId = $"{named.AssemblyName}:{named.FullName}";

                // Check if it's in contract
                if (!contract.AllowedTypeStableIds.Contains(stableId))
                {
                    // Check if it's in current graph (means we filtered it out)
                    if (graph.TypeIndex.TryGetValue(stableId, out _))
                    {
                        // ERROR: Referenced a type that exists but was filtered out
                        // Provide actionable guidance
                        var fixDirection = DetermineFixDirection(stableId);
                        validationCtx.RecordDiagnostic(
                            "LIB002",
                            "ERROR",
                            $"Dangling reference detected:\n" +
                            $"  User member:     {referencingMember}\n" +
                            $"  References:      {stableId}\n" +
                            $"  Location:        {context}\n" +
                            $"  Fix:             {fixDirection}");
                        danglingCount++;
                    }
                    // else: External type (not in graph) - allowed
                }

                // Recursively check type arguments
                foreach (var typeArg in named.TypeArguments)
                {
                    CheckTypeReferenceRecursive(typeArg, referencingMember, $"{context} type argument", graph, contract, validationCtx, ref danglingCount, visited);
                }
                break;

            case ArrayTypeReference array:
                CheckTypeReferenceRecursive(array.ElementType, referencingMember, $"{context} array element", graph, contract, validationCtx, ref danglingCount, visited);
                break;

            case PointerTypeReference pointer:
                CheckTypeReferenceRecursive(pointer.PointeeType, referencingMember, $"{context} pointer element", graph, contract, validationCtx, ref danglingCount, visited);
                break;

            case ByRefTypeReference byRef:
                CheckTypeReferenceRecursive(byRef.ReferencedType, referencingMember, $"{context} byref element", graph, contract, validationCtx, ref danglingCount, visited);
                break;

            case GenericParameterReference:
                // Generic parameters don't need validation - they're bound by constraints
                break;

            case PlaceholderTypeReference:
                // Placeholders are handled separately
                break;
        }
    }

    /// <summary>
    /// LIB003: Validate binding consistency.
    /// Ensures emitted surface exactly matches binding surface:
    /// - E = emitted member StableIds
    /// - B = contract binding StableIds
    /// - Must have E == B exactly
    /// </summary>
    internal static void ValidateBindingConsistency(
        SymbolGraph graph,
        LibraryContract contract,
        ValidationContext validationCtx)
    {
        // Collect all emitted member StableIds
        var emittedMemberStableIds = new HashSet<string>();

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Add all member StableIds
                foreach (var method in type.Members.Methods)
                {
                    emittedMemberStableIds.Add(method.StableId.ToString());
                }

                foreach (var prop in type.Members.Properties)
                {
                    emittedMemberStableIds.Add(prop.StableId.ToString());
                }

                foreach (var field in type.Members.Fields)
                {
                    emittedMemberStableIds.Add(field.StableId.ToString());
                }

                foreach (var evt in type.Members.Events)
                {
                    emittedMemberStableIds.Add(evt.StableId.ToString());
                }

                foreach (var ctor in type.Members.Constructors)
                {
                    emittedMemberStableIds.Add(ctor.StableId.ToString());
                }
            }
        }

        // Check E ⊆ B (all emitted members have bindings)
        var missingBindings = emittedMemberStableIds.Except(contract.AllowedBindingStableIds).ToList();
        foreach (var stableId in missingBindings.Take(20)) // Limit to first 20
        {
            validationCtx.RecordDiagnostic(
                "LIB003",
                "ERROR",
                $"Emitted member missing binding: {stableId}");
        }

        if (missingBindings.Count > 20)
        {
            validationCtx.RecordDiagnostic(
                "LIB003",
                "ERROR",
                $"... and {missingBindings.Count - 20} more emitted members without bindings");
        }

        // Check B ⊆ E (all bindings point to emitted members)
        var danglingBindings = contract.AllowedBindingStableIds.Except(emittedMemberStableIds).ToList();
        foreach (var stableId in danglingBindings.Take(20)) // Limit to first 20
        {
            validationCtx.RecordDiagnostic(
                "LIB003",
                "ERROR",
                $"Binding references non-emitted member: {stableId}");
        }

        if (danglingBindings.Count > 20)
        {
            validationCtx.RecordDiagnostic(
                "LIB003",
                "ERROR",
                $"... and {danglingBindings.Count - 20} more bindings without emitted members");
        }
    }
}
