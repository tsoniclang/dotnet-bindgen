using System.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;
using tsbindgen.Renaming;
using tsbindgen.Emit.Shared;

namespace tsbindgen.Emit.Printers;

/// <summary>
/// Prints TypeScript class declarations from TypeSymbol.
/// Handles classes, structs, static classes, enums, and delegates.
/// </summary>
public static class ClassPrinter
{
    /// <summary>
    /// Print a complete class declaration.
    /// GUARD: Only prints public types - internal types are rejected.
    /// </summary>
    /// <param name="typesWithoutGenerics">Optional set to track types that had generics in CLR but were emitted without them (e.g., static classes)</param>
    /// <param name="bindingsProvider">Optional bindings provider for V2 inherited member exposure (if null, falls back to V1 behavior)</param>
    /// <param name="staticFlattening">D1: Plan for flattening static-only type hierarchies (if null, no flattening)</param>
    /// <param name="staticConflicts">D2: Plan for suppressing conflicting static members (if null, no suppression)</param>
    public static string Print(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, HashSet<string>? typesWithoutGenerics = null, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        // GUARD: Never print non-public types
        if (type.Accessibility != Accessibility.Public)
        {
            ctx.Log("ClassPrinter", $"REJECTED: Attempted to print non-public type {type.ClrFullName} (accessibility={type.Accessibility})");
            return string.Empty;
        }

        // TS2315 FIX: Route static classes (including abstract ones with static members) to PrintStaticClass
        // This handles both StaticNamespace and Class types that are marked as static
        if (type.IsStatic || type.Kind == TypeKind.StaticNamespace)
        {
            return PrintStaticClass(type, resolver, ctx, typesWithoutGenerics);
        }

        return type.Kind switch
        {
            TypeKind.Class => PrintClass(type, resolver, ctx, graph, bindingsProvider: bindingsProvider, staticFlattening: staticFlattening, staticConflicts: staticConflicts, propertyOverrides: propertyOverrides, honestEmission: honestEmission),
            TypeKind.Struct => PrintStruct(type, resolver, ctx, graph, bindingsProvider: bindingsProvider, staticFlattening: staticFlattening, staticConflicts: staticConflicts, propertyOverrides: propertyOverrides, honestEmission: honestEmission),
            TypeKind.Enum => PrintEnum(type, ctx),
            TypeKind.Delegate => PrintDelegate(type, resolver, ctx),
            TypeKind.Interface => PrintInterface(type, resolver, ctx),
            _ => $"// Unknown type kind: {type.Kind}"
        };
    }

    /// <summary>
    /// Print class/struct with $instance suffix (for companion views pattern).
    /// Used when type has explicit interface views that will be in separate companion interface.
    /// GUARD: Only prints public types - internal types are rejected.
    /// </summary>
    public static string PrintInstance(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        // GUARD: Never print non-public types
        if (type.Accessibility != Accessibility.Public)
        {
            ctx.Log("ClassPrinter", $"REJECTED: Attempted to print non-public type {type.ClrFullName} (accessibility={type.Accessibility})");
            return string.Empty;
        }

        return type.Kind switch
        {
            TypeKind.Class => PrintClass(type, resolver, ctx, graph, instanceSuffix: true, bindingsProvider: bindingsProvider, staticFlattening: staticFlattening, staticConflicts: staticConflicts, overrideConflicts: overrideConflicts, propertyOverrides: propertyOverrides, honestEmission: honestEmission),
            TypeKind.Struct => PrintStruct(type, resolver, ctx, graph, instanceSuffix: true, bindingsProvider: bindingsProvider, staticFlattening: staticFlattening, staticConflicts: staticConflicts, overrideConflicts: overrideConflicts, propertyOverrides: propertyOverrides, honestEmission: honestEmission),
            _ => Print(type, resolver, ctx, graph, bindingsProvider: bindingsProvider, staticFlattening: staticFlattening, staticConflicts: staticConflicts, overrideConflicts: overrideConflicts, propertyOverrides: propertyOverrides, honestEmission: honestEmission) // Fallback (guard already checked above)
        };
    }

    private static string PrintClass(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, bool instanceSuffix = false, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        var sb = new StringBuilder();

        // STEP 1: Always use instance type name for classes
        // instanceSuffix parameter kept for compatibility but ignored - we always use $instance now
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        // Class modifiers and declaration
        if (type.IsAbstract)
            sb.Append("abstract ");

        sb.Append("class ");
        sb.Append(finalName);

        // Generic parameters: class Foo<T, U>
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Base class: extends BaseClass
        // D1 FIX: Skip extends for static-only types that are being flattened
        var shouldFlatten = staticFlattening?.ShouldFlattenType(type.StableId.ToString()) ?? false;

        if (type.BaseType != null && !shouldFlatten)
        {
            // Pass forValuePosition=true to use qualified names for reflection types
            // This avoids TS2693 "type used as value" errors in extends clauses
            var baseTypeName = TypeRefPrinter.Print(type.BaseType, resolver, ctx, allowedTypeParameterNames: null, forValuePosition: true);
            // TS2693 FIX (Same-Namespace): For same-namespace types with views, use instance class name
            baseTypeName = ApplyInstanceSuffixForSameNamespaceViews(baseTypeName, type.BaseType, type.Namespace, graph, ctx);

            // Skip System.Object, System.ValueType, and any fallback types (any, unknown)
            // CRITICAL: Never emit "extends any" - TypeScript rejects it
            if (baseTypeName != "Object" &&
                baseTypeName != "ValueType" &&
                baseTypeName != "any" &&
                baseTypeName != "unknown")
            {
                sb.Append(" extends ");
                sb.Append(baseTypeName);
            }
        }
        else if (shouldFlatten)
        {
            ctx.Log("StaticFlattening", $"  Suppressing extends for static-only type: {type.ClrFullName}");
        }

        // Interfaces: implements IFoo, IBar
        // TS2304 FIX: Filter out non-public interfaces (not in graph)
        // PR C: Filter out unsatisfiable interfaces (honest emission)
        var publicInterfaces = type.Interfaces
            .Where(i => IsInterfaceInGraph(i, graph))
            .Where(i => !IsUnsatisfiableInterface(type, i, honestEmission))
            .ToArray();

        if (publicInterfaces.Length > 0)
        {
            sb.Append(" implements ");
            var interfaceNames = publicInterfaces.Select(i =>
            {
                // Pass forValuePosition=true to use qualified names for reflection types
                var name = TypeRefPrinter.Print(i, resolver, ctx, allowedTypeParameterNames: null, forValuePosition: true);
                // TS2693 FIX (Same-Namespace): For same-namespace types with views, use instance class name
                return ApplyInstanceSuffixForSameNamespaceViews(name, i, type.Namespace, graph, ctx);
            });
            sb.Append(string.Join(", ", interfaceNames));
        }

        sb.AppendLine(" {");

        // Emit members
        EmitMembers(sb, type, resolver, ctx, graph, bindingsProvider, staticConflicts, overrideConflicts, propertyOverrides);

        // D1 FIX: Emit inherited static members for flattened types
        if (shouldFlatten && staticFlattening != null)
        {
            var inheritedMembers = staticFlattening.GetInheritedMembers(type.StableId.ToString());
            if (inheritedMembers.Count > 0)
            {
                ctx.Log("StaticFlattening", $"  Emitting {inheritedMembers.Count} inherited static members for {type.ClrFullName}");

                // Filter out members that already exist in the derived class (by CLR name)
                var existingFieldNames = new HashSet<string>(type.Members.Fields.Where(f => f.IsStatic).Select(f => f.ClrName));
                var existingPropertyNames = new HashSet<string>(type.Members.Properties.Where(p => p.IsStatic).Select(p => p.ClrName));
                var existingMethodNames = new HashSet<string>(type.Members.Methods.Where(m => m.IsStatic).Select(m => m.ClrName));

                var fieldsToEmit = inheritedMembers.Fields.Where(f => !existingFieldNames.Contains(f.ClrName)).ToList();
                var propertiesToEmit = inheritedMembers.Properties.Where(p => !existingPropertyNames.Contains(p.ClrName)).ToList();
                var methodsToEmit = inheritedMembers.Methods.Where(m => !existingMethodNames.Contains(m.ClrName)).ToList();

                var totalToEmit = fieldsToEmit.Count + propertiesToEmit.Count + methodsToEmit.Count;

                if (totalToEmit > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("    // Inherited static members from base classes (hierarchy flattened)");

                    // Emit inherited fields
                    foreach (var field in fieldsToEmit)
                    {
                        EmitInheritedStaticField(sb, field, type, resolver, ctx);
                    }

                    // Emit inherited properties
                    foreach (var property in propertiesToEmit)
                    {
                        EmitInheritedStaticProperty(sb, property, type, resolver, ctx);
                    }

                    // Emit inherited methods
                    foreach (var method in methodsToEmit)
                    {
                        EmitInheritedStaticMethod(sb, method, type, resolver, ctx);
                    }

                    ctx.Log("StaticFlattening", $"  Actually emitted {totalToEmit} inherited members (filtered {inheritedMembers.Count - totalToEmit} duplicates)");
                }
                else
                {
                    ctx.Log("StaticFlattening", $"  All {inheritedMembers.Count} inherited members already exist in derived class - skipped");
                }
            }
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintStruct(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, bool instanceSuffix = false, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        // Structs emit as classes in TypeScript (with metadata noting value semantics)
        var sb = new StringBuilder();

        // STEP 1: Always use instance type name for structs
        // instanceSuffix parameter kept for compatibility but ignored - we always use $instance now
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        sb.Append("class ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Interfaces
        // TS2304 FIX: Filter out non-public interfaces (not in graph)
        // PR C: Filter out unsatisfiable interfaces (honest emission)
        var publicInterfaces = type.Interfaces
            .Where(i => IsInterfaceInGraph(i, graph))
            .Where(i => !IsUnsatisfiableInterface(type, i, honestEmission))
            .ToArray();

        if (publicInterfaces.Length > 0)
        {
            sb.Append(" implements ");
            sb.Append(string.Join(", ", publicInterfaces.Select(i => TypeRefPrinter.Print(i, resolver, ctx))));
        }

        sb.AppendLine(" {");

        // Emit members
        EmitMembers(sb, type, resolver, ctx, graph, bindingsProvider, staticConflicts, overrideConflicts, propertyOverrides);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintStaticClass(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, HashSet<string>? typesWithoutGenerics)
    {
        // Static classes emit as abstract classes with static members in TypeScript
        // NOTE: We do NOT emit class-level generic parameters here because TypeScript
        // prohibits static members from referencing class-level generics (TS2302).
        // Instead, we lift class generic parameters to method-level generics in EmitStaticMembers.
        var sb = new StringBuilder();

        // STEP 1: Use instance type name for static classes too (they're still classes)
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        // TS2315 FIX: Track types that had generics in CLR but are emitted without them
        // This prevents convenience export aliases from referencing them with type parameters
        // NOTE: Track using bare stem (without $instance) to match InternalIndexEmitter check
        if (typesWithoutGenerics != null && type.GenericParameters.Length > 0)
        {
            var bareStem = ctx.Renamer.GetFinalTypeName(type);  // Bare stem without $instance suffix
            typesWithoutGenerics.Add(bareStem);
            ctx.Log("TS2315Fix", $"Tracking type without generics: {bareStem} (CLR had {type.GenericParameters.Length} generic parameters)");
        }

        sb.Append("abstract class ");
        sb.Append(finalName);
        sb.AppendLine(" {");

        // Emit static members with generic lifting
        EmitStaticMembers(sb, type, resolver, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintEnum(TypeSymbol type, BuildContext ctx)
    {
        var sb = new StringBuilder();

        var finalName = ctx.Renamer.GetFinalTypeName(type);

        sb.Append("enum ");
        sb.Append(finalName);
        sb.AppendLine(" {");

        // Create type scope for enum member name resolution
        var typeScope = ScopeFactory.ClassStatic(type); // Enum members are like static fields

        // Emit enum fields
        var fields = type.Members.Fields.Where(f => f.IsConst).ToList();
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var memberFinalName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope);
            sb.Append("    ");
            sb.Append(memberFinalName);

            if (field.ConstValue != null)
            {
                sb.Append(" = ");
                sb.Append(field.ConstValue);
            }

            if (i < fields.Count - 1)
                sb.Append(',');

            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintDelegate(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
    {
        // Delegates emit as type aliases to function signatures
        var sb = new StringBuilder();

        var finalName = ctx.Renamer.GetFinalTypeName(type);

        sb.Append("type ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        sb.Append(" = ");

        // Find Invoke method
        var invokeMethod = type.Members.Methods.FirstOrDefault(m => m.ClrName == "Invoke");
        if (invokeMethod != null)
        {
            // Emit function signature: (a: int, b: string) => void
            sb.Append('(');
            sb.Append(string.Join(", ", invokeMethod.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, resolver, ctx)}")));
            sb.Append(") => ");
            sb.Append(TypeRefPrinter.Print(invokeMethod.ReturnType, resolver, ctx));
        }
        else
        {
            sb.Append("Function"); // Fallback
        }

        sb.AppendLine(";");

        return sb.ToString();
    }

    private static string PrintInterface(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
    {
        var sb = new StringBuilder();

        // STEP 1: Use instance type name for interfaces
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        sb.Append("interface ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Base interfaces: extends IFoo, IBar
        if (type.Interfaces.Length > 0)
        {
            sb.Append(" extends ");
            sb.Append(string.Join(", ", type.Interfaces.Select(i => TypeRefPrinter.Print(i, resolver, ctx))));
        }

        sb.AppendLine(" {");

        // Emit members (interfaces only have instance members)
        EmitInterfaceMembers(sb, type, resolver, ctx);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, BindingsProvider? bindingsProvider = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null)
    {
        var members = type.Members;

        // Create type scope for member name resolution
        var typeScope = ScopeFactory.ClassInstance(type); // Instance members

        // D3: Helper to check if an instance member should be suppressed due to override conflict
        var typeStableId = type.StableId.ToString();
        bool ShouldSuppressMember(string memberStableId)
        {
            if (overrideConflicts == null)
                return false;

            var shouldSuppress = overrideConflicts.ShouldSuppress(typeStableId, memberStableId);
            if (shouldSuppress)
            {
                var reason = overrideConflicts.GetSuppressionReason(typeStableId, memberStableId);
                ctx.Log("OverrideConflict", $"  Suppressing: {type.ClrFullName} member (StableId: {memberStableId}) - {reason}");
            }
            return shouldSuppress;
        }

        // Constructors
        foreach (var ctor in members.Constructors.Where(c => !c.IsStatic))
        {
            sb.Append("    constructor(");
            sb.Append(string.Join(", ", ctor.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, resolver, ctx)}")));
            sb.AppendLine(");");
        }

        // Fields - only emit ClassSurface members
        // CLR-NAME CONTRACT: Use PascalCase CLR names
        foreach (var field in members.Fields.Where(f => !f.IsStatic && f.EmitScope == EmitScope.ClassSurface))
        {
            // Apply CLR surface name policy
            var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(field.ClrName);

            sb.Append("    ");
            if (field.IsReadOnly)
                sb.Append("readonly ");
            sb.Append(emitName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(field.FieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Properties - V2: Use ExposedProperties from bindings if available (own + inherited)
        var exposedProperties = bindingsProvider?.GetExposedProperties(type);
        if (exposedProperties != null)
        {
            // V2 path: Use ExposedProperties (complete property sets including inherited)
            // Group by CLR name and use TsName from OWN properties for emission
            var propertyGroups = exposedProperties
                .GroupBy(e => e.Property.ClrName)  // Group by CLR name
                .OrderBy(g => g.Key);

            foreach (var group in propertyGroups)
            {
                var exposures = group.ToList();

                // Only emit properties where we have an OWN (non-inherited) exposure
                // Inherited properties are automatically available through TypeScript's extends clause
                // Re-declaring them causes TS2416 errors even if types are identical
                var ownProperty = exposures.FirstOrDefault(e => !e.IsInherited);
                if (ownProperty == null)
                {
                    // All exposures are inherited - skip emitting (already available from base)
                    continue;
                }

                var tsName = ownProperty.TsName;

                // D3: Skip if this instance property conflicts with base class
                if (ShouldSuppressMember(ownProperty.Property.StableId.ToString()))
                    continue;

                // Emit property (use own property for type)
                // FIX D EXTENSION: Substitute generic parameters for properties from interfaces
                var propToEmit = SubstituteMemberIfNeeded(type, ownProperty.Property, ctx, graph);

                sb.Append("    ");
                if (!propToEmit.HasSetter)
                    sb.Append("readonly ");
                sb.Append(tsName);  // V2: Use TsName from BindingsProvider
                sb.Append(": ");

                // E: Check for property override unification
                var key = (type.StableId.ToString(), ownProperty.Property.StableId.ToString());
                if (propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out var overrideType) == true)
                {
                    sb.Append(overrideType);  // Use unified union type
                }
                else
                {
                    sb.Append(TypeRefPrinter.Print(propToEmit.PropertyType, resolver, ctx));
                }

                sb.AppendLine(";");
            }
        }
        else
        {
            // Fallback: Old path for types without bindings
            // CLR-NAME CONTRACT: Use PascalCase CLR names (Count, not count)
            foreach (var prop in members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
            {
                // D3: Skip if this instance property conflicts with base class
                if (ShouldSuppressMember(prop.StableId.ToString()))
                    continue;

                // Apply CLR surface name policy
                var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(prop.ClrName);

                // FIX D EXTENSION: Substitute generic parameters for properties from interfaces
                var propToEmit = SubstituteMemberIfNeeded(type, prop, ctx, graph);

                sb.Append("    ");
                if (!propToEmit.HasSetter)
                    sb.Append("readonly ");
                sb.Append(emitName);
                sb.Append(": ");

                // E: Check for property override unification
                var key = (type.StableId.ToString(), prop.StableId.ToString());
                if (propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out var overrideType) == true)
                {
                    sb.Append(overrideType);  // Use unified union type
                }
                else
                {
                    sb.Append(TypeRefPrinter.Print(propToEmit.PropertyType, resolver, ctx));
                }

                sb.AppendLine(";");
            }
        }

        // Methods - only emit ClassSurface members
        // TS2416/TS2420 FIX: Emit methods as TypeScript overload sets (grouped by CLR name)
        // TS2512 FIX: Ensure all overloads in a group have consistent abstract/non-abstract status
        // V2 FIX: Use ExposedMethods from bindings if available (includes inherited methods)
        var shouldSkipAbstract = !type.IsAbstract;

        // V2: Use ExposedMethods from bindings if available (own + inherited)
        var exposedMethods = bindingsProvider?.GetExposedMethods(type);
        if (exposedMethods != null)
        {
            // V2 path: Use ExposedMethods (complete overload sets including inherited)
            // CRITICAL: Group by CLR name to unify overload sets across inheritance
            // But use TsName from OWN methods for emission (inherited methods may have different disambiguation)
            var methodGroups = exposedMethods
                .GroupBy(e => e.Method.ClrName)  // Group by CLR name to unify overloads
                .OrderBy(g => g.Key);

            foreach (var group in methodGroups)
            {
                var exposures = group.ToList();

                // Only emit OWN (non-inherited) methods
                // Inherited methods are automatically available through TypeScript's extends clause
                // Re-declaring them causes TS2416 errors even if types are identical
                // EXCEPTION: Always emit methods that implement abstract base members (TS2654 fix)
                var ownMethods = exposures.Where(e => !e.IsInherited).ToList();
                if (!ownMethods.Any())
                {
                    // All methods are inherited - skip emitting (already available from base)
                    continue;
                }

                // Choose TsName carefully for abstract method implementations
                // If one of the overloads implements an abstract base method, use that TsName
                // (it will have the correct name from the base, not a renamed collision-avoiding variant)
                string tsName;
                var preferredName = ownMethods.FirstOrDefault(m => !NameUtilities.HasNumericSuffix(m.TsName));
                if (preferredName != null)
                {
                    // Prefer method without numeric suffix (e.g., "equals" not "equals3")
                    tsName = preferredName.TsName;
                }
                else
                {
                    // All have numeric suffixes - use first
                    tsName = ownMethods.First().TsName;
                }

                // TS2512 FIX: Compute single abstract status for OWN methods only
                var groupIsAbstract = ownMethods.All(e => e.Method.IsAbstract) && type.IsAbstract;

                // Emit ONLY own method overload signatures (not inherited)
                foreach (var exposure in ownMethods)
                {
                    // D3: Skip if this instance method conflicts with base class
                    if (ShouldSuppressMember(exposure.Method.StableId.ToString()))
                        continue;

                    // Skip abstract methods in concrete classes - they're inherited declarations only
                    if (shouldSkipAbstract && exposure.Method.IsAbstract)
                        continue;

                    sb.Append("    ");

                    // FIX D EXTENSION: Substitute generic parameters if needed
                    var methodToEmit = SubstituteMemberIfNeeded(type, exposure.Method, ctx, graph);

                    // V2: Use unified TsName from derived type's own methods
                    sb.Append(MethodPrinter.PrintWithName(methodToEmit, type, tsName, resolver, ctx, emitAbstract: groupIsAbstract));
                    sb.AppendLine(";");
                }
            }
        }
        else
        {
            // V1 fallback path: Use only type's own methods
            var instanceMethods = members.Methods
                .Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface)
                .ToList();

            // Group by CLR base name for overload emission
            var methodGroups = GroupMethodsByClrName(instanceMethods, isStatic: false);

            foreach (var (clrName, overloads) in methodGroups.OrderBy(kvp => kvp.Key))
            {
                // Get the CLR-based emit name (preserves casing like "Equals", "GetHashCode")
                var emitName = GetClrEmitName(clrName);

                // TS2512 FIX: Compute single abstract status for entire overload group
                // If ALL overloads are abstract, mark the group as abstract
                // Otherwise, emit all as non-abstract (TypeScript requires consistency)
                var groupIsAbstract = overloads.All(m => m.IsAbstract) && type.IsAbstract;

                // Emit each overload signature
                foreach (var method in overloads)
                {
                    // D3: Skip if this instance method conflicts with base class
                    if (ShouldSuppressMember(method.StableId.ToString()))
                        continue;

                    // Skip abstract methods in concrete classes - they're inherited declarations only
                    if (shouldSkipAbstract && method.IsAbstract)
                        continue;

                    sb.Append("    ");

                    // FIX D EXTENSION: Substitute generic parameters for methods from interfaces
                    var methodToEmit = SubstituteMemberIfNeeded(type, method, ctx, graph);

                    // TS2416/TS2420 FIX: Use CLR-cased name instead of Renamer's lowercase name
                    // TS2512 FIX: Pass group-level abstract status to ensure consistency
                    sb.Append(MethodPrinter.PrintWithName(methodToEmit, type, emitName, resolver, ctx, emitAbstract: groupIsAbstract));
                    sb.AppendLine(";");
                }
            }
        }

        // Static members
        EmitStaticMembers(sb, type, resolver, ctx, staticConflicts);
    }

    private static void EmitStaticMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Shape.StaticConflictPlan? staticConflicts = null)
    {
        var members = type.Members;

        // Create type scope for static member name resolution
        var staticTypeScope = ScopeFactory.ClassStatic(type); // Static members

        // D2: Helper to check if a static member should be suppressed due to conflict with base
        var typeStableId = type.StableId.ToString();
        bool ShouldSuppressMember(string memberStableId)
        {
            if (staticConflicts == null)
                return false;

            var shouldSuppress = staticConflicts.ShouldSuppress(typeStableId, memberStableId);
            if (shouldSuppress)
            {
                var reason = staticConflicts.GetSuppressionReason(typeStableId, memberStableId);
                ctx.Log("StaticConflict", $"  Suppressing: {type.ClrFullName} static member (StableId: {memberStableId}) - {reason}");
            }
            return shouldSuppress;
        }

        // Skip abstract static methods in concrete classes (same rule as instance methods)
        var shouldSkipAbstract = !type.IsAbstract;

        // Static fields - only emit ClassSurface or StaticSurface members
        // NOTE: If field type references class generics, widen to 'unknown' (TypeScript limitation)
        foreach (var field in members.Fields.Where(f => f.IsStatic && !f.IsConst &&
            (f.EmitScope == EmitScope.ClassSurface || f.EmitScope == EmitScope.StaticSurface)))
        {
            // D2: Skip if this static field conflicts with base class
            if (ShouldSuppressMember(field.StableId.ToString()))
                continue;

            var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope);
            sb.Append("    static ");
            if (field.IsReadOnly)
                sb.Append("readonly ");
            sb.Append(finalName);
            sb.Append(": ");

            // Check if field type references class-level generics
            var fieldType = SubstituteClassGenericsInTypeRef(field.FieldType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(fieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Const fields (as static readonly) - only emit ClassSurface or StaticSurface members
        // CLR-NAME CONTRACT: Use PascalCase CLR names
        foreach (var field in members.Fields.Where(f => f.IsConst &&
            (f.EmitScope == EmitScope.ClassSurface || f.EmitScope == EmitScope.StaticSurface)))
        {
            // D2: Skip if this const field conflicts with base class
            if (ShouldSuppressMember(field.StableId.ToString()))
                continue;

            // Apply CLR surface name policy
            var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(field.ClrName);

            sb.Append("    static readonly ");
            sb.Append(emitName);
            sb.Append(": ");

            var fieldType = SubstituteClassGenericsInTypeRef(field.FieldType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(fieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Static properties - only emit ClassSurface or StaticSurface members
        // NOTE: If property type references class generics, widen to 'unknown' (TypeScript limitation)
        // CLR-NAME CONTRACT: Use PascalCase CLR names
        foreach (var prop in members.Properties.Where(p => p.IsStatic &&
            (p.EmitScope == EmitScope.ClassSurface || p.EmitScope == EmitScope.StaticSurface)))
        {
            // D2: Skip if this static property conflicts with base class
            if (ShouldSuppressMember(prop.StableId.ToString()))
                continue;

            // Apply CLR surface name policy
            var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(prop.ClrName);

            sb.Append("    static ");
            if (!prop.HasSetter)
                sb.Append("readonly ");
            sb.Append(emitName);
            sb.Append(": ");

            var propType = SubstituteClassGenericsInTypeRef(prop.PropertyType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(propType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Static methods - only emit ClassSurface or StaticSurface members
        // FIX: Lift class-level generic parameters to method-level to avoid TS2302
        // TS2416/TS2420 FIX: Group by CLR base name, emit TypeScript overload sets
        // TS2512 FIX: Ensure all overloads in a group have consistent abstract/non-abstract status
        var staticMethods = members.Methods
            .Where(m => m.IsStatic && (m.EmitScope == EmitScope.ClassSurface || m.EmitScope == EmitScope.StaticSurface))
            .ToList();

        var staticMethodGroups = GroupMethodsByClrName(staticMethods, isStatic: true);

        foreach (var (clrName, overloads) in staticMethodGroups.OrderBy(kvp => kvp.Key))
        {
            var emitName = GetClrEmitName(clrName);

            // TS2512 FIX: Compute single abstract status for entire overload group
            var groupIsAbstract = overloads.All(m => m.IsAbstract) && type.IsAbstract;

            foreach (var method in overloads)
            {
                // D2: Skip if this static method conflicts with base class
                if (ShouldSuppressMember(method.StableId.ToString()))
                    continue;

                // Skip abstract static methods in concrete classes
                if (shouldSkipAbstract && method.IsAbstract)
                    continue;

                // Lift class generic parameters into this method
                var liftedMethod = LiftClassGenericsToMethod(method, type, ctx);

                sb.Append("    ");
                sb.Append(MethodPrinter.PrintWithName(liftedMethod, type, emitName, resolver, ctx, emitAbstract: groupIsAbstract));
                sb.AppendLine(";");
            }
        }
    }

    private static void EmitInterfaceMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
    {
        var members = type.Members;

        // Properties - only emit ClassSurface members, skip static (TypeScript doesn't support static interface members)
        // CLR-NAME CONTRACT: Use PascalCase CLR names (Dispose, not dispose)
        foreach (var prop in members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
        {
            // Apply CLR surface name policy (preserves PascalCase, sanitizes reserved words)
            var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(prop.ClrName);

            sb.Append("    ");
            if (!prop.HasSetter)
                sb.Append("readonly ");
            sb.Append(emitName);
            sb.Append(": ");
            sb.Append(TypeRefPrinter.Print(prop.PropertyType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Methods - only emit ClassSurface members, skip static (TypeScript doesn't support static interface members)
        // CLR-NAME CONTRACT: Group by CLR name, emit as TypeScript overload sets
        var interfaceMethods = members.Methods
            .Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface)
            .ToList();

        var methodGroups = GroupMethodsByClrName(interfaceMethods, isStatic: false);

        foreach (var (clrName, overloads) in methodGroups.OrderBy(kvp => kvp.Key))
        {
            // Get CLR-based emit name (PascalCase, sanitized)
            var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(clrName);

            // Emit each overload signature (interfaces have no abstract keyword)
            foreach (var method in overloads)
            {
                sb.Append("    ");
                sb.Append(MethodPrinter.PrintWithName(method, type, emitName, resolver, ctx));
                sb.AppendLine(";");
            }
        }
    }

    private static string PrintGenericParameter(GenericParameterSymbol gp, TypeNameResolver resolver, BuildContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(gp.Name);

        // Constraints from IReadOnlyList<TypeReference>
        if (gp.Constraints.Length > 0)
        {
            sb.Append(" extends ");

            // TS2344 FIX: Filter out "any" from constraints
            // C# value type constraints (struct, unmanaged) can't be represented in TS and emit as "any"
            // "any & IFoo" is invalid - just use "IFoo"
            var printedConstraints = gp.Constraints
                .Select(c => TypeRefPrinter.Print(c, resolver, ctx))
                .Where(c => c != "any" && c != "unknown")  // Filter out fallback types
                .ToArray();

            if (printedConstraints.Length == 0)
            {
                // All constraints were "any" - just use "any"
                sb.Append("any");
            }
            else if (printedConstraints.Length == 1)
            {
                sb.Append(printedConstraints[0]);
            }
            else
            {
                // Multiple constraints: T extends IFoo & IBar
                sb.Append(string.Join(" & ", printedConstraints));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// FIX D EXTENSION: Substitute generic parameters for methods from interfaces.
    /// If method has SourceInterface, match it to class's actual interface implementation
    /// and substitute generic parameters (fixes "T" leaks in class surface).
    /// </summary>
    private static MethodSymbol SubstituteMemberIfNeeded(TypeSymbol type, MethodSymbol method, BuildContext ctx, Model.SymbolGraph graph)
    {
        // FIX D EXTENSION: Handle both interface and base class substitution

        // Case 1: Method from interface
        if (method.SourceInterface != null)
        {
            return SubstituteInterfaceMethod(type, method, ctx, graph);
        }

        // Case 2: Method might be from base class - check for orphaned generic parameters
        if (HasOrphanedGenericParameters(type, method))
        {
            return SubstituteBaseClassMethod(type, method, ctx, graph);
        }

        return method;
    }

    /// <summary>
    /// Check if a method has generic parameter references that don't exist in the type's generic parameters.
    /// This indicates the method is inherited from a generic base class.
    /// </summary>
    private static bool HasOrphanedGenericParameters(TypeSymbol type, MethodSymbol method)
    {
        var typeGenericParams = new HashSet<string>(type.GenericParameters.Select(gp => gp.Name));
        var methodGenericParams = new HashSet<string>(method.GenericParameters.Select(gp => gp.Name));

        // Check return type and parameters for generic references not in type or method
        var allTypeRefs = new List<TypeReference> { method.ReturnType };
        allTypeRefs.AddRange(method.Parameters.Select(p => p.Type));

        foreach (var typeRef in allTypeRefs)
        {
            if (ContainsOrphanedGenericParameter(typeRef, typeGenericParams, methodGenericParams))
                return true;
        }

        return false;
    }

    private static bool ContainsOrphanedGenericParameter(
        TypeReference typeRef,
        HashSet<string> typeParams,
        HashSet<string> methodParams)
    {
        return typeRef switch
        {
            GenericParameterReference gp => !typeParams.Contains(gp.Name) && !methodParams.Contains(gp.Name),
            ArrayTypeReference arr => ContainsOrphanedGenericParameter(arr.ElementType, typeParams, methodParams),
            PointerTypeReference ptr => ContainsOrphanedGenericParameter(ptr.PointeeType, typeParams, methodParams),
            ByRefTypeReference byref => ContainsOrphanedGenericParameter(byref.ReferencedType, typeParams, methodParams),
            NamedTypeReference named => named.TypeArguments.Any(arg => ContainsOrphanedGenericParameter(arg, typeParams, methodParams)),
            _ => false
        };
    }

    /// <summary>
    /// Substitute generic parameters for methods from base classes.
    /// </summary>
    private static MethodSymbol SubstituteBaseClassMethod(TypeSymbol type, MethodSymbol method, BuildContext ctx, Model.SymbolGraph graph)
    {
        // Get base class reference
        if (type.BaseType == null)
            return method;

        ctx.Log("FixDExtension", $"Base class substitution for {type.ClrName}.{method.ClrName}");

        // Find base class symbol
        var baseClassSymbol = FindTypeSymbol(graph, type.BaseType);
        if (baseClassSymbol == null)
        {
            ctx.Log("FixDExtension", $"  Base class symbol not found");
            return method;
        }

        // Build substitution map from base class generic params to derived class's type arguments
        var substitutionMap = BuildSubstitutionMapForClass(type.BaseType, baseClassSymbol);
        if (substitutionMap.Count == 0)
        {
            ctx.Log("FixDExtension", $"  No substitutions in map");
            return method;
        }

        ctx.Log("FixDExtension", $"  Substitution map: {string.Join(", ", substitutionMap.Select(kv => $"{kv.Key}→{GetTypeFullName(kv.Value)}"))}");

        // Guard: exclude method-level generic parameters from substitution
        var methodLevelParams = new HashSet<string>(method.GenericParameters.Select(gp => gp.Name));
        var filteredMap = substitutionMap
            .Where(kv => !methodLevelParams.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filteredMap.Count == 0)
            return method;

        // Substitute return type and parameters
        var newReturnType = Load.InterfaceMemberSubstitution.SubstituteTypeReference(
            method.ReturnType, filteredMap);

        var newParameters = method.Parameters
            .Select(p => p with
            {
                Type = Load.InterfaceMemberSubstitution.SubstituteTypeReference(p.Type, filteredMap)
            })
            .ToImmutableArray();

        ctx.Log("FixDExtension", $"  Substituted method signature");

        return method with
        {
            ReturnType = newReturnType,
            Parameters = newParameters
        };
    }

    /// <summary>
    /// Substitute generic parameters for methods from interfaces (original Fix D logic).
    /// </summary>
    private static MethodSymbol SubstituteInterfaceMethod(TypeSymbol type, MethodSymbol method, BuildContext ctx, Model.SymbolGraph graph)
    {

        ctx.Log("FixDExtension", $"Processing {type.ClrName}.{method.ClrName} from {GetTypeFullName(method.SourceInterface)}");

        // Match SourceInterface to class's actual interface implementation
        var matchedInterface = FindMatchingInterfaceForMember(type, method.SourceInterface);
        if (matchedInterface == null)
        {
            ctx.Log("FixDExtension", $"  No matched interface found");
            return method; // No match found, return original
        }

        ctx.Log("FixDExtension", $"  Matched interface: {GetTypeFullName(matchedInterface)}");

        // Find the interface symbol to get its generic parameter names
        var ifaceSymbol = FindInterfaceSymbol(graph, method.SourceInterface);
        if (ifaceSymbol == null)
        {
            ctx.Log("FixDExtension", $"  Interface symbol not found in graph");
            return method; // Can't find interface definition
        }

        ctx.Log("FixDExtension", $"  Found interface symbol with {ifaceSymbol.GenericParameters.Length} generic params");

        // Build substitution map using actual interface generic parameter names
        var substitutionMap = BuildSubstitutionMapForClass(matchedInterface, ifaceSymbol);
        if (substitutionMap.Count == 0)
            return method; // No substitutions needed

        // Guard: exclude method-level generic parameters from substitution
        var methodLevelParams = new HashSet<string>(method.GenericParameters.Select(gp => gp.Name));
        var filteredMap = substitutionMap
            .Where(kv => !methodLevelParams.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filteredMap.Count == 0)
            return method; // No substitutions after filtering

        // Substitute return type and parameters
        var newReturnType = Load.InterfaceMemberSubstitution.SubstituteTypeReference(
            method.ReturnType, filteredMap);

        var newParameters = method.Parameters
            .Select(p => p with
            {
                Type = Load.InterfaceMemberSubstitution.SubstituteTypeReference(p.Type, filteredMap)
            })
            .ToImmutableArray();

        return method with
        {
            ReturnType = newReturnType,
            Parameters = newParameters
        };
    }

    /// <summary>
    /// FIX D EXTENSION: Substitute generic parameters for properties from interfaces or base classes.
    /// </summary>
    private static PropertySymbol SubstituteMemberIfNeeded(TypeSymbol type, PropertySymbol prop, BuildContext ctx, Model.SymbolGraph graph)
    {
        // Case 1: Property from interface
        if (prop.SourceInterface != null)
        {
            return SubstituteInterfaceProperty(type, prop, ctx, graph);
        }

        // Case 2: Property might be from base class - check for orphaned generic parameters
        if (HasOrphanedGenericParametersInProperty(type, prop))
        {
            return SubstituteBaseClassProperty(type, prop, ctx, graph);
        }

        return prop;
    }

    private static bool HasOrphanedGenericParametersInProperty(TypeSymbol type, PropertySymbol prop)
    {
        var typeGenericParams = new HashSet<string>(type.GenericParameters.Select(gp => gp.Name));

        return ContainsOrphanedGenericParameter(prop.PropertyType, typeGenericParams, new HashSet<string>());
    }

    private static PropertySymbol SubstituteBaseClassProperty(TypeSymbol type, PropertySymbol prop, BuildContext ctx, Model.SymbolGraph graph)
    {
        if (type.BaseType == null)
            return prop;

        var baseClassSymbol = FindTypeSymbol(graph, type.BaseType);
        if (baseClassSymbol == null)
            return prop;

        var substitutionMap = BuildSubstitutionMapForClass(type.BaseType, baseClassSymbol);
        if (substitutionMap.Count == 0)
            return prop;

        var newPropertyType = Load.InterfaceMemberSubstitution.SubstituteTypeReference(
            prop.PropertyType, substitutionMap);

        return prop with
        {
            PropertyType = newPropertyType
        };
    }

    private static PropertySymbol SubstituteInterfaceProperty(TypeSymbol type, PropertySymbol prop, BuildContext ctx, Model.SymbolGraph graph)
    {
        // Match SourceInterface to class's actual interface implementation
        var matchedInterface = FindMatchingInterfaceForMember(type, prop.SourceInterface!);
        if (matchedInterface == null)
            return prop;

        // Find the interface symbol to get its generic parameter names
        var ifaceSymbol = FindInterfaceSymbol(graph, prop.SourceInterface!);
        if (ifaceSymbol == null)
            return prop;

        // Build substitution map using actual interface generic parameter names
        var substitutionMap = BuildSubstitutionMapForClass(matchedInterface, ifaceSymbol);
        if (substitutionMap.Count == 0)
            return prop;

        // Substitute property type
        var newPropertyType = Load.InterfaceMemberSubstitution.SubstituteTypeReference(
            prop.PropertyType, substitutionMap);

        return prop with
        {
            PropertyType = newPropertyType
        };
    }

    /// <summary>
    /// Match member's SourceInterface to class's actual interface implementations.
    /// Returns the matched interface with correct type arguments.
    /// </summary>
    private static TypeReference? FindMatchingInterfaceForMember(TypeSymbol type, TypeReference sourceInterface)
    {
        var sourceBaseName = GetInterfaceBaseName(sourceInterface);

        foreach (var implementedInterface in type.Interfaces)
        {
            var implBaseName = GetInterfaceBaseName(implementedInterface);

            // Match by base name (e.g., "ICollection`1")
            if (sourceBaseName == implBaseName)
            {
                return implementedInterface;
            }
        }

        return null;
    }

    /// <summary>
    /// Get the base name of an interface (without type arguments) for matching.
    /// </summary>
    private static string GetInterfaceBaseName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.Name,  // e.g., "ICollection`1"
            NestedTypeReference nested => nested.NestedName,
            _ => typeRef.ToString() ?? ""
        };
    }

    /// <summary>
    /// Build substitution map from a closed interface reference using actual interface generic parameter names.
    /// For ICollection`1<TItem> with interface definition ICollection`1<T>, maps T -> TItem.
    /// </summary>
    private static Dictionary<string, TypeReference> BuildSubstitutionMapForClass(
        TypeReference closedInterfaceRef,
        TypeSymbol interfaceSymbol)
    {
        var map = new Dictionary<string, TypeReference>();

        if (closedInterfaceRef is NamedTypeReference { TypeArguments.Count: > 0 } namedRef)
        {
            // Map interface generic parameters to actual type arguments
            // Interface: ICollection<T> has GenericParameters = [T]
            // Class implements: ICollection<TItem>
            // Map: T -> TItem

            if (interfaceSymbol.GenericParameters.Length != namedRef.TypeArguments.Count)
                return map; // Mismatch - skip

            for (int i = 0; i < interfaceSymbol.GenericParameters.Length; i++)
            {
                var param = interfaceSymbol.GenericParameters[i];
                var arg = namedRef.TypeArguments[i];
                map[param.Name] = arg; // Map "T" -> TItem
            }
        }

        return map;
    }

    /// <summary>
    /// Find the interface symbol definition in the symbol graph.
    /// </summary>
    private static TypeSymbol? FindInterfaceSymbol(Model.SymbolGraph graph, TypeReference interfaceRef)
    {
        var ifaceName = GetTypeFullName(interfaceRef);

        // Search through all namespaces in the graph for the interface
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.Kind == TypeKind.Interface && type.ClrFullName == ifaceName)
                {
                    return type;
                }
            }
        }

        return null; // Interface not found in graph
    }

    /// <summary>
    /// Find any type symbol (class, struct, interface, etc.) in the symbol graph.
    /// </summary>
    private static TypeSymbol? FindTypeSymbol(Model.SymbolGraph graph, TypeReference typeRef)
    {
        var typeName = GetTypeFullName(typeRef);

        // Search through all namespaces in the graph for the type
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.ClrFullName == typeName)
                {
                    return type;
                }
            }
        }

        return null; // Type not found in graph
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Lifts class-level generic parameters to method-level generic parameters.
    /// This is necessary for static methods because TypeScript prohibits static members
    /// from referencing class-level generic parameters (TS2302).
    ///
    /// Example transformation:
    /// Class: ArrayMarshaller_2 (has class generics T, TUnmanagedElement)
    /// Method: static allocate(managed: T[]) → allocate&lt;T, TUnmanagedElement&gt;(managed: T[])
    /// </summary>
    private static MethodSymbol LiftClassGenericsToMethod(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
    {
        // If the declaring type has no generic parameters, nothing to lift
        if (declaringType.GenericParameters.Length == 0)
            return method;

        // Build a list of class generic parameters to lift
        var classGenerics = declaringType.GenericParameters.ToList();

        // Check for collisions with existing method generic parameters
        var existingMethodGenericNames = new HashSet<string>(
            method.GenericParameters.Select(gp => gp.Name)
        );

        // Rename class generics if they collide with method generics
        var liftedGenerics = new List<GenericParameterSymbol>();
        var substitutionMap = new Dictionary<string, TypeReference>();

        foreach (var classGeneric in classGenerics)
        {
            var name = classGeneric.Name;
            var renamedName = name;
            var counter = 1;

            // Find a non-colliding name
            while (existingMethodGenericNames.Contains(renamedName))
            {
                renamedName = $"{name}{counter}";
                counter++;
            }

            // Add to lifted generics with possibly renamed parameter
            var liftedGeneric = classGeneric with { Name = renamedName };
            liftedGenerics.Add(liftedGeneric);
            existingMethodGenericNames.Add(renamedName);

            // If we renamed, we need to substitute references
            if (renamedName != name)
            {
                substitutionMap[name] = new GenericParameterReference
                {
                    Id = new GenericParameterId
                    {
                        DeclaringTypeName = $"{declaringType.ClrFullName}_Lifted",
                        Position = classGeneric.Position,
                        IsMethodParameter = false
                    },
                    Name = renamedName,
                    Position = classGeneric.Position,
                    Constraints = classGeneric.Constraints
                };
            }
        }

        // Combine lifted generics with existing method generics
        var combinedGenerics = liftedGenerics
            .Concat(method.GenericParameters)
            .ToImmutableArray();

        // Substitute class generic references in return type and parameters
        // If we renamed any generics, apply the substitution map
        var newReturnType = method.ReturnType;
        var newParameters = method.Parameters;

        if (substitutionMap.Count > 0)
        {
            newReturnType = Load.InterfaceMemberSubstitution.SubstituteTypeReference(
                method.ReturnType, substitutionMap);

            newParameters = method.Parameters
                .Select(p => p with
                {
                    Type = Load.InterfaceMemberSubstitution.SubstituteTypeReference(p.Type, substitutionMap)
                })
                .ToImmutableArray();
        }

        ctx.Log("GenericLift", $"Lifted {liftedGenerics.Count} class generics into method {declaringType.ClrName}.{method.ClrName}");

        return method with
        {
            GenericParameters = combinedGenerics,
            ReturnType = newReturnType,
            Parameters = newParameters
        };
    }

    /// <summary>
    /// Substitutes class-level generic parameter references with 'unknown' type.
    /// This is used for static fields/properties that reference class generics,
    /// which TypeScript doesn't support.
    ///
    /// Returns the original type if it doesn't reference class generics,
    /// or 'unknown' type if it does.
    /// </summary>
    private static TypeReference SubstituteClassGenericsInTypeRef(
        TypeReference typeRef,
        ImmutableArray<GenericParameterSymbol> classGenerics)
    {
        // If no class generics, return original
        if (classGenerics.Length == 0)
            return typeRef;

        // Check if type references any class generic
        var classGenericNames = new HashSet<string>(classGenerics.Select(gp => gp.Name));

        if (ReferencesClassGeneric(typeRef, classGenericNames))
        {
            // Widen to 'unknown' type
            return new NamedTypeReference
            {
                AssemblyName = "TypeScript",
                Namespace = "",
                Name = "unknown",
                FullName = "unknown",
                Arity = 0,
                TypeArguments = ImmutableArray<TypeReference>.Empty,
                IsValueType = false
            };
        }

        return typeRef;
    }

    /// <summary>
    /// Recursively checks if a type reference contains any class-level generic parameters.
    /// </summary>
    private static bool ReferencesClassGeneric(TypeReference typeRef, HashSet<string> classGenericNames)
    {
        return typeRef switch
        {
            GenericParameterReference gp => classGenericNames.Contains(gp.Name),
            ArrayTypeReference arr => ReferencesClassGeneric(arr.ElementType, classGenericNames),
            PointerTypeReference ptr => ReferencesClassGeneric(ptr.PointeeType, classGenericNames),
            ByRefTypeReference byref => ReferencesClassGeneric(byref.ReferencedType, classGenericNames),
            NamedTypeReference named => named.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames)),
            NestedTypeReference nested => nested.FullReference.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames)),
            _ => false
        };
    }

    /// <summary>
    /// TS2693 FIX (Same-Namespace): For types with views in the SAME namespace, heritage clauses
    /// need the instance class name, not the type alias. Type aliases are emitted at module level
    /// (outside namespace) and aren't accessible as VALUES inside namespace declarations.
    /// This only applies to heritage clauses (extends/implements), not method signatures.
    /// </summary>
    private static string ApplyInstanceSuffixForSameNamespaceViews(
        string resolvedName,
        TypeReference typeRef,
        string currentNamespace,
        SymbolGraph graph,
        BuildContext ctx)
    {
        // Only applies to named types in the same namespace
        if (typeRef is not NamedTypeReference named)
            return resolvedName;

        // TS2304 FIX: Skip built-in TypeScript types that come from TypeMap mappings
        // (e.g., System.Delegate → "Function", System.Array → "Array")
        // These should never get $instance suffix
        if (IsBuiltInTypeScriptType(resolvedName))
        {
            ctx.Log("TS2304Fix", $"Skipping $instance suffix for built-in type: {resolvedName}");
            return resolvedName;
        }

        // Look up type symbol to check if it has views
        // CRITICAL: TypeIndex is keyed by ClrFullName (not stable ID format)
        var clrFullName = named.FullName;
        if (!graph.TypeIndex.TryGetValue(clrFullName, out var typeSymbol))
            return resolvedName; // External type

        // Check if it's in the same namespace
        if (typeSymbol.Namespace != currentNamespace)
            return resolvedName; // Cross-namespace (already handled by qualified names)

        // Check if type has views (emits as instance class + type alias)
        if (typeSymbol.ExplicitViews.Length > 0 &&
            (typeSymbol.Kind == Model.Symbols.TypeKind.Class || typeSymbol.Kind == Model.Symbols.TypeKind.Struct))
        {
            // Type has views - return instance class name
            // The type alias "SafeHandle" exists at module level but isn't accessible as a value
            // inside namespace declarations. Must use "SafeHandle$instance".

            // TS2693 FIX: Check if $instance is already present (from TypeNameResolver qualification)
            // Don't double-add $instance suffix
            if (resolvedName.Contains("$instance"))
            {
                return resolvedName; // Already has $instance suffix
            }

            // CRITICAL: If the resolved name contains generic arguments (e.g., "Foo<T>"),
            // we need to insert $instance BEFORE the '<', not at the end:
            //   CORRECT: "Foo$instance<T>"
            //   WRONG:   "Foo<T>$instance" (syntax error!)
            var genericStart = resolvedName.IndexOf('<');
            if (genericStart >= 0)
            {
                // Insert $instance before the generic arguments
                return resolvedName.Substring(0, genericStart) + "$instance" + resolvedName.Substring(genericStart);
            }
            else
            {
                // No generic arguments - just append $instance
                return $"{resolvedName}$instance";
            }
        }

        return resolvedName;
    }

    /// <summary>
    /// TS2304 FIX: Check if an interface reference is in the graph (publicly visible).
    /// Non-public interfaces are not emitted and shouldn't appear in implements clauses.
    /// </summary>
    private static bool IsInterfaceInGraph(TypeReference ifaceRef, SymbolGraph graph)
    {
        if (ifaceRef is not NamedTypeReference named)
            return true; // Non-named types (generic parameters, etc.) are always allowed

        // TypeIndex uses ClrFullName as key (not StableId format with assembly prefix)
        return graph.TypeIndex.TryGetValue(named.FullName, out _);
    }

    /// <summary>
    /// TS2304 FIX: Check if a resolved type name is a built-in TypeScript type.
    /// Built-in types come from TypeMap mappings (e.g., System.Delegate → "Function")
    /// and should never get $instance suffix.
    /// Handles generic arguments (e.g., "Function<T>" → extracts "Function").
    /// </summary>
    private static bool IsBuiltInTypeScriptType(string resolvedName)
    {
        // Extract base name before generic arguments
        var genericStart = resolvedName.IndexOf('<');
        var baseName = genericStart >= 0 ? resolvedName.Substring(0, genericStart) : resolvedName;

        // Built-in types that come from TypeMap or are TypeScript primitives
        return baseName is "Function" or "Array" or "String" or "Number" or "Boolean";
    }

    /// <summary>
    /// TS2416/TS2420 FIX: Get the TypeScript emit name for a method using CLR casing.
    /// Sanitizes reserved words but does NOT apply member style transform (lowercase).
    /// This preserves CLR method names like "Equals", "GetHashCode", "Add" on the surface.
    /// DEPRECATED: Use NameUtilities.ApplyClrSurfaceNamePolicy instead.
    /// </summary>
    private static string GetClrEmitName(string clrName)
    {
        return NameUtilities.ApplyClrSurfaceNamePolicy(clrName);
    }

    /// <summary>
    /// TS2416/TS2420 FIX: Group methods by CLR base name for overload emission.
    /// Groups are partitioned by isStatic.
    /// Returns: Dictionary[clrBaseName -> List of methods with that CLR name]
    /// </summary>
    private static Dictionary<string, List<MethodSymbol>> GroupMethodsByClrName(
        IEnumerable<MethodSymbol> methods,
        bool isStatic)
    {
        return methods
            .Where(m => m.IsStatic == isStatic)
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.StableId.ToString()).ToList());
    }

    /// <summary>
    /// D1: Emit an inherited static field from a base class (for static hierarchy flattening).
    /// </summary>
    private static void EmitInheritedStaticField(StringBuilder sb, FieldSymbol field, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        var fieldEmitName = NameUtilities.ApplyClrSurfaceNamePolicy(field.ClrName);
        sb.Append("    static ");
        if (field.IsReadOnly || field.IsConst)
            sb.Append("readonly ");
        sb.Append(fieldEmitName);
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(field.FieldType, resolver, ctx));
        sb.AppendLine(";");
    }

    /// <summary>
    /// D1: Emit an inherited static property from a base class (for static hierarchy flattening).
    /// </summary>
    private static void EmitInheritedStaticProperty(StringBuilder sb, PropertySymbol property, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        var propEmitName = NameUtilities.ApplyClrSurfaceNamePolicy(property.ClrName);
        sb.Append("    static ");
        if (!property.HasSetter)
            sb.Append("readonly ");
        sb.Append(propEmitName);
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(property.PropertyType, resolver, ctx));
        sb.AppendLine(";");
    }

    /// <summary>
    /// D1: Emit an inherited static method from a base class (for static hierarchy flattening).
    /// Note: MethodPrinter.PrintWithName already includes "static" keyword for static methods.
    /// </summary>
    private static void EmitInheritedStaticMethod(StringBuilder sb, MethodSymbol method, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        var emitName = NameUtilities.ApplyClrSurfaceNamePolicy(method.ClrName);
        sb.Append("    ");
        sb.Append(MethodPrinter.PrintWithName(method, derivedType, emitName, resolver, ctx));
        sb.AppendLine(";");
    }

    /// <summary>
    /// PR C: Check if an interface is unsatisfiable for this type (honest emission).
    /// Returns true if the interface should be omitted from the 'implements' clause.
    /// </summary>
    private static bool IsUnsatisfiableInterface(TypeSymbol type, TypeReference interfaceRef, Plan.HonestEmissionPlan? honestPlan)
    {
        if (honestPlan == null)
            return false;

        if (!honestPlan.UnsatisfiableInterfaces.TryGetValue(type.ClrFullName, out var unsatisfiableList))
            return false;

        // Extract CLR full name from interface reference
        var interfaceClrName = interfaceRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => null
        };

        if (interfaceClrName == null)
            return false;

        return unsatisfiableList.Any(u => u.InterfaceClrName == interfaceClrName);
    }
}
