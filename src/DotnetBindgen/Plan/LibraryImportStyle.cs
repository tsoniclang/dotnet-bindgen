namespace DotnetBindgen.Plan;

/// <summary>
/// Controls how dotnet-bindgen imports types from external libraries provided via <c>--lib</c>.
/// </summary>
public enum LibraryImportStyle
{
    /// <summary>
    /// Import from the library's public facade module (e.g. <c>@tsonic/dotnet/System.Linq.js</c>)
    /// and map type names to facade exports (dropping arity where appropriate).
    /// </summary>
    Facade,

    /// <summary>
    /// Import from the library's internal index module (e.g. <c>@tsonic/dotnet/System.Linq/internal/index.js</c>)
    /// and keep arity-stable internal names (IQueryable_1, IEnumerable_1, etc.).
    ///
    /// This is required for "airplane-grade" type relationships in internal/index.d.ts, because
    /// extending conditional multi-arity facade aliases (IQueryable&lt;T&gt;) does not reliably
    /// propagate members for assignability checks (e.g. DbSet&lt;T&gt; should satisfy IQueryable_1&lt;T&gt;).
    /// </summary>
    InternalIndex,
}

