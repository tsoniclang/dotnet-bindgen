#nullable enable

namespace NrtPropertyFixture;

public sealed class BuildRequest
{
    public string? baseURL { get; set; }
    public string? themesDir { get; set; }
}

public sealed class SiteConfig
{
    public string baseURL { get; set; } = "";
}

