using System;
using System.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core;
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
            TypeKind.Interface => PrintInterface(type, resolver, ctx, graph),
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

    /// <summary>
    /// STATIC-SIDE FIX: Print value export for constructors and static members.
    /// Emits: export const TypeName: { new(...): Instance; statics... } = null!;
    /// This removes static-side inheritance entirely, fixing TS2417.
    /// </summary>
    public static string PrintValueExport(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, Shape.StaticConflictPlan? staticConflicts = null)
    {
        // GUARD: Never print non-public types
        if (type.Accessibility != Accessibility.Public)
            return string.Empty;

        // Only classes and structs have value exports (not interfaces, enums, delegates)
        if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
            return string.Empty;

        // Static classes use different emission pattern (abstract class)
        if (type.IsStatic || type.Kind == TypeKind.StaticNamespace)
            return string.Empty;

        var sb = new StringBuilder();

        // Get type names
        var finalName = ctx.Renamer.GetFinalTypeName(type);
        var instanceName = ctx.Renamer.GetInstanceTypeName(type);

        // Generic parameters for the const declaration
        // Note: export const with generics uses function syntax, not generic class syntax
        // For generic types, we'll emit a callable/newable interface
        var hasGenerics = type.GenericParameters.Length > 0;
        string genericParams = "";
        string genericArgs = "";
        if (hasGenerics)
        {
            genericParams = "<" + string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))) + ">";
            genericArgs = "<" + string.Join(", ", type.GenericParameters.Select(gp => gp.Name)) + ">";
        }

        var members = type.Members;

        // Some CLR base classes are designed to be subclassed by user code but have no public constructors
        // (e.g., abstract base classes with protected ctors). TypeScript still requires the base expression
        // in `class D extends Base {}` to be constructable, otherwise `extends Base` fails to typecheck.
        //
        // To keep TS-valid inheritance WITHOUT introducing unstable renames (Dispose2) or requiring
        // consumers to import internal symbols, we attach an *abstract constructor type* to the value
        // export for eligible types. This enables `extends` while (in TS) still rejecting `new Base()`.
        //
        // NOTE: TypeScript does NOT allow `abstract new(...)` inside an object type literal, so we
        // express the constructor surface as an intersection with a constructor type:
        //   export const Base: (abstract new (...) => Base) & { ...static members... }
        //
        // We intentionally do NOT do this for CLR "magic" base types that cannot be subclassed via C#
        // (Delegate/MulticastDelegate/Enum), where `extends` should fail.
        string? abstractCtorType = null;
        if (type.Kind == TypeKind.Class &&
            type.Accessibility == Accessibility.Public &&
            type.ClrFullName is not ("System.Delegate" or "System.MulticastDelegate" or "System.Enum"))
        {
            var accessibleCtors = members.Constructors
                .Where(c =>
                    !c.IsStatic &&
                    (c.Visibility == Visibility.Public ||
                     c.Visibility == Visibility.Protected ||
                     c.Visibility == Visibility.ProtectedInternal))
                .ToList();

            var hasPublicCtor = accessibleCtors.Any(c => c.Visibility == Visibility.Public);
            var hasProtectedCtor = accessibleCtors.Any(c =>
                c.Visibility == Visibility.Protected ||
                c.Visibility == Visibility.ProtectedInternal);

            // Only attach abstract ctor typing when the type is NOT directly constructible from user code.
            // - abstract types (even if they have public ctors)
            // - non-abstract types with only protected/protected-internal ctors
            var needsAbstractCtorTyping =
                (type.IsAbstract && accessibleCtors.Count > 0) ||
                (!type.IsAbstract && !hasPublicCtor && hasProtectedCtor);

            if (needsAbstractCtorTyping)
            {
                var ctorSigs = new List<string>();
                foreach (var ctor in accessibleCtors)
                {
                    var sig = new StringBuilder();
                    sig.Append("abstract new");
                    if (hasGenerics) sig.Append(genericParams);
                    sig.Append("(");
                    sig.Append(string.Join(", ", ctor.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, resolver, ctx)}")));
                    sig.Append(") => ");
                    sig.Append(finalName);
                    if (hasGenerics) sig.Append(genericArgs);
                    ctorSigs.Add("(" + sig + ")");
                }

                if (ctorSigs.Count > 0)
                {
                    abstractCtorType = string.Join(" & ", ctorSigs);
                }
            }
        }

        // Start: export const TypeName: [abstract ctor type] & { ... }
        sb.Append("export const ");
        sb.Append(finalName);
        sb.Append(": ");
        if (!string.IsNullOrEmpty(abstractCtorType))
        {
            sb.Append(abstractCtorType);
            sb.Append(" & ");
        }
        sb.AppendLine("{");

        // Create type scope for member name resolution
        var staticTypeScope = ScopeFactory.ClassStatic(type);

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

        // Constructors: new(...): FinalType (not $instance!)
        // NOTE: We emit "new" only when the CLR type is constructible from user code:
        //   - type is NOT abstract
        //   - constructor is a public instance ctor
        // This avoids incorrectly making abstract types and protected-only ctor types instantiable
        // in TypeScript (e.g., System.Delegate, System.MulticastDelegate, System.Enum).
        //
        // The constructor must return the full intersection type (List_1<T>), not just the instance
        // interface (List_1$instance<T>), otherwise `const x: List<T> = new List<T>()` fails
        // because List_1$instance<T> is not assignable to List_1<T> (missing views intersection).
        if (!type.IsAbstract)
        {
            foreach (var ctor in members.Constructors.Where(c => !c.IsStatic && c.Visibility == Visibility.Public))
            {
                sb.Append("    new");
                if (hasGenerics) sb.Append(genericParams);
                sb.Append("(");
                sb.Append(string.Join(", ", ctor.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, resolver, ctx)}")));
                sb.Append("): ");
                sb.Append(finalName);
                if (hasGenerics) sb.Append(genericArgs);
                sb.AppendLine(";");
            }
        }

        // Structs always have an implicit public parameterless constructor.
        // Some structs may not declare any public .ctor in metadata, so add one deterministically.
        if (type.Kind == TypeKind.Struct && !members.Constructors.Any(c => !c.IsStatic && c.Visibility == Visibility.Public))
        {
            sb.Append("    new");
            if (hasGenerics) sb.Append(genericParams);
            sb.Append("(): ");
            sb.Append(finalName);
            if (hasGenerics) sb.Append(genericArgs);
            sb.AppendLine(";");
        }

        // Static fields - only emit ClassSurface or StaticSurface members
        foreach (var field in members.Fields.Where(f => f.IsStatic && !f.IsConst &&
            (f.EmitScope == EmitScope.ClassSurface || f.EmitScope == EmitScope.StaticSurface)))
        {
            if (ShouldSuppressMember(field.StableId.ToString()))
                continue;

            var finalMemberName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope);
            sb.Append("    ");
            if (field.IsReadOnly)
                sb.Append("readonly ");
            sb.Append(finalMemberName);
            sb.Append(": ");

            var fieldType = SubstituteClassGenericsInTypeRef(field.FieldType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(fieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Const fields (as readonly)
        foreach (var field in members.Fields.Where(f => f.IsConst &&
            (f.EmitScope == EmitScope.ClassSurface || f.EmitScope == EmitScope.StaticSurface)))
        {
            if (ShouldSuppressMember(field.StableId.ToString()))
                continue;

            var emitName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope);
            sb.Append("    readonly ");
            sb.Append(emitName);
            sb.Append(": ");

            var fieldType = SubstituteClassGenericsInTypeRef(field.FieldType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(fieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Static properties
        foreach (var prop in members.Properties.Where(p => p.IsStatic &&
            (p.EmitScope == EmitScope.ClassSurface || p.EmitScope == EmitScope.StaticSurface)))
        {
            if (ShouldSuppressMember(prop.StableId.ToString()))
                continue;

            var emitName = ctx.Renamer.GetFinalMemberName(prop.StableId, staticTypeScope);
            var propType = SubstituteClassGenericsInTypeRef(prop.PropertyType, type.GenericParameters);

            // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
            EmitProperty(sb, prop, emitName, propType, resolver, ctx);
        }

        // Static methods
        var staticMethods = members.Methods
            .Where(m => m.IsStatic && (m.EmitScope == EmitScope.ClassSurface || m.EmitScope == EmitScope.StaticSurface))
            .ToList();

        var staticMethodGroups = GroupMethodsByClrName(staticMethods, isStatic: true);

        foreach (var (clrName, overloads) in staticMethodGroups.OrderBy(kvp => kvp.Key))
        {
            var firstMethod = overloads.First();
            var emitName = ctx.Renamer.GetFinalMemberName(firstMethod.StableId, staticTypeScope);

            foreach (var method in overloads)
            {
                if (ShouldSuppressMember(method.StableId.ToString()))
                    continue;

                // Skip abstract static methods in concrete classes
                if (!type.IsAbstract && method.IsAbstract)
                    continue;

                // Lift class generic parameters into method
                var liftedMethod = LiftClassGenericsToMethod(method, type, ctx);

                sb.Append("    ");
                // Emit method signature without 'static' keyword (it's in an object literal type)
                sb.Append(MethodPrinter.PrintSignatureOnly(liftedMethod, type, emitName, resolver, ctx));
                sb.AppendLine(";");
            }
        }

        // Close: };
        // Note: No initializer in .d.ts files - just the type annotation
        sb.AppendLine("};");

        return sb.ToString();
    }

    private static string PrintClass(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, bool instanceSuffix = false, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        var sb = new StringBuilder();

        // STEP 1: Always use instance type name for classes
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        // STATIC-SIDE FIX: Emit interface instead of class to avoid TS2417 static-side inheritance
        // TypeScript checks that 'typeof Derived' extends 'typeof Base' when using 'class extends',
        // but .NET static methods are not polymorphic - derived types can have different overload sets.
        // Using interface for instance side removes this constraint entirely.
        sb.Append("interface ");
        sb.Append(finalName);

        // Generic parameters: class Foo<T, U>
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // STATIC-SIDE FIX: Build extends list from base class AND interfaces
        // Interfaces use "extends" for all inheritance (not "implements")
        var extendsList = new List<string>();

        // Base class: extends BaseClass$instance
        // D1 FIX: Skip extends for static-only types that are being flattened
        var shouldFlatten = staticFlattening?.ShouldFlattenType(type.StableId.ToString()) ?? false;

        if (type.BaseType != null && !shouldFlatten)
        {
            // Pass forValuePosition=false since this is an interface (type position)
            var baseTypeName = TypeRefPrinter.Print(type.BaseType, resolver, ctx, allowedTypeParameterNames: null, forValuePosition: false);
            // TS2693 FIX (Same-Namespace): For same-namespace types with views, use instance class name
            baseTypeName = ApplyInstanceSuffixForSameNamespaceViews(baseTypeName, type.BaseType, type.Namespace, graph, ctx);

            // Skip System.Object, System.ValueType, and any fallback types (any, unknown)
            if (baseTypeName != "Object" &&
                baseTypeName != "ValueType" &&
                baseTypeName != "any" &&
                baseTypeName != "unknown")
            {
                extendsList.Add(baseTypeName);
            }
        }
        else if (shouldFlatten)
        {
            ctx.Log("StaticFlattening", $"  Suppressing extends for static-only type: {type.ClrFullName}");
        }

        // Interfaces: extends IFoo$instance, IBar$instance
        // TS2304 FIX: Filter out non-public interfaces (not in graph)
        // PR C: Filter out unsatisfiable interfaces (honest emission)
        var publicInterfaces = type.Interfaces
            .Where(i => IsInterfaceInGraph(i, graph))
            .Where(i => !IsUnsatisfiableInterface(type, i, honestEmission))
            .ToArray();

        foreach (var iface in publicInterfaces)
        {
            var ifaceName = TypeRefPrinter.Print(iface, resolver, ctx, allowedTypeParameterNames: null, forValuePosition: false);
            ifaceName = ApplyInstanceSuffixForSameNamespaceViews(ifaceName, iface, type.Namespace, graph, ctx);
            extendsList.Add(ifaceName);
        }

        // Emit extends clause if there are any base types/interfaces
        if (extendsList.Count > 0)
        {
            sb.Append(" extends ");
            sb.Append(string.Join(", ", extendsList));
        }

        sb.AppendLine(" {");

        // NOMINAL CLR TYPES: Attach a per-type brand for all class/struct types.
        // This prevents structural false positives between unrelated CLR types that
        // happen to share members (e.g., List<T> structurally matching ParallelQuery<T>).
        EmitNominalClrTypeBrand(sb, type);

        // NOMINAL CLR INTERFACES: Attach interface brands for all implemented CLR interfaces
        // (including explicit views and inherited interfaces). This prevents structural matches
        // (e.g., GetAsyncEnumerator pattern) from incorrectly making a type appear to implement
        // an interface like IAsyncEnumerable<T> when it does not in CLR metadata.
        EmitNominalClrInterfaceBrands(sb, type, graph);

        // STATIC-SIDE FIX: Emit only INSTANCE members for the interface
        // Static members and constructors will be emitted separately in PrintValueExport
        EmitInstanceMembersOnly(sb, type, resolver, ctx, graph, bindingsProvider, overrideConflicts, propertyOverrides);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string PrintStruct(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, bool instanceSuffix = false, BindingsProvider? bindingsProvider = null, Shape.StaticFlatteningPlan? staticFlattening = null, Shape.StaticConflictPlan? staticConflicts = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null, Plan.HonestEmissionPlan? honestEmission = null)
    {
        // STATIC-SIDE FIX: Structs emit as interfaces (like classes) to avoid static-side inheritance issues
        // Constructors and static members go in the const declaration (PrintValueExport)
        var sb = new StringBuilder();

        // STEP 1: Always use instance type name for structs
        var finalName = ctx.Renamer.GetInstanceTypeName(type);

        // STATIC-SIDE FIX: Emit interface instead of class
        sb.Append("interface ");
        sb.Append(finalName);

        // Generic parameters
        if (type.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Interfaces - STATIC-SIDE FIX: Use "extends" (interfaces use extends, not implements)
        // TS2304 FIX: Filter out non-public interfaces (not in graph)
        // PR C: Filter out unsatisfiable interfaces (honest emission)
        var publicInterfaces = type.Interfaces
            .Where(i => IsInterfaceInGraph(i, graph))
            .Where(i => !IsUnsatisfiableInterface(type, i, honestEmission))
            .ToArray();

        if (publicInterfaces.Length > 0)
        {
            sb.Append(" extends ");
            sb.Append(string.Join(", ", publicInterfaces.Select(i => TypeRefPrinter.Print(i, resolver, ctx))));
        }

        sb.AppendLine(" {");

        // NOMINAL CLR TYPES: Attach a per-type brand for all class/struct types.
        // This prevents structural false positives between unrelated CLR types that
        // happen to share members (e.g., List<T> structurally matching ParallelQuery<T>).
        EmitNominalClrTypeBrand(sb, type);

        // NOMINAL CLR INTERFACES: Attach interface brands for all implemented CLR interfaces
        // (including explicit views and inherited interfaces). This prevents structural matches
        // (e.g., GetAsyncEnumerator pattern) from incorrectly making a type appear to implement
        // an interface like IAsyncEnumerable<T> when it does not in CLR metadata.
        EmitNominalClrInterfaceBrands(sb, type, graph);

        // STATIC-SIDE FIX: Emit only instance members (no constructors, no statics)
        // Constructors and static members go in PrintValueExport
        EmitInstanceMembersOnly(sb, type, resolver, ctx, graph, bindingsProvider, overrideConflicts, propertyOverrides: null);

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

        // Find Invoke method - this is REQUIRED for delegate printing.
        // Concrete delegates (Func, Action, custom delegates) always have Invoke.
        // If Invoke is missing, this type was incorrectly classified as a delegate.
        var invokeMethod = type.Members.Methods.FirstOrDefault(m => m.ClrName == "Invoke");
        if (invokeMethod == null)
        {
            throw new InvalidOperationException(
                $"Delegate type '{type.ClrFullName}' has no Invoke method. " +
                $"This type should not have been classified as TypeKind.Delegate. " +
                $"System.Delegate and System.MulticastDelegate must be excluded from delegate classification.");
        }

        // Emit function signature: (arg1: T1, arg2: T2) => TResult
        sb.Append('(');
        sb.Append(string.Join(", ", invokeMethod.Parameters.Select(p => $"{p.Name}: {TypeRefPrinter.Print(p.Type, resolver, ctx)}")));
        sb.Append(") => ");
        sb.Append(TypeRefPrinter.Print(invokeMethod.ReturnType, resolver, ctx));

        sb.AppendLine(";");

        return sb.ToString();
    }

    private static string PrintInterface(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, SymbolGraph graph)
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

        // NOMINAL CLR INTERFACES: Prevent TypeScript structural typing ("duck typing") from
        // treating any structurally compatible object as a CLR interface.
        //
        // This brand is a phantom field. It is populated on CLR types that implement this
        // interface (including via base types) during emission (see EmitNominalClrInterfaceBrands).
        sb.Append("    readonly ");
        sb.Append(NameUtilities.GetClrInterfaceBrandPropertyName(type.ClrFullName));
        sb.AppendLine(": never;");
        sb.AppendLine();

        // Emit members (interfaces only have instance members)
        // Pass graph to collect inherited method overloads for TS2430 fix
        EmitInterfaceMembers(sb, type, resolver, ctx, graph);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitNominalClrInterfaceBrands(StringBuilder sb, TypeSymbol type, SymbolGraph graph)
    {
        // Only class-like types can "implement" interfaces (classes/structs). Interfaces emit
        // their own brand directly in PrintInterface.
        if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
            return;

        var interfaceFullNames = CollectInterfaceBrandFullNames(type, graph);
        if (interfaceFullNames.Count == 0)
            return;

        foreach (var fullName in interfaceFullNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.Append("    readonly ");
            sb.Append(NameUtilities.GetClrInterfaceBrandPropertyName(fullName));
            sb.AppendLine(": never;");
        }

        sb.AppendLine();
    }

    private static void EmitNominalClrTypeBrand(StringBuilder sb, TypeSymbol type)
    {
        if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
            return;

        sb.Append("    readonly ");
        sb.Append(NameUtilities.GetClrTypeBrandPropertyName(type.ClrFullName));
        sb.AppendLine(": never;");
        sb.AppendLine();
    }

    private static HashSet<string> CollectInterfaceBrandFullNames(TypeSymbol type, SymbolGraph graph)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var visitedTypes = new HashSet<string>(StringComparer.Ordinal);

        void AddInterface(TypeReference ifaceRef)
        {
            var fullName = ifaceRef switch
            {
                NamedTypeReference named => named.FullName,
                NestedTypeReference nested => nested.FullReference.FullName,
                _ => null
            };
            if (string.IsNullOrEmpty(fullName))
                return;

            // NOTE: We intentionally do NOT consult the symbol graph here.
            //
            // In --lib mode, external BCL types are filtered out of the graph, but we still
            // need nominal interface brands for their interface identities so extension method
            // selection and assignability stay CLR-faithful.
            //
            // TypeSymbol.Interfaces is sourced from CLR metadata and represents implemented
            // interfaces (typically including inherited interfaces as well), so the FullName
            // is the correct stable key for branding.
            result.Add(fullName);
        }

        void AddTypeInterfacesRecursive(TypeReference? typeRef)
        {
            if (typeRef == null)
                return;

            var fullName = typeRef switch
            {
                NamedTypeReference named => named.FullName,
                NestedTypeReference nested => nested.FullReference.FullName,
                _ => null
            };
            if (string.IsNullOrEmpty(fullName))
                return;

            if (!visitedTypes.Add(fullName))
                return;

            if (!graph.TypeIndex.TryGetValue(fullName, out var typeSymbol))
                return;

            foreach (var iface in typeSymbol.Interfaces)
                AddInterface(iface);

            foreach (var view in typeSymbol.ExplicitViews)
                AddInterface(view.InterfaceReference);

            AddTypeInterfacesRecursive(typeSymbol.BaseType);
        }

        foreach (var iface in type.Interfaces)
            AddInterface(iface);

        foreach (var view in type.ExplicitViews)
            AddInterface(view.InterfaceReference);

        AddTypeInterfacesRecursive(type.BaseType);

        return result;
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
        foreach (var field in members.Fields.Where(f => !f.IsStatic && f.EmitScope == EmitScope.ClassSurface))
        {
            // Get final name from Renamer (applies camelCase transform if configured)
            var emitName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope);

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

                // E: Check for property override unification
                var key = (type.StableId.ToString(), ownProperty.Property.StableId.ToString());
                string? overrideType = null;
                propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out overrideType);

                // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
                EmitProperty(sb, propToEmit, tsName, propToEmit.PropertyType, resolver, ctx, overrideType);
            }
        }
        else
        {
            // Fallback: Old path for types without bindings
            foreach (var prop in members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
            {
                // D3: Skip if this instance property conflicts with base class
                if (ShouldSuppressMember(prop.StableId.ToString()))
                    continue;

                // Get final name from Renamer (applies camelCase transform if configured)
                var emitName = ctx.Renamer.GetFinalMemberName(prop.StableId, typeScope);

                // FIX D EXTENSION: Substitute generic parameters for properties from interfaces
                var propToEmit = SubstituteMemberIfNeeded(type, prop, ctx, graph);

                // E: Check for property override unification
                var key = (type.StableId.ToString(), prop.StableId.ToString());
                string? overrideType = null;
                propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out overrideType);

                // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
                EmitProperty(sb, propToEmit, emitName, propToEmit.PropertyType, resolver, ctx, overrideType);
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
                // Get final name from Renamer using first method in overload group
                var firstMethod = overloads.First();
                var emitName = ctx.Renamer.GetFinalMemberName(firstMethod.StableId, typeScope);

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

                    // TS2512 FIX: Pass group-level abstract status to ensure consistency
                    sb.Append(MethodPrinter.PrintWithName(methodToEmit, type, emitName, resolver, ctx, emitAbstract: groupIsAbstract));
                    sb.AppendLine(";");
                }
            }
        }

        // Static members
        EmitStaticMembers(sb, type, resolver, ctx, staticConflicts);
    }

    /// <summary>
    /// STATIC-SIDE FIX: Emit only INSTANCE members (no constructors, no static members).
    /// Used when emitting interface for instance side of class.
    /// </summary>
    private static void EmitInstanceMembersOnly(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, Model.SymbolGraph graph, BindingsProvider? bindingsProvider = null, Shape.OverrideConflictPlan? overrideConflicts = null, Plan.PropertyOverridePlan? propertyOverrides = null)
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

        // NO CONSTRUCTORS - they go in the value export (const declaration)

        // Fields - only emit ClassSurface members, no static fields
        foreach (var field in members.Fields.Where(f => !f.IsStatic && f.EmitScope == EmitScope.ClassSurface && f.Visibility == Visibility.Public))
        {
            // Get final name from Renamer (applies camelCase transform if configured)
            var emitName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope);

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
            var propertyGroups = exposedProperties
                .GroupBy(e => e.Property.ClrName)
                .OrderBy(g => g.Key);

            foreach (var group in propertyGroups)
            {
                var exposures = group.ToList();

                // Only emit properties where we have an OWN (non-inherited) exposure
                var ownProperty = exposures.FirstOrDefault(e => !e.IsInherited);
                if (ownProperty == null)
                    continue;

                // Airplane-grade rule: do not generate unstable numeric renames (Dispose2, SendAsync2, ...)
                // just to preserve TS access modifiers. We expose protected virtual/abstract/override
                // members on the instance surface so overload families can share the CLR name.
                // C# compilation enforces true accessibility.
                var propVisibility = ownProperty.Property.Visibility;
                var isPublic = propVisibility == Visibility.Public;
                var isProtectedVirtual =
                    (propVisibility == Visibility.Protected || propVisibility == Visibility.ProtectedInternal) &&
                    (ownProperty.Property.IsVirtual || ownProperty.Property.IsAbstract || ownProperty.Property.IsOverride);
                if (!isPublic && !isProtectedVirtual)
                    continue;

                var tsName = ownProperty.TsName;

                // D3: Skip if this instance property conflicts with base class
                if (ShouldSuppressMember(ownProperty.Property.StableId.ToString()))
                    continue;

                // FIX D EXTENSION: Substitute generic parameters for properties from interfaces
                var propToEmit = SubstituteMemberIfNeeded(type, ownProperty.Property, ctx, graph);

                // E: Check for property override unification
                var key = (type.StableId.ToString(), ownProperty.Property.StableId.ToString());
                string? overrideType = null;
                propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out overrideType);

                // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
                EmitProperty(sb, propToEmit, tsName, propToEmit.PropertyType, resolver, ctx, overrideType);
            }
        }
        else
        {
            // Fallback: Old path for types without bindings
            foreach (var prop in members.Properties.Where(p =>
                         !p.IsStatic &&
                         p.EmitScope == EmitScope.ClassSurface &&
                         (p.Visibility == Visibility.Public ||
                          ((p.Visibility == Visibility.Protected || p.Visibility == Visibility.ProtectedInternal) &&
                           (p.IsVirtual || p.IsAbstract || p.IsOverride)))))
            {
                // D3: Skip if this instance property conflicts with base class
                if (ShouldSuppressMember(prop.StableId.ToString()))
                    continue;

                var emitName = ctx.Renamer.GetFinalMemberName(prop.StableId, typeScope);
                var propToEmit = SubstituteMemberIfNeeded(type, prop, ctx, graph);

                // E: Check for property override unification
                var key = (type.StableId.ToString(), prop.StableId.ToString());
                string? overrideType = null;
                propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out overrideType);

                // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
                EmitProperty(sb, propToEmit, emitName, propToEmit.PropertyType, resolver, ctx, overrideType);
            }
        }

        // Methods - only emit ClassSurface members, no static methods
        var shouldSkipAbstract = !type.IsAbstract;

        // V2: Use ExposedMethods from bindings if available (own + inherited)
        var exposedMethods = bindingsProvider?.GetExposedMethods(type);
        if (exposedMethods != null)
        {
            var methodGroups = exposedMethods
                .GroupBy(e => e.Method.ClrName)
                .OrderBy(g => g.Key);

            foreach (var group in methodGroups)
            {
                var exposures = group.ToList();

                var ownMethods = exposures
                    .Where(e =>
                        !e.IsInherited &&
                        (e.Method.Visibility == Visibility.Public ||
                         ((e.Method.Visibility == Visibility.Protected || e.Method.Visibility == Visibility.ProtectedInternal) &&
                          (e.Method.IsVirtual || e.Method.IsAbstract || e.Method.IsOverride))))
                    .ToList();
                if (!ownMethods.Any())
                    continue;

                string tsName;
                var preferredName = ownMethods.FirstOrDefault(m => !NameUtilities.HasNumericSuffix(m.TsName));
                if (preferredName != null)
                    tsName = preferredName.TsName;
                else
                    tsName = ownMethods.First().TsName;

                // STATIC-SIDE FIX: Interfaces don't have abstract keyword - all methods are implicitly abstract
                // Since we're now emitting classes as interfaces, we never emit abstract

                foreach (var exposure in ownMethods)
                {
                    // D3: Skip if this instance method conflicts with base class
                    if (ShouldSuppressMember(exposure.Method.StableId.ToString()))
                        continue;

                    if (shouldSkipAbstract && exposure.Method.IsAbstract)
                        continue;

                    sb.Append("    ");

                    var methodToEmit = SubstituteMemberIfNeeded(type, exposure.Method, ctx, graph);
                    // Never emit abstract - we're emitting as interface now
                    sb.Append(MethodPrinter.PrintWithName(methodToEmit, type, tsName, resolver, ctx, emitAbstract: false));
                    sb.AppendLine(";");
                }
            }
        }
        else
        {
            // V1 fallback path: Use only type's own methods
            var instanceMethods = members.Methods
                .Where(m =>
                    !m.IsStatic &&
                    m.EmitScope == EmitScope.ClassSurface &&
                    (m.Visibility == Visibility.Public ||
                     ((m.Visibility == Visibility.Protected || m.Visibility == Visibility.ProtectedInternal) &&
                      (m.IsVirtual || m.IsAbstract || m.IsOverride))))
                .ToList();

            var methodGroups = GroupMethodsByClrName(instanceMethods, isStatic: false);

            foreach (var (clrName, overloads) in methodGroups.OrderBy(kvp => kvp.Key))
            {
                var firstMethod = overloads.First();
                var emitName = ctx.Renamer.GetFinalMemberName(firstMethod.StableId, typeScope);

                // STATIC-SIDE FIX: Interfaces don't have abstract keyword - all methods are implicitly abstract
                // Since we're now emitting classes as interfaces, we never emit abstract

                foreach (var method in overloads)
                {
                    if (ShouldSuppressMember(method.StableId.ToString()))
                        continue;

                    if (shouldSkipAbstract && method.IsAbstract)
                        continue;

                    sb.Append("    ");

                    var methodToEmit = SubstituteMemberIfNeeded(type, method, ctx, graph);
                    // Never emit abstract - we're emitting as interface now
                    sb.Append(MethodPrinter.PrintWithName(methodToEmit, type, emitName, resolver, ctx, emitAbstract: false));
                    sb.AppendLine(";");
                }
            }
        }

        // Task/ValueTask thenable shim (TypeScript async/await compatibility):
        // Make System.Threading.Tasks.Task / Task<TResult> structurally PromiseLike so:
        // - `await task` typechecks in vanilla tsc
        // - `async function f(): Task<T>` is accepted by tsc
        //
        // This is TYPE-ONLY; Tsonic bans `.then/.catch/.finally` usage and lowers async/await to CLR Task.
        EmitTaskThenableShimIfNeeded(sb, type);

        // NO STATIC MEMBERS - they go in the value export (const declaration)
    }

    private static void EmitTaskThenableShimIfNeeded(StringBuilder sb, TypeSymbol type)
    {
        // NOTE: Keep this logic deterministic and minimal; do not rely on external configuration.
        // We intentionally do NOT emit catch/finally; a single `then` is sufficient for TS await/async checks.

        var isTask =
            type.ClrFullName == "System.Threading.Tasks.Task" ||
            type.ClrFullName.StartsWith("System.Threading.Tasks.Task`", StringComparison.Ordinal) ||
            type.ClrFullName.StartsWith("System.Threading.Tasks.Task\u0060", StringComparison.Ordinal);

        var isValueTask =
            type.ClrFullName == "System.Threading.Tasks.ValueTask" ||
            type.ClrFullName.StartsWith("System.Threading.Tasks.ValueTask`", StringComparison.Ordinal) ||
            type.ClrFullName.StartsWith("System.Threading.Tasks.ValueTask\u0060", StringComparison.Ordinal);

        if (!isTask && !isValueTask)
            return;

        // Non-generic Task/ValueTask behaves like PromiseLike<void> for await purposes.
        var awaitedType =
            type.GenericParameters.Length == 1
                ? type.GenericParameters[0].Name
                : "void";

        sb.Append("    then<TResult1 = ");
        sb.Append(awaitedType);
        sb.Append(", TResult2 = never>(");
        sb.Append("onfulfilled?: ((value: ");
        sb.Append(awaitedType);
        sb.Append(") => TResult1 | PromiseLike<TResult1>) | undefined | null, ");
        sb.Append("onrejected?: ((reason: any) => TResult2 | PromiseLike<TResult2>) | undefined | null");
        sb.AppendLine("): PromiseLike<TResult1 | TResult2>;");
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
        foreach (var field in members.Fields.Where(f => f.IsConst &&
            (f.EmitScope == EmitScope.ClassSurface || f.EmitScope == EmitScope.StaticSurface)))
        {
            // D2: Skip if this const field conflicts with base class
            if (ShouldSuppressMember(field.StableId.ToString()))
                continue;

            // Get final name from Renamer (applies camelCase transform if configured)
            var emitName = ctx.Renamer.GetFinalMemberName(field.StableId, staticTypeScope);

            sb.Append("    static readonly ");
            sb.Append(emitName);
            sb.Append(": ");

            var fieldType = SubstituteClassGenericsInTypeRef(field.FieldType, type.GenericParameters);
            sb.Append(TypeRefPrinter.Print(fieldType, resolver, ctx));
            sb.AppendLine(";");
        }

        // Static properties - only emit ClassSurface or StaticSurface members
        // NOTE: If property type references class generics, widen to 'unknown' (TypeScript limitation)
        foreach (var prop in members.Properties.Where(p => p.IsStatic &&
            (p.EmitScope == EmitScope.ClassSurface || p.EmitScope == EmitScope.StaticSurface)))
        {
            // D2: Skip if this static property conflicts with base class
            if (ShouldSuppressMember(prop.StableId.ToString()))
                continue;

            // Get final name from Renamer (applies camelCase transform if configured)
            var emitName = ctx.Renamer.GetFinalMemberName(prop.StableId, staticTypeScope);
            var propType = SubstituteClassGenericsInTypeRef(prop.PropertyType, type.GenericParameters);

            // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
            EmitProperty(sb, prop, emitName, propType, resolver, ctx, overrideType: null, isStatic: true);
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
            // Get final name from Renamer using first method in overload group
            var firstMethod = overloads.First();
            var emitName = ctx.Renamer.GetFinalMemberName(firstMethod.StableId, staticTypeScope);

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

    private static void EmitInterfaceMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, SymbolGraph graph)
    {
        var members = type.Members;

        // Create scope for member name resolution (interfaces use instance scope)
        var instanceScope = ScopeFactory.ClassInstance(type);

        // Properties - only emit ClassSurface members, skip static (TypeScript doesn't support static interface members)
        foreach (var prop in members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
        {
            // Get final name from Renamer (applies camelCase transform if configured)
            var emitName = ctx.Renamer.GetFinalMemberName(prop.StableId, instanceScope);

            // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
            EmitProperty(sb, prop, emitName, prop.PropertyType, resolver, ctx);
        }

        // TS2430 FIX: Collect inherited method signatures that need to be emitted as overloads
        // When an interface extends multiple interfaces with same method name but different signatures,
        // TypeScript requires the derived interface to be compatible with ALL parent signatures.
        // We emit all distinct inherited signatures as overloads.
        var inheritedMethodSignatures = CollectInheritedMethodSignatures(type, resolver, ctx, graph);

        // Methods - only emit ClassSurface members, skip static (TypeScript doesn't support static interface members)
        // Group by CLR name, emit as TypeScript overload sets
        var interfaceMethods = members.Methods
            .Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface)
            .ToList();

        var methodGroups = GroupMethodsByClrName(interfaceMethods, isStatic: false);

        // Track which methods we've emitted (by emitName) so we can add inherited overloads
        var emittedMethodNames = new HashSet<string>();

        foreach (var (clrName, overloads) in methodGroups.OrderBy(kvp => kvp.Key))
        {
            // Get final name from Renamer using first method in overload group
            var firstMethod = overloads.First();
            var emitName = ctx.Renamer.GetFinalMemberName(firstMethod.StableId, instanceScope);
            emittedMethodNames.Add(emitName);

            // Collect signatures we're emitting to avoid duplicates with inherited
            var emittedSignatures = new HashSet<string>();

            // Emit each overload signature (interfaces have no abstract keyword)
            foreach (var method in overloads)
            {
                var sig = MethodPrinter.PrintWithName(method, type, emitName, resolver, ctx);
                sb.Append("    ");
                sb.Append(sig);
                sb.AppendLine(";");
                emittedSignatures.Add(sig);
            }

            // TS2430 FIX: Emit inherited overloads with different signatures
            if (inheritedMethodSignatures.TryGetValue(emitName, out var inheritedSigs))
            {
                foreach (var inheritedSig in inheritedSigs.OrderBy(s => s))
                {
                    // Only emit if we haven't already emitted an identical signature
                    if (!emittedSignatures.Contains(inheritedSig))
                    {
                        sb.Append("    ");
                        sb.Append(inheritedSig);
                        sb.AppendLine(";");
                        emittedSignatures.Add(inheritedSig);
                    }
                }
            }
        }

        // TS2430 FIX: Emit inherited methods that this interface doesn't declare at all
        // These are needed for TypeScript to see the interface as compatible with base interfaces
        foreach (var (methodName, inheritedSigs) in inheritedMethodSignatures.OrderBy(kvp => kvp.Key))
        {
            if (emittedMethodNames.Contains(methodName))
                continue; // Already handled above

            var emittedSignatures = new HashSet<string>();
            foreach (var sig in inheritedSigs.OrderBy(s => s))
            {
                if (!emittedSignatures.Contains(sig))
                {
                    sb.Append("    ");
                    sb.Append(sig);
                    sb.AppendLine(";");
                    emittedSignatures.Add(sig);
                }
            }
        }
    }

    /// <summary>
    /// TS2430 FIX: Collect method signatures from inherited interfaces.
    /// When an interface extends multiple interfaces with conflicting method signatures,
    /// we need to emit all distinct signatures as overloads.
    /// Returns: Dictionary of methodEmitName → List of signature strings
    /// </summary>
    private static Dictionary<string, List<string>> CollectInheritedMethodSignatures(
        TypeSymbol type,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph)
    {
        var result = new Dictionary<string, List<string>>();

        // Recursively collect from all inherited interfaces
        CollectMethodSignaturesRecursive(type.Interfaces, resolver, ctx, graph, result, new Dictionary<string, TypeReference>());

        return result;
    }

    /// <summary>
    /// Recursively collect method signatures from parent interfaces.
    /// Skip methods that use self-referential generic parameters (like compareTo(other: TSelf))
    /// because these create recursive constraints that are hard to satisfy.
    /// </summary>
    private static void CollectMethodSignaturesRecursive(
        IReadOnlyList<TypeReference> interfaces,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph,
        Dictionary<string, List<string>> result,
        Dictionary<string, TypeReference> substitutionMap)
    {
        foreach (var ifaceRef in interfaces)
        {
            // Find the interface in the graph
            var ifaceSymbol = FindInterfaceInGraph(ifaceRef, graph);
            if (ifaceSymbol == null)
                continue;

            // Build substitution map for this interface's generic parameters
            var localSubMap = BuildInterfaceSubstitutionMap(ifaceSymbol, ifaceRef, substitutionMap);

            // Get the set of generic parameter names that map to self-referential types
            // These are parameters like TSelf in IBinaryInteger_1<TSelf extends IBinaryInteger_1<TSelf>>
            var selfRefParams = GetSelfReferentialParams(ifaceSymbol);

            // Collect methods from this interface
            var instanceScope = ScopeFactory.ClassInstance(ifaceSymbol);
            foreach (var method in ifaceSymbol.Members.Methods.Where(m => !m.IsStatic))
            {
                // Skip methods that use self-referential parameters in their signature
                // (except for return types - those are fine)
                if (MethodUsesSelfRefParamInParameters(method, selfRefParams))
                    continue;

                var emitName = ctx.Renamer.GetFinalMemberName(method.StableId, instanceScope);

                // Build the signature string with substitution
                var sig = BuildMethodSignatureWithSubstitution(method, ifaceSymbol, emitName, resolver, ctx, localSubMap);

                if (!result.TryGetValue(emitName, out var list))
                {
                    list = new List<string>();
                    result[emitName] = list;
                }

                // Only add if this exact signature isn't already present
                if (!list.Contains(sig))
                {
                    list.Add(sig);
                }
            }

            // Recurse to parent interfaces
            CollectMethodSignaturesRecursive(ifaceSymbol.Interfaces, resolver, ctx, graph, result, localSubMap);
        }
    }

    /// <summary>
    /// Get the set of generic parameter names that are self-referential (like TSelf in IBinaryInteger_1).
    /// These typically have constraints that reference the interface itself.
    /// </summary>
    private static HashSet<string> GetSelfReferentialParams(TypeSymbol ifaceSymbol)
    {
        var result = new HashSet<string>();

        foreach (var gp in ifaceSymbol.GenericParameters)
        {
            // Check if any constraint references the interface itself
            foreach (var constraint in gp.Constraints)
            {
                if (constraint is NamedTypeReference named)
                {
                    // If constraint contains the interface's name, it's self-referential
                    // e.g., TSelf extends IBinaryInteger_1<TSelf>
                    var clrBaseName = ExtractClrBaseName(ifaceSymbol.ClrFullName);
                    var constraintBaseName = ExtractClrBaseName(named.FullName);
                    if (clrBaseName == constraintBaseName)
                    {
                        result.Add(gp.Name);
                        break;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Extract the base name (without namespace and arity suffix) from a CLR full name.
    /// </summary>
    private static string ExtractClrBaseName(string clrFullName)
    {
        // Remove assembly qualification
        var commaIdx = clrFullName.IndexOf(',');
        if (commaIdx >= 0)
            clrFullName = clrFullName.Substring(0, commaIdx);

        // Get the simple name (after last dot)
        var lastDot = clrFullName.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? clrFullName.Substring(lastDot + 1) : clrFullName;

        // Remove arity suffix (`1, `2, etc.)
        var backtick = simpleName.IndexOf('`');
        return backtick >= 0 ? simpleName.Substring(0, backtick) : simpleName;
    }

    /// <summary>
    /// Check if a method uses any of the self-referential parameters in its parameter list.
    /// </summary>
    private static bool MethodUsesSelfRefParamInParameters(MethodSymbol method, HashSet<string> selfRefParams)
    {
        if (selfRefParams.Count == 0)
            return false;

        foreach (var param in method.Parameters)
        {
            if (TypeRefUsesSelfRefParam(param.Type, selfRefParams))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a type reference uses any of the self-referential parameters.
    /// </summary>
    private static bool TypeRefUsesSelfRefParam(TypeReference typeRef, HashSet<string> selfRefParams)
    {
        return typeRef switch
        {
            GenericParameterReference gp => selfRefParams.Contains(gp.Name),
            NamedTypeReference named => named.TypeArguments.Any(arg => TypeRefUsesSelfRefParam(arg, selfRefParams)),
            ArrayTypeReference arr => TypeRefUsesSelfRefParam(arr.ElementType, selfRefParams),
            ByRefTypeReference byref => TypeRefUsesSelfRefParam(byref.ReferencedType, selfRefParams),
            _ => false
        };
    }

    /// <summary>
    /// Find an interface by its type reference in the graph.
    /// </summary>
    private static TypeSymbol? FindInterfaceInGraph(TypeReference ifaceRef, SymbolGraph graph)
    {
        var fullName = ifaceRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => null
        };

        if (fullName == null)
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    /// <summary>
    /// Build substitution map for interface generic parameters.
    /// </summary>
    private static Dictionary<string, TypeReference> BuildInterfaceSubstitutionMap(
        TypeSymbol ifaceSymbol,
        TypeReference ifaceRef,
        Dictionary<string, TypeReference> outerSubMap)
    {
        var result = new Dictionary<string, TypeReference>(outerSubMap);

        if (ifaceRef is not NamedTypeReference named)
            return result;

        if (named.TypeArguments.Count == 0)
            return result;

        if (ifaceSymbol.GenericParameters.Length != named.TypeArguments.Count)
            return result;

        for (int i = 0; i < ifaceSymbol.GenericParameters.Length; i++)
        {
            var param = ifaceSymbol.GenericParameters[i];
            var arg = named.TypeArguments[i];

            // Apply outer substitution to the argument
            var substitutedArg = SubstituteTypeReference(arg, outerSubMap);
            result[param.Name] = substitutedArg;
        }

        return result;
    }

    /// <summary>
    /// Substitute type references using the substitution map.
    /// </summary>
    private static TypeReference SubstituteTypeReference(TypeReference typeRef, Dictionary<string, TypeReference> subMap)
    {
        if (subMap.Count == 0)
            return typeRef;

        return typeRef switch
        {
            GenericParameterReference gp when subMap.TryGetValue(gp.Name, out var sub) => sub,

            NamedTypeReference named when named.TypeArguments.Count > 0 =>
                named with
                {
                    TypeArguments = named.TypeArguments
                        .Select(arg => SubstituteTypeReference(arg, subMap))
                        .ToImmutableArray()
                },

            ArrayTypeReference arr =>
                arr with { ElementType = SubstituteTypeReference(arr.ElementType, subMap) },

            _ => typeRef
        };
    }

    /// <summary>
    /// Build method signature string with generic substitution for inherited methods.
    /// </summary>
    private static string BuildMethodSignatureWithSubstitution(
        MethodSymbol method,
        TypeSymbol declaringType,
        string emitName,
        TypeNameResolver resolver,
        BuildContext ctx,
        Dictionary<string, TypeReference> substitutionMap)
    {
        var sb = new StringBuilder();
        sb.Append(emitName);

        // Generic parameters (if any)
        if (method.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.GenericParameters.Select(gp => gp.Name)));
            sb.Append('>');
        }

        // Parameters
        sb.Append('(');
        var paramParts = new List<string>();
        foreach (var p in method.Parameters)
        {
            var paramType = SubstituteTypeReference(p.Type, substitutionMap);
            var typeStr = TypeRefPrinter.Print(paramType, resolver, ctx);

            // Sanitize parameter name (handle reserved words)
            var paramName = TypeScriptReservedWords.SanitizeParameterName(p.Name);

            // ref/out/in modifiers are ABI semantics tracked in metadata, not TS types
            // Emit plain element type for all parameters

            // Handle optional and rest parameters
            if (p.IsParams)
            {
                paramParts.Add($"...{paramName}: {typeStr}");
            }
            else if (p.HasDefaultValue)
            {
                paramParts.Add($"{paramName}?: {typeStr}");
            }
            else
            {
                paramParts.Add($"{paramName}: {typeStr}");
            }
        }
        sb.Append(string.Join(", ", paramParts));
        sb.Append("): ");

        // Return type
        var returnType = SubstituteTypeReference(method.ReturnType, substitutionMap);
        sb.Append(TypeRefPrinter.Print(returnType, resolver, ctx));

        return sb.ToString();
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
            // PRIMITIVE CONSTRAINT RELAXATION: Widen value semantics constraints
            var printedConstraints = gp.Constraints
                .Select(c =>
                {
                    var printed = TypeRefPrinter.Print(c, resolver, ctx);
                    // Relax IEquatable_1<T>, IComparable_1<T>, IComparable to admit primitives
                    if (AliasEmit.IsValueSemanticsConstraint(c, gp.Name))
                    {
                        return AliasEmit.RelaxConstraintForPrimitives(printed, gp.Name);
                    }
                    return printed;
                })
                .Where(c => c != "any" && c != "unknown")  // Filter out fallback types
                .ToArray();

            if (printedConstraints.Length == 0)
            {
                // All constraints were unrepresentable - use "unknown" (never "any")
                sb.Append("unknown");
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
    /// Uses the declaring type's scope to get the correct final name from Renamer.
    /// </summary>
    private static void EmitInheritedStaticField(StringBuilder sb, FieldSymbol field, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Get final name from Renamer using the declaring type's static scope
        var declaringTypeName = field.StableId.DeclaringClrFullName;
        var scope = new TypeScope
        {
            TypeFullName = declaringTypeName,
            IsStatic = true,
            ScopeKey = $"type:{declaringTypeName}#static"
        };
        var fieldEmitName = ctx.Renamer.GetFinalMemberName(field.StableId, scope);

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
    /// Uses the declaring type's scope to get the correct final name from Renamer.
    /// </summary>
    private static void EmitInheritedStaticProperty(StringBuilder sb, PropertySymbol property, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Get final name from Renamer using the declaring type's static scope
        var declaringTypeName = property.StableId.DeclaringClrFullName;
        var scope = new TypeScope
        {
            TypeFullName = declaringTypeName,
            IsStatic = true,
            ScopeKey = $"type:{declaringTypeName}#static"
        };
        var propEmitName = ctx.Renamer.GetFinalMemberName(property.StableId, scope);

        // NRT: Use EmitProperty helper to handle split get/set accessors for nullable properties
        EmitProperty(sb, property, propEmitName, property.PropertyType, resolver, ctx, overrideType: null, isStatic: true);
    }

    /// <summary>
    /// D1: Emit an inherited static method from a base class (for static hierarchy flattening).
    /// Note: MethodPrinter.PrintWithName already includes "static" keyword for static methods.
    /// Uses the declaring type's scope to get the correct final name from Renamer.
    /// </summary>
    private static void EmitInheritedStaticMethod(StringBuilder sb, MethodSymbol method, TypeSymbol derivedType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Get final name from Renamer using the declaring type's static scope
        var declaringTypeName = method.StableId.DeclaringClrFullName;
        var scope = new TypeScope
        {
            TypeFullName = declaringTypeName,
            IsStatic = true,
            ScopeKey = $"type:{declaringTypeName}#static"
        };
        var emitName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

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

    /// <summary>
    /// Emit a property.
    ///
    /// If the property is nullable and has both a getter and setter, we emit split accessors
    /// to preserve the nullable surface explicitly:
    ///   get prop(): T | undefined;
    ///   set prop(value: T | undefined);
    ///
    /// This matches CLR semantics for nullable properties (T?) and avoids forcing downstream
    /// projects into casts/guards just to assign a nullable value to a nullable property.
    /// </summary>
    private static void EmitProperty(
        StringBuilder sb,
        PropertySymbol property,
        string emitName,
        TypeReference propertyType,
        TypeNameResolver resolver,
        BuildContext ctx,
        string? overrideType = null,
        bool isStatic = false)
    {
        var staticPrefix = isStatic ? "static " : "";

        // Indexers: emit TypeScript index signatures instead of bogus `Item: T` properties.
        // This enables idiomatic `obj[key]` usage for CLR indexers like:
        // - IQueryCollection.this[string] → obj["from"]
        // - List<T>.this[int] → obj[0]
        //
        // TS limitation: index signature keys must be string | number | symbol, and only one is supported.
        if (property.IsIndexer && !isStatic)
        {
            // Only single-parameter indexers are representable in TS.
            if (property.IndexParameters.Length == 1)
            {
                var p0 = property.IndexParameters[0];
                var rawKeyType = TypeRefPrinter.Print(p0.Type, resolver, ctx);

                string? keyType = rawKeyType switch
                {
                    "string" => "string",
                    "System_Internal.String" => "string",
                    // All numeric CLR indexers surface as number in TypeScript.
                    // Tsonic's proof system enforces the actual CLR numeric requirements.
                    "number" => "number",
                    "int" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong" or "ushort" or "nint" or "nuint" => "number",
                    "float" or "double" or "decimal" or "half" => "number",
                    _ => null
                };

                if (keyType != null)
                {
                    var resolvedType = overrideType ?? TypeRefPrinter.Print(propertyType, resolver, ctx);
                    var keyName = string.IsNullOrWhiteSpace(p0.Name) ? "key" : p0.Name;

                    sb.Append("    ");
                    if (!property.HasSetter)
                        sb.Append("readonly ");
                    sb.Append('[');
                    sb.Append(keyName);
                    sb.Append(": ");
                    sb.Append(keyType);
                    sb.Append("]: ");
                    sb.Append(resolvedType);
                    sb.AppendLine(";");
                    return;
                }
            }

            // Unsupported indexer key shape: fall back to emitting `Item` as a normal property.
            // This is not great, but it's safer than lying about a key type that TS can't represent.
        }

        // Check if we need split accessors due to NRT nullability asymmetry
        if (NeedsSplitAccessors(property))
        {
            // Emit split get/set accessors
            var resolvedType = overrideType ?? TypeRefPrinter.Print(propertyType, resolver, ctx);

            // Getter: returns type (nullable when declared)
            sb.Append("    ");
            sb.Append(staticPrefix);
            sb.Append("get ");
            sb.Append(emitName);
            sb.Append("(): ");
            sb.Append(resolvedType);
            sb.AppendLine(";");

            // Setter: takes the same type (nullable when declared)
            sb.Append("    ");
            sb.Append(staticPrefix);
            sb.Append("set ");
            sb.Append(emitName);
            sb.Append("(value: ");
            sb.Append(resolvedType);
            sb.AppendLine(");");
        }
        else
        {
            // Standard property shorthand
            sb.Append("    ");
            sb.Append(staticPrefix);
            if (!property.HasSetter)
                sb.Append("readonly ");
            sb.Append(emitName);
            sb.Append(": ");
            if (overrideType != null)
                sb.Append(overrideType);
            else
                sb.Append(TypeRefPrinter.Print(propertyType, resolver, ctx));
            sb.AppendLine(";");
        }
    }

    /// <summary>
    /// Check if a property needs split get/set accessors due to NRT nullability.
    ///
    /// We use split accessors when:
    /// 1. Property has both getter AND setter
    /// 2. Property type is nullable (NrtState.Nullable)
    ///
    /// This emits:
    ///   get propertyName(): Type | undefined;
    ///   set propertyName(value: Type | undefined);
    /// </summary>
    private static bool NeedsSplitAccessors(PropertySymbol property)
    {
        // Only need split accessors if property has both getter and setter
        if (!property.HasGetter || !property.HasSetter)
            return false;

        // Check if property type is nullable (would render as Type | undefined)
        return property.PropertyType switch
        {
            NamedTypeReference named => named.Nullability == NrtState.Nullable && !named.IsValueType,
            ArrayTypeReference arr => arr.Nullability == NrtState.Nullable,
            // GenericParameterReference: never nullable per NRT simplification
            _ => false
        };
    }
}
