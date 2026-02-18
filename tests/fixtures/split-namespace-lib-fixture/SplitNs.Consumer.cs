using Example.SplitNs;

namespace SplitNs.Consumer;

[Keyless]
public sealed class MyContext : DbContext
{
    public KeylessInfo Info { get; } = new KeylessInfo();

    public DbSet<int> Users { get; } = new DbSet<int>();
}
