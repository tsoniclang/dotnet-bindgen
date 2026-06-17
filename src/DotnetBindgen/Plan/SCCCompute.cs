using System.Collections.Generic;
using System.Linq;

namespace DotnetBindgen.Plan;

/// <summary>
/// Computes Strongly Connected Components (SCCs) in namespace dependency graph.
/// Uses Tarjan's algorithm for linear-time SCC detection.
/// </summary>
public static class SCCCompute
{
    /// <summary>
    /// Compute SCCs from namespace dependency graph.
    /// Returns list of SCCs, where each SCC is a list of namespace names.
    /// SCCs are returned in reverse topological order (dependencies first).
    /// </summary>
    public static List<List<string>> ComputeSCCs(Dictionary<string, List<string>> dependencyGraph)
    {
        var sccs = new List<List<string>>();
        var index = 0;
        var stack = new Stack<string>();
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var onStack = new HashSet<string>();

        // Tarjan's algorithm
        foreach (var node in dependencyGraph.Keys)
        {
            if (!indices.ContainsKey(node))
            {
                StrongConnect(node, dependencyGraph, ref index, stack, indices, lowLinks, onStack, sccs);
            }
        }

        return sccs;
    }

    private static void StrongConnect(
        string node,
        Dictionary<string, List<string>> graph,
        ref int index,
        Stack<string> stack,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowLinks,
        HashSet<string> onStack,
        List<List<string>> sccs)
    {
        // Set the depth index for this node
        indices[node] = index;
        lowLinks[node] = index;
        index++;
        stack.Push(node);
        onStack.Add(node);

        // Consider successors of node
        if (graph.TryGetValue(node, out var successors))
        {
            foreach (var successor in successors)
            {
                if (!indices.ContainsKey(successor))
                {
                    // Successor has not yet been visited; recurse on it
                    StrongConnect(successor, graph, ref index, stack, indices, lowLinks, onStack, sccs);
                    lowLinks[node] = System.Math.Min(lowLinks[node], lowLinks[successor]);
                }
                else if (onStack.Contains(successor))
                {
                    // Successor is in stack and hence in the current SCC
                    lowLinks[node] = System.Math.Min(lowLinks[node], indices[successor]);
                }
            }
        }

        // If node is a root node, pop the stack and create an SCC
        if (lowLinks[node] == indices[node])
        {
            var scc = new List<string>();
            string w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (w != node);

            // Sort namespace names within SCC for determinism
            scc.Sort();
            sccs.Add(scc);
        }
    }

    /// <summary>
    /// Build dependency graph from ImportPlan.
    /// Returns adjacency list: namespace → list of namespaces it depends on.
    /// </summary>
    public static Dictionary<string, List<string>> BuildDependencyGraph(ImportPlan imports)
    {
        var graph = new Dictionary<string, List<string>>();

        foreach (var (sourceNamespace, importList) in imports.NamespaceImports)
        {
            if (!graph.ContainsKey(sourceNamespace))
            {
                graph[sourceNamespace] = new List<string>();
            }

            foreach (var import in importList)
            {
                if (!graph[sourceNamespace].Contains(import.TargetNamespace))
                {
                    graph[sourceNamespace].Add(import.TargetNamespace);
                }
            }
        }

        // Ensure all namespaces exist in graph (even those with no dependencies)
        foreach (var ns in imports.NamespaceExports.Keys)
        {
            if (!graph.ContainsKey(ns))
            {
                graph[ns] = new List<string>();
            }
        }

        return graph;
    }
}
