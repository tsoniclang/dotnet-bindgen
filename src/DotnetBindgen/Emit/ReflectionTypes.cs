namespace DotnetBindgen.Emit;

/// <summary>
/// Centralized list of common reflection types that require special handling.
/// These types are heavily used as base classes and return types across System.Reflection.Emit
/// and require dual import strategy to avoid both TS2416 and TS2693 errors.
/// </summary>
public static class ReflectionTypes
{
    /// <summary>
    /// All common reflection types by CLR full name.
    /// These are the canonical source of truth for reflection type detection.
    /// </summary>
    private static readonly HashSet<string> CommonReflectionTypesByClrName = new()
    {
        "System.Reflection.Assembly",
        "System.Reflection.ConstructorInfo",
        "System.Reflection.EventInfo",
        "System.Reflection.FieldInfo",
        "System.Reflection.MemberInfo",
        "System.Reflection.MethodInfo",
        "System.Reflection.Module",
        "System.Reflection.ParameterInfo",
        "System.Reflection.PropertyInfo",
        "System.Reflection.TypeInfo",
        "System.Type"
    };

    /// <summary>
    /// All common reflection types by TypeScript name (after reserved word transformation).
    /// Used by InternalIndexEmitter which operates on TS names.
    /// </summary>
    private static readonly HashSet<string> CommonReflectionTypesByTsName = new()
    {
        "Assembly",
        "ConstructorInfo",
        "EventInfo",
        "FieldInfo",
        "MemberInfo",
        "MethodInfo",
        "Module_",        // System.Reflection.Module → Module_ (reserved word)
        "ParameterInfo",
        "PropertyInfo",
        "TypeInfo",
        "Type_"          // System.Type → Type_ (reserved word)
    };

    /// <summary>
    /// Check if a CLR full name is a common reflection type.
    /// Used by TypeNameResolver when deciding how to emit type references.
    /// </summary>
    public static bool IsCommonReflectionType(string clrFullName)
    {
        return CommonReflectionTypesByClrName.Contains(clrFullName);
    }

    /// <summary>
    /// Check if a TypeScript name is a common reflection type.
    /// Used by InternalIndexEmitter when deciding which types need dual imports.
    /// </summary>
    /// <param name="targetNamespace">The namespace of the type (e.g., "System.Reflection")</param>
    /// <param name="tsTypeName">The TypeScript name after reserved word transformation</param>
    public static bool IsCommonReflectionTypeName(string targetNamespace, string tsTypeName)
    {
        // Only applies to System.Reflection and System namespaces
        if (targetNamespace != "System.Reflection" && targetNamespace != "System")
            return false;

        return CommonReflectionTypesByTsName.Contains(tsTypeName);
    }

    /// <summary>
    /// Get all common reflection types (for documentation/testing).
    /// Returns CLR full names.
    /// </summary>
    public static IReadOnlySet<string> GetAllClrNames() => CommonReflectionTypesByClrName;

    /// <summary>
    /// Get all common reflection types (for documentation/testing).
    /// Returns TypeScript names after reserved word transformation.
    /// </summary>
    public static IReadOnlySet<string> GetAllTsNames() => CommonReflectionTypesByTsName;
}
