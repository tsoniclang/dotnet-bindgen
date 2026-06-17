using System.Collections.Generic;
using System.Linq;

namespace DotnetBindgen.Plan;

/// <summary>
/// Plans SCC bucketing for namespaces to eliminate circular import dependencies.
/// </summary>
public static class SCCPlanner
{
    public static SCCPlan PlanSCCBuckets(BuildContext ctx, ImportPlan imports)
    {
        ctx.Log("SCCPlanner", "Computing Strongly Connected Components for namespace dependency graph...");

        // Build dependency graph from imports
        var dependencyGraph = SCCCompute.BuildDependencyGraph(imports);

        ctx.Log("SCCPlanner", $"Dependency graph: {dependencyGraph.Count} namespaces");

        // Compute SCCs using Tarjan's algorithm
        var sccs = SCCCompute.ComputeSCCs(dependencyGraph);

        ctx.Log("SCCPlanner", $"Found {sccs.Count} SCCs");

        // Build SCC buckets
        var buckets = new List<SCCBucket>();
        var namespaceToBucket = new Dictionary<string, int>();

        for (int i = 0; i < sccs.Count; i++)
        {
            var scc = sccs[i];
            var bucketId = scc.Count == 1
                ? scc[0]  // Singleton: use namespace name
                : $"scc_{i}";  // Multi-namespace: use indexed name

            buckets.Add(new SCCBucket
            {
                BucketId = bucketId,
                Namespaces = scc
            });

            // Map each namespace to its bucket index
            foreach (var ns in scc)
            {
                namespaceToBucket[ns] = i;
            }

            if (scc.Count > 1)
            {
                ctx.Log("SCCPlanner", $"  SCC {i} ({bucketId}): {scc.Count} namespaces - {string.Join(", ", scc.Take(5))}{(scc.Count > 5 ? "..." : "")}");
            }
        }

        var multiNamespaceSCCs = buckets.Count(b => b.IsMultiNamespace);
        var singletonSCCs = buckets.Count(b => !b.IsMultiNamespace);

        ctx.Log("SCCPlanner", $"SCC summary: {multiNamespaceSCCs} multi-namespace SCCs, {singletonSCCs} singletons");

        return new SCCPlan
        {
            Buckets = buckets,
            NamespaceToBucket = namespaceToBucket
        };
    }
}
