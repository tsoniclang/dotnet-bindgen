using System.Reflection;
using tsbindgen.Normalize;
using tsbindgen.Plan;
using Xunit;

namespace tsbindgen.Tests;

public sealed class SafeToExtendTests
{
    [Fact]
    public void DictionaryKeyCollection_DoesNotDirectlyExtend_NonGenericICollection()
    {
        var fixture = BuildFixture("System.Collections.dll");
        var result = GetSafeToExtendResult(fixture.Plan, "System.Collections.Generic.Dictionary`2+KeyCollection");

        Assert.DoesNotContain(
            result.AssignableInterfaces,
            iface => iface is tsbindgen.Model.Types.NamedTypeReference named &&
                     named.FullName == "System.Collections.ICollection");
    }

    [Fact]
    public void TaskOfT_Exposes_All_Base_WaitAsync_Overloads()
    {
        var fixture = BuildFixture("System.Private.CoreLib.dll");
        var taskOfT = fixture.Plan.Graph.TypeIndex["System.Threading.Tasks.Task`1"];
        var bindings = new tsbindgen.Emit.BindingsProvider(fixture.Context, fixture.Plan.Graph);

        var waitAsync = bindings.GetExposedMethods(taskOfT)!
            .Where(e => e.Method.ClrName == "WaitAsync")
            .Select(e => $"{string.Join(", ", e.Method.Parameters.Select(p => FormatType(p.Type)))} => {FormatType(e.Method.ReturnType)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("System.Threading.CancellationToken => System.Threading.Tasks.Task", waitAsync);
        Assert.Contains("System.TimeSpan => System.Threading.Tasks.Task", waitAsync);
        Assert.Contains("System.TimeSpan, System.TimeProvider => System.Threading.Tasks.Task", waitAsync);
        Assert.Contains("System.TimeSpan, System.Threading.CancellationToken => System.Threading.Tasks.Task", waitAsync);
        Assert.Contains("System.TimeSpan, System.TimeProvider, System.Threading.CancellationToken => System.Threading.Tasks.Task", waitAsync);
    }

    private static TestBuildFixture BuildFixture(string assemblyFileName)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir), "Failed to locate runtime directory.");

        var assemblyPath = Path.Combine(runtimeDir!, assemblyFileName);
        Assert.True(File.Exists(assemblyPath), $"Missing runtime assembly: {assemblyPath}");

        var ctx = BuildContext.Create();

        var loadPhase = typeof(Builder).GetMethod("LoadPhase", BindingFlags.NonPublic | BindingFlags.Static);
        var shapePhase = typeof(Builder).GetMethod("ShapePhase", BindingFlags.NonPublic | BindingFlags.Static);
        var planPhase = typeof(Builder).GetMethod("PlanPhase", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(loadPhase);
        Assert.NotNull(shapePhase);
        Assert.NotNull(planPhase);

        var loadResult = ((tsbindgen.Model.SymbolGraph Graph, MetadataLoadContext LoadContext))loadPhase!.Invoke(
            null,
            [ctx, new[] { assemblyPath }, new[] { runtimeDir! }])!;

        var graph = loadResult.Graph.WithIndices();
        graph = (tsbindgen.Model.SymbolGraph)shapePhase!.Invoke(null, [ctx, graph])!;
        graph = NameReservation.ReserveAllNames(ctx, graph);

        return new TestBuildFixture(
            ctx,
            (EmissionPlan)planPhase!.Invoke(null, [ctx, graph, loadResult.LoadContext])!);
    }

    private static SafeToExtendAnalyzer.SafeToExtendResult GetSafeToExtendResult(EmissionPlan plan, string clrFullName)
    {
        var type = plan.Graph.TypeIndex[clrFullName];
        return plan.SafeToExtend[type.StableId.ToString()];
    }

    private static string FormatType(tsbindgen.Model.Types.TypeReference typeRef) =>
        typeRef switch
        {
            tsbindgen.Model.Types.NamedTypeReference named when named.TypeArguments.Count == 0 => named.FullName,
            tsbindgen.Model.Types.NamedTypeReference named => $"{named.FullName}<{string.Join(", ", named.TypeArguments.Select(FormatType))}>",
            tsbindgen.Model.Types.GenericParameterReference generic => generic.Name,
            tsbindgen.Model.Types.ArrayTypeReference array => $"{FormatType(array.ElementType)}[]",
            tsbindgen.Model.Types.PointerTypeReference pointer => $"{FormatType(pointer.PointeeType)}*",
            tsbindgen.Model.Types.ByRefTypeReference byRef => $"{FormatType(byRef.ReferencedType)}&",
            tsbindgen.Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "<unknown>"
        };

    private sealed record TestBuildFixture(BuildContext Context, EmissionPlan Plan);
}
