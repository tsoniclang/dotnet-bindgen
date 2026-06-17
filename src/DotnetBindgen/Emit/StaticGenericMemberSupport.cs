using System.Collections.Immutable;
using System.Linq;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Emit;

internal static class StaticGenericMemberSupport
{
    public static bool RequiresCallableAccessor(
        TypeReference typeRef,
        ImmutableArray<GenericParameterSymbol> classGenerics)
    {
        if (classGenerics.Length == 0)
        {
            return false;
        }

        var classGenericNames = new HashSet<string>(
            classGenerics.Select(gp => gp.Name),
            StringComparer.Ordinal);

        return ReferencesClassGeneric(typeRef, classGenericNames);
    }

    private static bool ReferencesClassGeneric(
        TypeReference typeRef,
        HashSet<string> classGenericNames)
    {
        return typeRef switch
        {
            GenericParameterReference gp => classGenericNames.Contains(gp.Name),
            ArrayTypeReference arr => ReferencesClassGeneric(arr.ElementType, classGenericNames),
            PointerTypeReference ptr => ReferencesClassGeneric(ptr.PointeeType, classGenericNames),
            ByRefTypeReference byref => ReferencesClassGeneric(byref.ReferencedType, classGenericNames),
            FunctionPointerTypeReference fnptr =>
                ReferencesClassGeneric(fnptr.ReturnType, classGenericNames) ||
                fnptr.ParameterTypes.Any(parameter => ReferencesClassGeneric(parameter, classGenericNames)) ||
                fnptr.CallingConventionTypes.Any(cc => ReferencesClassGeneric(cc, classGenericNames)),
            NamedTypeReference named => named.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames)),
            NestedTypeReference nested => nested.FullReference.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames)),
            _ => false
        };
    }
}
