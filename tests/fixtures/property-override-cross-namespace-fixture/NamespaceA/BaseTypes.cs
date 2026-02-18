namespace NamespaceA;

public sealed class BaseEventType
{
}

public class BaseType
{
    public BaseEventType Event { get; set; } = new BaseEventType();
}
