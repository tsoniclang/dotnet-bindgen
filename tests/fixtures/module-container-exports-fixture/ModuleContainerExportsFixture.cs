namespace Tsonic.Internal
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class ModuleContainerAttribute : global::System.Attribute { }
}

namespace ModuleContainerExportsFixture
{
    [global::Tsonic.Internal.ModuleContainerAttribute]
    public static class BuildSite
    {
        public static int buildSite(int x) => x + 1;

        public static string Version => "1";

        public static int Count = 42;

        internal static int Hidden => 0;
    }

    public static class OtherExports
    {
        public static int buildSite(int x) => x + 2;
    }
}
