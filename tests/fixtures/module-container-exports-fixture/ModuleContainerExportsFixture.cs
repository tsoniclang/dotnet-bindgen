namespace Tsonic.Internal
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class ModuleContainerAttribute : global::System.Attribute { }
}

namespace ModuleContainerExportsFixture
{
    public sealed class BuildRequest
    {
        public BuildRequest(int x) => X = x;
        public int X { get; }
    }

    public sealed class BuildResult
    {
        public BuildResult(int y) => Y = y;
        public int Y { get; }
    }

    [global::Tsonic.Internal.ModuleContainerAttribute]
    public static class BuildSite
    {
        public static BuildResult buildSite(BuildRequest req) => new(req.X + 1);

        public static string Version => "1";

        public static int Count = 42;

        internal static int Hidden => 0;
    }

    public static class OtherExports
    {
        public static int buildSite(int x) => x + 2;
    }
}
