using MyCompany.Duplicate;

namespace MyCompany.DuplicateConsumer;

public sealed class Consumer
{
    public LibAThing A { get; }
    public LibBThing B { get; }

    public Consumer(LibAThing a, LibBThing b)
    {
        A = a;
        B = b;
    }
}

