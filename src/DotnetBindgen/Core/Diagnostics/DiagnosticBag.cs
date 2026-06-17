namespace DotnetBindgen.Core.Diagnostics;

/// <summary>
/// Severity level for diagnostics.
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A single diagnostic message.
/// </summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? Location { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Collects diagnostics throughout the pipeline.
/// Thread-safe collection.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly object _lock = new();

    /// <summary>
    /// Add an error diagnostic.
    /// </summary>
    public void Error(string code, string message, string? location = null)
    {
        Add(new Diagnostic
        {
            Code = code,
            Severity = DiagnosticSeverity.Error,
            Message = message,
            Location = location
        });
    }

    /// <summary>
    /// Add a warning diagnostic.
    /// </summary>
    public void Warning(string code, string message, string? location = null)
    {
        Add(new Diagnostic
        {
            Code = code,
            Severity = DiagnosticSeverity.Warning,
            Message = message,
            Location = location
        });
    }

    /// <summary>
    /// Add an info diagnostic.
    /// </summary>
    public void Info(string code, string message, string? location = null)
    {
        Add(new Diagnostic
        {
            Code = code,
            Severity = DiagnosticSeverity.Info,
            Message = message,
            Location = location
        });
    }

    /// <summary>
    /// Add a diagnostic with metadata.
    /// </summary>
    public void Add(Diagnostic diagnostic)
    {
        lock (_lock)
        {
            _diagnostics.Add(diagnostic);
        }
    }

    /// <summary>
    /// Get all diagnostics.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetAll()
    {
        lock (_lock)
        {
            return _diagnostics.ToList();
        }
    }

    /// <summary>
    /// Get diagnostics by severity.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetBySeverity(DiagnosticSeverity severity)
    {
        lock (_lock)
        {
            return _diagnostics.Where(d => d.Severity == severity).ToList();
        }
    }

    /// <summary>
    /// Get diagnostics by code.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetByCode(string code)
    {
        lock (_lock)
        {
            return _diagnostics.Where(d => d.Code == code).ToList();
        }
    }

    /// <summary>
    /// Check if there are any errors.
    /// </summary>
    public bool HasErrors()
    {
        lock (_lock)
        {
            return _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    /// <summary>
    /// Get the count of diagnostics.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _diagnostics.Count;
        }
    }

    /// <summary>
    /// Clear all diagnostics (for testing).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
            _diagnostics.Clear();
    }
}
