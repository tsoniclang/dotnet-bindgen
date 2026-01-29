namespace ProtectedVirtualFixture;

public class Base
{
    protected virtual int Foo(int x) => x;

    protected internal virtual string Bar(string s) => s;

    public virtual void PublicVirt() { }

    internal virtual void InternalVirt() { }

    private protected virtual void PrivateProtectedVirt() { }

    protected void ProtectedNonVirtual() { }

    protected virtual int Prop { get; set; }

    protected internal virtual string Prop2 { get; } = "x";

    public void Dispose() { }

    protected virtual void Dispose(bool disposing) { }
}

public class Derived : Base
{
    protected override int Foo(int x) => x + 1;

    protected internal override string Bar(string s) => s + "!";

    public override void PublicVirt() { }
}
