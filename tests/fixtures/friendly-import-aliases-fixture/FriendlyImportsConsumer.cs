using FriendlyImportsLib;

namespace FriendlyImportsConsumer;

public sealed class UsesLib
{
    public Box<int> GetBox() => new Box<int>();

    public Database_1 GetDb0() => new Database_1();

    public Database_1<int> GetDb1() => new Database_1<int>();
}
