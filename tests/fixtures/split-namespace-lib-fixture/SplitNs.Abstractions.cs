namespace Example.SplitNs;

[System.AttributeUsage(System.AttributeTargets.All)]
public sealed class KeylessAttribute : System.Attribute
{
}

// A non-attribute type that is only defined in the abstractions package.
// Used to ensure consumers must import at least one type from this package,
// even if attribute usage isn't tracked in signature import planning.
public sealed class KeylessInfo
{
}
