using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Shape;

/// <summary>
/// D2: Detects static member conflicts between base and derived classes.
///
/// When a derived class has a static member with the same name as a base class
/// static member but with an incompatible signature/type, TypeScript reports TS2417.
///
/// Solution: Suppress the conflicting static member in the derived class.
/// The base class's static member will be accessible via the base class name.
/// </summary>
public static class StaticConflictDetector
{
    /// <summary>
    /// Analyzes the symbol graph and identifies static member conflicts.
    /// Returns a plan indicating which members should be suppressed.
    /// </summary>
    public static StaticConflictPlan Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("StaticConflictDetector", "Analyzing static member conflicts...");

        var plan = new StaticConflictPlan();
        var conflictCount = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Skip types not in TypeIndex (platform-specific intrinsics, internal types, etc.)
                if (!graph.IsEmittableType(type.StableId.ToString()))
                    continue;

                // Only check classes with base types
                if (type.Kind != TypeKind.Class || type.BaseType == null)
                    continue;

                // Find base type in graph
                var baseType = FindTypeByReference(graph, type.BaseType);
                if (baseType == null)
                    continue;

                // Check for static property conflicts
                foreach (var prop in type.Members.Properties.Where(p => p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
                {
                    var baseProp = FindStaticProperty(baseType, prop.ClrName);
                    if (baseProp != null && HasPropertyConflict(prop, baseProp))
                    {
                        plan.AddSuppression(type, prop.StableId.ToString(), $"Static property '{prop.ClrName}' conflicts with base class");
                        conflictCount++;
                        ctx.Log("StaticConflictDetector",
                            $"  Conflict: {type.ClrFullName}.{prop.ClrName} (property type mismatch)");
                    }
                }

                // Check for static method conflicts
                foreach (var method in type.Members.Methods.Where(m => m.IsStatic && m.EmitScope == EmitScope.ClassSurface))
                {
                    var baseMethods = FindStaticMethods(baseType, method.ClrName);
                    if (baseMethods.Count > 0 && HasMethodConflict(method, baseMethods))
                    {
                        plan.AddSuppression(type, method.StableId.ToString(), $"Static method '{method.ClrName}' conflicts with base class");
                        conflictCount++;
                        ctx.Log("StaticConflictDetector",
                            $"  Conflict: {type.ClrFullName}.{method.ClrName} (signature mismatch)");
                    }
                }

                // Check for static field conflicts
                foreach (var field in type.Members.Fields.Where(f => f.IsStatic && f.EmitScope == EmitScope.ClassSurface))
                {
                    var baseField = FindStaticField(baseType, field.ClrName);
                    if (baseField != null && HasFieldConflict(field, baseField))
                    {
                        plan.AddSuppression(type, field.StableId.ToString(), $"Static field '{field.ClrName}' conflicts with base class");
                        conflictCount++;
                        ctx.Log("StaticConflictDetector",
                            $"  Conflict: {type.ClrFullName}.{field.ClrName} (field type mismatch)");
                    }
                }
            }
        }

        ctx.Log("StaticConflictDetector", $"Found {conflictCount} static member conflicts");
        return plan;
    }

    /// <summary>
    /// Find a static property in a type by CLR name.
    /// </summary>
    private static PropertySymbol? FindStaticProperty(TypeSymbol type, string clrName)
    {
        return type.Members.Properties.FirstOrDefault(p => p.IsStatic && p.ClrName == clrName);
    }

    /// <summary>
    /// Find all static methods in a type with a given CLR name (handles overloads).
    /// </summary>
    private static List<MethodSymbol> FindStaticMethods(TypeSymbol type, string clrName)
    {
        return type.Members.Methods.Where(m => m.IsStatic && m.ClrName == clrName).ToList();
    }

    /// <summary>
    /// Find a static field in a type by CLR name.
    /// </summary>
    private static FieldSymbol? FindStaticField(TypeSymbol type, string clrName)
    {
        return type.Members.Fields.FirstOrDefault(f => f.IsStatic && f.ClrName == clrName);
    }

    /// <summary>
    /// Check if a property conflicts with a base property.
    /// Conflict occurs when types are different (incompatible).
    /// </summary>
    private static bool HasPropertyConflict(PropertySymbol derived, PropertySymbol baseProperty)
    {
        // If types match exactly, no conflict
        if (TypeReferencesEqual(derived.PropertyType, baseProperty.PropertyType))
            return false;

        // Different types = conflict
        // (TypeScript requires exact match for properties in inheritance)
        return true;
    }

    /// <summary>
    /// Check if a method conflicts with base methods.
    /// Conflict occurs when signature is different (not an exact override).
    /// </summary>
    private static bool HasMethodConflict(MethodSymbol derived, List<MethodSymbol> baseMethods)
    {
        // If any base method has exact same signature, no conflict
        foreach (var baseMethod in baseMethods)
        {
            if (MethodSignaturesEqual(derived, baseMethod))
                return false;  // Exact match = valid override, no conflict
        }

        // No matching signature found = conflict
        return true;
    }

    /// <summary>
    /// Check if a field conflicts with a base field.
    /// Conflict occurs when types are different.
    /// </summary>
    private static bool HasFieldConflict(FieldSymbol derived, FieldSymbol baseField)
    {
        // If types match exactly, no conflict
        if (TypeReferencesEqual(derived.FieldType, baseField.FieldType))
            return false;

        // Different types = conflict
        return true;
    }

    /// <summary>
    /// Check if two type references are equal (same type).
    /// Simplified comparison based on string representation.
    /// </summary>
    private static bool TypeReferencesEqual(TypeReference a, TypeReference b)
    {
        // Simple comparison: use ToString() representation
        // This works for most cases; could be enhanced with proper structural equality
        return a.ToString() == b.ToString();
    }

    /// <summary>
    /// Check if two method signatures are equal (same parameters and return type).
    /// </summary>
    private static bool MethodSignaturesEqual(MethodSymbol a, MethodSymbol b)
    {
        // Check return type
        if (!TypeReferencesEqual(a.ReturnType, b.ReturnType))
            return false;

        // Check parameter count
        if (a.Parameters.Length != b.Parameters.Length)
            return false;

        // Check each parameter type
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (!TypeReferencesEqual(a.Parameters[i].Type, b.Parameters[i].Type))
                return false;
        }

        // Check generic parameters count
        if (a.GenericParameters.Length != b.GenericParameters.Length)
            return false;

        return true;
    }

    /// <summary>
    /// Find a TypeSymbol in the graph by TypeReference.
    /// </summary>
    private static TypeSymbol? FindTypeByReference(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => null
        };

        if (fullName == null)
            return null;

        // Skip System.Object and System.ValueType
        if (fullName == "System.Object" || fullName == "System.ValueType")
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName);
    }
}

/// <summary>
/// Plan for suppressing static members that conflict with base class.
/// </summary>
public sealed class StaticConflictPlan
{
    /// <summary>
    /// Map: Type StableId → Set of member StableIds to suppress.
    /// </summary>
    public Dictionary<string, HashSet<string>> SuppressedMembers { get; } = new();

    /// <summary>
    /// Map: Type StableId → Map of member StableId → suppression reason.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> SuppressionReasons { get; } = new();

    /// <summary>
    /// Add a member suppression for a type.
    /// </summary>
    public void AddSuppression(TypeSymbol type, string memberStableId, string reason)
    {
        var typeStableId = type.StableId.ToString();

        if (!SuppressedMembers.ContainsKey(typeStableId))
        {
            SuppressedMembers[typeStableId] = new HashSet<string>();
            SuppressionReasons[typeStableId] = new Dictionary<string, string>();
        }

        SuppressedMembers[typeStableId].Add(memberStableId);
        SuppressionReasons[typeStableId][memberStableId] = reason;
    }

    /// <summary>
    /// Check if a member should be suppressed.
    /// </summary>
    public bool ShouldSuppress(string typeStableId, string memberStableId)
    {
        return SuppressedMembers.TryGetValue(typeStableId, out var members)
            && members.Contains(memberStableId);
    }

    /// <summary>
    /// Get the suppression reason for a member (if suppressed).
    /// </summary>
    public string? GetSuppressionReason(string typeStableId, string memberStableId)
    {
        if (SuppressionReasons.TryGetValue(typeStableId, out var reasons))
        {
            if (reasons.TryGetValue(memberStableId, out var reason))
                return reason;
        }
        return null;
    }
}
