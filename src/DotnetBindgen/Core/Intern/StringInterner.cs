namespace DotnetBindgen.Core.Intern;

/// <summary>
/// Interns strings to reduce allocations and enable reference equality checks.
/// Thread-safe singleton pattern.
/// </summary>
public sealed class StringInterner
{
    private readonly Dictionary<string, string> _pool = new();
    private readonly object _lock = new();

    /// <summary>
    /// Intern a string. Returns the canonical instance.
    /// </summary>
    public string Intern(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        lock (_lock)
        {
            if (_pool.TryGetValue(value, out var canonical))
                return canonical;

            _pool[value] = value;
            return value;
        }
    }

    /// <summary>
    /// Get the count of interned strings (for diagnostics).
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _pool.Count;
        }
    }

    /// <summary>
    /// Clear the intern pool (for testing).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
            _pool.Clear();
    }
}
