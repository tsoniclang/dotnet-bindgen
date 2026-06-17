using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Model;

namespace DotnetBindgen.Plan;

/// <summary>
/// Plans honest TypeScript emission by identifying interfaces that cannot be satisfied.
/// </summary>
public static class HonestEmissionPlanner
{
    public static HonestEmissionPlan PlanHonestEmission(
        BuildContext ctx,
        Dictionary<string, List<ConformanceIssue>> conformanceIssuesByType)
    {
        ctx.Log("HonestEmissionPlanner", "Planning honest TypeScript emission for unsatisfiable interfaces...");

        var unsatisfiableByType = new Dictionary<string, List<UnsatisfiableInterface>>();
        int totalUnsatisfiable = 0;

        foreach (var (typeClrName, issues) in conformanceIssuesByType)
        {
            // Group issues by interface and aggregate
            var unsatisfiableInterfaces = AggregateByInterface(issues);

            if (unsatisfiableInterfaces.Count > 0)
            {
                unsatisfiableByType[typeClrName] = unsatisfiableInterfaces;
                totalUnsatisfiable += unsatisfiableInterfaces.Count;
            }
        }

        ctx.Log("HonestEmissionPlanner", $"Found {totalUnsatisfiable} unsatisfiable interfaces across {unsatisfiableByType.Count} types");

        // Convert to read-only dictionary
        var readonlyDict = unsatisfiableByType.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<UnsatisfiableInterface>)kvp.Value);

        return new HonestEmissionPlan
        {
            UnsatisfiableInterfaces = readonlyDict,
            TotalUnsatisfiableCount = totalUnsatisfiable
        };
    }

    private static List<UnsatisfiableInterface> AggregateByInterface(List<ConformanceIssue> issues)
    {
        // Group issues by interface CLR name and aggregate counts/reasons
        var byInterface = new Dictionary<string, (int count, UnsatisfiableReason reason)>();

        foreach (var issue in issues)
        {
            var key = issue.InterfaceClrFullName;

            if (!byInterface.TryGetValue(key, out var current))
            {
                byInterface[key] = (1, issue.Reason);
            }
            else
            {
                // Keep the more specific reason (ExplicitImplementationMovedToView takes precedence)
                var finalReason = issue.Reason == UnsatisfiableReason.ExplicitImplementationMovedToView
                    ? issue.Reason
                    : current.reason;
                byInterface[key] = (current.count + 1, finalReason);
            }
        }

        // Build list of UnsatisfiableInterface records
        return byInterface.Select(kvp => new UnsatisfiableInterface
        {
            InterfaceClrName = kvp.Key,
            Reason = kvp.Value.reason,
            IssueCount = kvp.Value.count
        }).ToList();
    }
}
