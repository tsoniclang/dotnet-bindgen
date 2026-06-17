using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Plan.Validation;

namespace DotnetBindgen.Plan;

/// <summary>
/// Analyzes interface conformance to identify unsatisfiable interfaces.
/// This is a pre-validation analysis that runs before PhaseGate.
/// </summary>
public static class InterfaceConformanceAnalyzer
{
    public static Dictionary<string, List<ConformanceIssue>> AnalyzeConformance(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceConformanceAnalyzer", "Analyzing interface conformance (pre-validation)...");

        var conformanceIssuesByType = new Dictionary<string, List<ConformanceIssue>>();
        int typesWithIssues = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    continue;

                // Build set of interfaces that have planned explicit views
                var plannedInterfaces = type.ExplicitViews
                    .Select(v => GetTypeFullName(v.InterfaceReference))
                    .ToHashSet();

                var conformanceIssues = new List<ConformanceIssue>();

                // Check that all claimed interfaces have corresponding members
                foreach (var ifaceRef in type.Interfaces)
                {
                    var ifaceFullName = GetTypeFullName(ifaceRef);
                    var ifaceShortName = GetInterfaceName(ifaceRef);

                    // Interfaces with explicit views have members moved to companion interfaces.
                    // Therefore they are unsatisfiable for a TS `implements` clause.
                    if (plannedInterfaces.Contains(ifaceFullName))
                    {
                        conformanceIssues.Add(new ConformanceIssue(
                            InterfaceClrFullName: ifaceFullName,
                            InterfaceShortName: ifaceShortName,
                            Reason: UnsatisfiableReason.ExplicitImplementationMovedToView,
                            Details: "Members moved to companion view interface"));
                        continue; // no need to check members; it's already unsatisfiable
                    }

                    var iface = FindInterface(graph, ifaceRef);
                    if (iface == null)
                        continue; // External interface

                    // Verify structural conformance
                    var representableMethods = type.Members.Methods
                        .Where(m => m.EmitScope == EmitScope.ClassSurface)
                        .ToList();

                    foreach (var requiredMethod in iface.Members.Methods)
                    {
                        var methodSig = $"{requiredMethod.ClrName}({requiredMethod.Parameters.Length})";

                        var matchingMethod = representableMethods.FirstOrDefault(m =>
                            m.ClrName == requiredMethod.ClrName &&
                            m.Parameters.Length == requiredMethod.Parameters.Length);

                        if (matchingMethod == null)
                        {
                            conformanceIssues.Add(new ConformanceIssue(
                                InterfaceClrFullName: ifaceFullName,
                                InterfaceShortName: ifaceShortName,
                                Reason: UnsatisfiableReason.MissingOrIncompatibleMembers,
                                Details: $"Missing method {methodSig}"));
                        }
                        else
                        {
                            if (IsRepresentableConformanceBreak(matchingMethod, requiredMethod))
                            {
                                conformanceIssues.Add(new ConformanceIssue(
                                    InterfaceClrFullName: ifaceFullName,
                                    InterfaceShortName: ifaceShortName,
                                    Reason: UnsatisfiableReason.MissingOrIncompatibleMembers,
                                    Details: $"Method {methodSig} has incompatible TS signature"));
                            }
                        }
                    }

                    // Check properties
                    var representableProperties = type.Members.Properties
                        .Where(p => p.EmitScope == EmitScope.ClassSurface)
                        .ToList();

                    foreach (var requiredProperty in iface.Members.Properties)
                    {
                        var matchingProperty = representableProperties.FirstOrDefault(p =>
                            p.ClrName == requiredProperty.ClrName);

                        if (matchingProperty == null)
                        {
                            conformanceIssues.Add(new ConformanceIssue(
                                InterfaceClrFullName: ifaceFullName,
                                InterfaceShortName: ifaceShortName,
                                Reason: UnsatisfiableReason.MissingOrIncompatibleMembers,
                                Details: $"Missing property {requiredProperty.ClrName}"));
                        }
                        // Note: We don't check property type differences here - those are covariance issues (TBG310, not TBG203)
                    }
                }

                if (conformanceIssues.Count > 0)
                {
                    conformanceIssuesByType[type.ClrFullName] = conformanceIssues;
                    typesWithIssues++;
                }
            }
        }

        ctx.Log("InterfaceConformanceAnalyzer", $"Analyzed {graph.Namespaces.Sum(ns => ns.Types.Length)} types, found {typesWithIssues} with conformance issues");

        return conformanceIssuesByType;
    }

    // Local helper methods (replicate Core.cs helpers)
    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => GetTypeFullName(arr.ElementType),
            Model.Types.PointerTypeReference ptr => GetTypeFullName(ptr.PointeeType),
            Model.Types.ByRefTypeReference byref => GetTypeFullName(byref.ReferencedType),
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static string GetInterfaceName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.Name,
            Model.Types.NestedTypeReference nested => nested.NestedName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, Model.Types.TypeReference ifaceRef)
    {
        var fullName = GetTypeFullName(ifaceRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static bool IsRepresentableConformanceBreak(MethodSymbol classMethod, MethodSymbol ifaceMethod)
    {
        // Erase both methods to TypeScript signatures
        var classSig = TsErase.EraseMember(classMethod);
        var ifaceSig = TsErase.EraseMember(ifaceMethod);

        // Check if class method is assignable to interface method
        return !TsAssignability.IsMethodAssignable(classSig, ifaceSig);
    }
}
