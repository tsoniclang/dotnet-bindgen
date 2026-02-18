namespace NamespaceB;

public sealed class DerivedEventType
{
}

public sealed class DerivedType : NamespaceA.BaseType
{
    public new DerivedEventType Event { get; set; } = new DerivedEventType();
}
