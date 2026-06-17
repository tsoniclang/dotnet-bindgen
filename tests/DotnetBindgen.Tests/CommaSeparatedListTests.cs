using System.Reflection;
using DotnetBindgen.Emit;
using Xunit;

namespace DotnetBindgen.Tests;

public sealed class CommaSeparatedListTests
{
    [Fact]
    public void MergeExtendsLists_DoesNotSplitGenericTypeArgs()
    {
        var merge = typeof(InternalIndexEmitter)
            .GetMethod("MergeExtendsLists", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(merge);

        var existing =
            "IAdditionOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>, " +
            "IBitwiseOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>";

        var additional =
            "IDivisionOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>, " +
            "IEqualityOperators_3<Vector_1<T>, Vector_1<T>, bool>";

        var merged = (string)merge!.Invoke(null, new object[] { existing, additional })!;

        Assert.Contains("IAdditionOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>", merged);
        Assert.Contains("IBitwiseOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>", merged);
        Assert.Contains("IDivisionOperators_3<Vector_1<T>, Vector_1<T>, Vector_1<T>>", merged);
        Assert.Contains("IEqualityOperators_3<Vector_1<T>, Vector_1<T>, bool>", merged);

        // Regression guard: the old implementation incorrectly split on commas inside <...>
        Assert.DoesNotContain(
            "IBitwiseOperators_3<Vector_1<T>, IDivisionOperators_3",
            merged);
    }

    [Fact]
    public void MergeExtendsLists_SplitsOnlyAtTopLevel()
    {
        var merge = typeof(InternalIndexEmitter)
            .GetMethod("MergeExtendsLists", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(merge);

        var existing = "Foo<Bar<Baz<int, string>>, Qux>, Quux";
        var additional = "Corge<Grault>, Garply";

        var merged = (string)merge!.Invoke(null, new object[] { existing, additional })!;

        Assert.Equal(
            "Foo<Bar<Baz<int, string>>, Qux>, Quux, Corge<Grault>, Garply",
            merged);
    }
}
