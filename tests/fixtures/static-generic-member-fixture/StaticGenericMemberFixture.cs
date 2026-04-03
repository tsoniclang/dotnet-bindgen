#nullable enable

namespace StaticGenericMemberFixture;

public sealed class Box<T>
{
    public static readonly T Seed = default!;
    public static T Default => default!;
    public static T Mutable = default!;
    public static T Current { get; set; } = default!;
}
