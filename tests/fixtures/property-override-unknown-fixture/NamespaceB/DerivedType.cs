namespace NamespaceB;

public sealed class DerivedEvents
{
}

public sealed class DerivedType : NamespaceA.BaseType
{
    public new DerivedEvents Events { get; set; } = new DerivedEvents();
}
