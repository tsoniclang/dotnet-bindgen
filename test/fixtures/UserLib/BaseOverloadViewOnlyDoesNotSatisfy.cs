namespace MyCompany.Utils;

// Regression fixture: BaseOverloadAdder must ignore ViewOnly interface members when checking
// whether a derived class has "covered" a base-class overload.
//
// This mirrors the real-world pattern in System.IO.TextWriter/StringWriter:
// - The base class provides a public sealed Dispose() plus a protected Dispose(bool)
// - The derived class overrides Dispose(bool) only
// - StructuralConformance injects a ViewOnly interface member for IDisposable.Dispose() on the derived class
//
// If BaseOverloadAdder mistakenly counts the ViewOnly Dispose() as satisfying the base-class Dispose() overload,
// the derived type becomes TS-incompatible with its base (TS2430 / assignment failures).

public interface IMyDisposable
{
    void Dispose();
}

public class BaseBase
{
    public virtual void Dispose() { }
}

public abstract class BaseWriter : BaseBase, IMyDisposable
{
    public sealed override void Dispose() { }

    protected virtual void Dispose(bool disposing) { }
}

public class DerivedWriter : BaseWriter
{
    protected override void Dispose(bool disposing) { }
}
