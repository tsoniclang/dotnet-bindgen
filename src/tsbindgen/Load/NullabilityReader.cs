using System.Reflection;

namespace tsbindgen.Load;

/// <summary>
/// Reads Nullable Reference Type (NRT) metadata from .NET assemblies.
/// Works with MetadataLoadContext by reading attributes directly (not using NullabilityInfoContext).
///
/// NRT Encoding:
/// - NullableContextAttribute (type-level): Default nullability for all members
/// - NullableAttribute (member-level): Override for specific members
///
/// Byte values:
/// - 0 = oblivious (pre-C# 8, no NRT annotations)
/// - 1 = not-null
/// - 2 = nullable
///
/// For complex generic types, NullableAttribute contains a byte[] with one byte per type
/// in pre-order traversal: Dictionary&lt;string?, List&lt;int?&gt;&gt; → [1, 2, 1, 2]
/// </summary>
public static class NullabilityReader
{
    /// <summary>
    /// Nullability state for a type reference position.
    /// Named NrtState to avoid conflict with System.Reflection.NrtState.
    /// </summary>
    public enum NrtState : byte
    {
        /// <summary>Pre-C# 8 or #nullable disable - treat as non-nullable (strictest interpretation).</summary>
        Oblivious = 0,
        /// <summary>Not nullable (T).</summary>
        NotNull = 1,
        /// <summary>Nullable (T?).</summary>
        Nullable = 2
    }

    /// <summary>
    /// Get nullability for a property's return type.
    /// </summary>
    public static NrtState GetPropertyNullability(PropertyInfo property)
    {
        // Check for NullableAttribute on the property itself
        var nullableAttr = GetNullableAttribute(property.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, 0);
        }

        // Fall back to declaring type's NullableContextAttribute
        return GetContextDefault(property.DeclaringType);
    }

    /// <summary>
    /// Get nullability for a property's type at a specific position in the type tree.
    /// Used for nested generic types.
    /// </summary>
    /// <param name="property">The property to read nullability from.</param>
    /// <param name="position">Position in pre-order traversal of the type tree.</param>
    public static NrtState GetPropertyNullabilityAtPosition(PropertyInfo property, int position)
    {
        var nullableAttr = GetNullableAttribute(property.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, position);
        }

        return GetContextDefault(property.DeclaringType);
    }

    /// <summary>
    /// Get nullability for a method's return type.
    /// </summary>
    public static NrtState GetReturnTypeNullability(MethodInfo method)
    {
        // Check for NullableAttribute on the return parameter
        var returnParam = method.ReturnParameter;
        var nullableAttr = GetNullableAttribute(returnParam.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, 0);
        }

        // Fall back to declaring type's NullableContextAttribute
        return GetContextDefault(method.DeclaringType);
    }

    /// <summary>
    /// Get nullability for a method's return type at a specific position.
    /// </summary>
    public static NrtState GetReturnTypeNullabilityAtPosition(MethodInfo method, int position)
    {
        var returnParam = method.ReturnParameter;
        var nullableAttr = GetNullableAttribute(returnParam.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, position);
        }

        return GetContextDefault(method.DeclaringType);
    }

    /// <summary>
    /// Get nullability for a parameter.
    /// </summary>
    public static NrtState GetParameterNullability(ParameterInfo parameter)
    {
        var nullableAttr = GetNullableAttribute(parameter.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, 0);
        }

        // Fall back to declaring type's context
        var declaringType = parameter.Member?.DeclaringType;
        return GetContextDefault(declaringType);
    }

    /// <summary>
    /// Get nullability for a parameter at a specific position in the type tree.
    /// </summary>
    public static NrtState GetParameterNullabilityAtPosition(ParameterInfo parameter, int position)
    {
        var nullableAttr = GetNullableAttribute(parameter.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, position);
        }

        var declaringType = parameter.Member?.DeclaringType;
        return GetContextDefault(declaringType);
    }

    /// <summary>
    /// Get nullability for a field.
    /// </summary>
    public static NrtState GetFieldNullability(FieldInfo field)
    {
        var nullableAttr = GetNullableAttribute(field.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, 0);
        }

        return GetContextDefault(field.DeclaringType);
    }

    /// <summary>
    /// Get nullability for a field at a specific position.
    /// </summary>
    public static NrtState GetFieldNullabilityAtPosition(FieldInfo field, int position)
    {
        var nullableAttr = GetNullableAttribute(field.CustomAttributes);
        if (nullableAttr != null)
        {
            return GetNullabilityFromAttribute(nullableAttr, position);
        }

        return GetContextDefault(field.DeclaringType);
    }

    /// <summary>
    /// Calculate the number of type positions in a type tree (for pre-order traversal).
    /// </summary>
    /// <param name="type">The type to count positions for.</param>
    /// <returns>Number of positions in the type tree.</returns>
    public static int CountTypePositions(Type type)
    {
        // Value types (including Nullable<T>) are not tracked in NRT metadata
        // But Nullable<T> adds one position for the inner type
        if (type.IsValueType)
        {
            // Check for Nullable<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            {
                var underlyingType = type.GetGenericArguments()[0];
                return 1 + CountTypePositions(underlyingType);
            }
            return 0; // Non-nullable value types have no NRT positions
        }

        // Reference type takes 1 position
        var count = 1;

        // Add positions for generic type arguments
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                count += CountTypePositions(arg);
            }
        }

        // Add positions for array element type
        if (type.IsArray)
        {
            count += CountTypePositions(type.GetElementType()!);
        }

        return count;
    }

    /// <summary>
    /// Get NullableAttribute from custom attributes.
    /// </summary>
    private static CustomAttributeData? GetNullableAttribute(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute")
            {
                return attr;
            }
        }
        return null;
    }

    /// <summary>
    /// Get NullableContextAttribute from a type.
    /// </summary>
    private static CustomAttributeData? GetNullableContextAttribute(Type? type)
    {
        if (type == null) return null;

        foreach (var attr in type.CustomAttributes)
        {
            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute")
            {
                return attr;
            }
        }

        // Check enclosing types (for nested types)
        if (type.DeclaringType != null)
        {
            return GetNullableContextAttribute(type.DeclaringType);
        }

        return null;
    }

    /// <summary>
    /// Get nullability state from a NullableAttribute at a specific position.
    /// </summary>
    private static NrtState GetNullabilityFromAttribute(CustomAttributeData attr, int position)
    {
        if (attr.ConstructorArguments.Count == 0)
            return NrtState.Oblivious;

        var arg = attr.ConstructorArguments[0];

        // Single byte constructor: NullableAttribute(byte)
        if (arg.Value is byte singleByte)
        {
            // Single byte applies to all positions
            return (NrtState)singleByte;
        }

        // Byte array constructor: NullableAttribute(byte[])
        if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> byteArray)
        {
            var bytes = byteArray.Select(a => (byte)a.Value!).ToArray();
            if (position < bytes.Length)
            {
                return (NrtState)bytes[position];
            }
            // Position out of range - fall back to first byte or oblivious
            return bytes.Length > 0 ? (NrtState)bytes[0] : NrtState.Oblivious;
        }

        return NrtState.Oblivious;
    }

    /// <summary>
    /// Get the default nullability from a type's NullableContextAttribute.
    /// </summary>
    private static NrtState GetContextDefault(Type? type)
    {
        var contextAttr = GetNullableContextAttribute(type);
        if (contextAttr == null)
            return NrtState.Oblivious;

        if (contextAttr.ConstructorArguments.Count == 0)
            return NrtState.Oblivious;

        var arg = contextAttr.ConstructorArguments[0];
        if (arg.Value is byte contextByte)
        {
            return (NrtState)contextByte;
        }

        return NrtState.Oblivious;
    }
}
