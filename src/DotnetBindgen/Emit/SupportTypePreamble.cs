using System.Text;

namespace DotnetBindgen.Emit;

internal static class SupportTypePreamble
{
    private const string OpaqueTypeSupportMarker = "__TSBINDGEN_OPAQUE_TYPE_SUPPORT__";

    public static void EmitCoreTypeImports(StringBuilder sb)
    {
        sb.AppendLine("// Core type aliases from @tsonic/core");
        sb.AppendLine("import type { fnptr, ptr, sbyte, byte, short, ushort, int, uint, long, ulong, int128, uint128, half, float, double, decimal, nint, nuint, char } from '@tsonic/core/types.js';");
        sb.AppendLine();
    }

    public static void EmitOpaqueTypeSupportMarker(StringBuilder sb)
    {
        sb.AppendLine(OpaqueTypeSupportMarker);
        sb.AppendLine();
    }

    public static string FinalizeOpaqueTypeSupport(string text)
    {
        var markerLine = OpaqueTypeSupportMarker + Environment.NewLine;
        if (text.Contains("__OpaqueClrType<", StringComparison.Ordinal))
        {
            return text.Replace(markerLine, GetOpaqueTypeSupportText(), StringComparison.Ordinal);
        }

        return text.Replace(markerLine, string.Empty, StringComparison.Ordinal);
    }

    private static string GetOpaqueTypeSupportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Explicit opaque CLR placeholder for non-emittable signature shapes");
        sb.AppendLine("type __OpaqueClrType<Name extends string> = {");
        sb.AppendLine("  readonly __tsonic_opaqueClrType?: Name;");
        sb.AppendLine("};");
        sb.AppendLine();
        return sb.ToString();
    }
}
