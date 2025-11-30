using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Renaming;
using tsbindgen.Shape;

namespace tsbindgen.Normalize.Naming;

/// <summary>
/// Name reservation functions - reserves names through the central Renamer.
/// </summary>
internal static class Reservation
{
    /// <summary>
    /// Reserve member names without mutating symbols (Phase 1).
    /// Returns (Reserved, Skipped) counts.
    /// Skips members that already have rename decisions from earlier passes.
    ///
    /// IMPORTANT: Methods are reserved by overload family (clrName, isStatic) AND erased signature.
    /// Methods with DIFFERENT TypeScript-level signatures can share the same name (valid overloads).
    /// Methods with IDENTICAL TypeScript-level signatures get numeric suffixes (duplicate prevention).
    /// </summary>
    internal static (int Reserved, int Skipped) ReserveMemberNamesOnly(BuildContext ctx, TypeSymbol type)
    {
        // Base scope for member reservations (ReserveMemberName will add #instance/#static)
        var typeScope = ScopeFactory.ClassBase(type);

        int reserved = 0;
        int skipped = 0;

        // Group methods by overload family (clrName, isStatic)
        var methodsByFamily = type.Members.Methods
            .Where(m => m.EmitScope != EmitScope.ViewOnly &&
                       m.EmitScope != EmitScope.Omitted &&
                       m.EmitScope != EmitScope.Unspecified)
            .GroupBy(m => (m.ClrName, m.IsStatic))
            .OrderBy(g => g.Key.ClrName)
            .ThenBy(g => g.Key.IsStatic);

        foreach (var family in methodsByFamily)
        {
            var (clrName, isStatic) = family.Key;
            var methods = family.OrderBy(m => m.StableId.ToString()).ToList();

            // Check for Unspecified EmitScope in the original list (validation)
            var unspecified = type.Members.Methods.Where(m =>
                m.ClrName == clrName && m.IsStatic == isStatic && m.EmitScope == EmitScope.Unspecified).ToList();
            if (unspecified.Any())
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for method with Unspecified EmitScope: {unspecified.First().StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip families where all methods already have decisions
            var methodCheckScope = ScopeFactory.ClassSurface(type, isStatic);
            var methodsNeedingReservation = methods
                .Where(m => !ctx.Renamer.TryGetDecision(m.StableId, methodCheckScope, out _))
                .ToList();

            if (methodsNeedingReservation.Count == 0)
            {
                skipped += methods.Count;
                continue;
            }

            // ALGORITHM (per Alice's review):
            // 1. Pick ONE anchor method (deterministic - smallest stableId)
            // 2. Reserve name EXACTLY ONCE for the anchor
            // 3. All methods with UNIQUE erased signatures → RecordOverloadDecision(familyFinalName)
            // 4. Methods with DUPLICATE erased signatures:
            //    - Exactly ONE method per erased-signature group keeps familyFinalName
            //    - Others need new distinct names (suffix)

            // Step 1: Pick anchor (first by stableId - deterministic)
            var anchorMethod = methodsNeedingReservation.First(); // Already sorted by StableId
            var requested = Shared.ComputeMethodBase(anchorMethod);
            var anchorReason = anchorMethod.Provenance switch
            {
                MemberProvenance.Original => "MethodDeclaration",
                MemberProvenance.FromInterface => "InterfaceMember",
                MemberProvenance.Synthesized => "SynthesizedMember",
                _ => "Unknown"
            };

            // Step 2: Reserve name EXACTLY ONCE for the anchor
            ctx.Renamer.ReserveMemberName(anchorMethod.StableId, requested, typeScope, anchorReason, isStatic, "NameReservation:FamilyAnchor");
            var familyFinalName = ctx.Renamer.GetFinalMemberName(anchorMethod.StableId, methodCheckScope);
            reserved++;

            // Compute anchor's erased signature to know which group it belongs to
            var anchorErasedSig = ComputeErasedParameterSignature(anchorMethod);

            // Step 3-4: Group by erased signature, handle unique vs duplicate
            var byErasedSignature = methodsNeedingReservation
                .GroupBy(m => ComputeErasedParameterSignature(m))
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var erasedGroup in byErasedSignature)
            {
                var groupMethods = erasedGroup.OrderBy(m => m.StableId.ToString()).ToList();
                var erasedSig = erasedGroup.Key;

                // Check if anchor is in this erased-signature group
                var anchorInThisGroup = erasedSig == anchorErasedSig;

                // Track how many methods have been assigned familyFinalName in this group
                // (anchor counts as 1 if it's in this group)
                var familyNameUsedInGroup = anchorInThisGroup;

                foreach (var method in groupMethods)
                {
                    // Skip anchor - already reserved
                    if (method.StableId.Equals(anchorMethod.StableId))
                        continue;

                    var methodRequested = Shared.ComputeMethodBase(method);
                    var reason = method.Provenance switch
                    {
                        MemberProvenance.Original => "MethodDeclaration",
                        MemberProvenance.FromInterface => "InterfaceMember",
                        MemberProvenance.Synthesized => "SynthesizedMember",
                        _ => "Unknown"
                    };

                    if (!familyNameUsedInGroup)
                    {
                        // First method to use familyFinalName in this erased-signature group
                        // → Valid TS overload (different erased signature from others with same name)
                        ctx.Renamer.RecordOverloadDecision(method.StableId, methodRequested, familyFinalName, typeScope, reason, isStatic, "NameReservation:ValidOverload");
                        reserved++;
                        familyNameUsedInGroup = true;

                    }
                    else
                    {
                        // Duplicate erased signature - familyFinalName already used in this group
                        // → Need distinct name (suffix) because TS can't have identical signatures
                        ctx.Renamer.ReserveMemberName(method.StableId, methodRequested, typeScope, reason, isStatic, "NameReservation:DuplicateErasure");
                        reserved++;

                        ctx.Log("NameReservation", $"WARNING: Duplicate TS-erased signature in {type.ClrFullName}.{clrName} - method gets suffix");
                    }
                }
            }

            // Count methods that already had decisions
            skipped += methods.Count - methodsNeedingReservation.Count;
        }

        // Count ViewOnly and Omitted methods as skipped
        skipped += type.Members.Methods.Count(m => m.EmitScope == EmitScope.ViewOnly || m.EmitScope == EmitScope.Omitted);

        foreach (var property in type.Members.Properties.OrderBy(p => p.ClrName))
        {
            // M5: Skip ViewOnly members - they'll be reserved in view-scoped reservation
            if (property.EmitScope == EmitScope.ViewOnly)
            {
                skipped++;
                continue;
            }

            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (property.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for property with Unspecified EmitScope: {property.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (property.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed (e.g., IndexerPlanner)
            // Pass class scope and isStatic to TryGetDecision
            var propertyCheckScope = ScopeFactory.ClassSurface(type, property.IsStatic);
            if (ctx.Renamer.TryGetDecision(property.StableId, propertyCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = property.IsIndexer ? "IndexerProperty" : "PropertyDeclaration";
            var requested = Shared.RequestedBaseForMember(property.ClrName);
            ctx.Renamer.ReserveMemberName(property.StableId, requested, typeScope, reason, property.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var field in type.Members.Fields.OrderBy(f => f.ClrName))
        {
            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (field.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for field with Unspecified EmitScope: {field.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (field.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed
            // Pass class scope and isStatic to TryGetDecision
            var fieldCheckScope = ScopeFactory.ClassSurface(type, field.IsStatic);
            if (ctx.Renamer.TryGetDecision(field.StableId, fieldCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = field.IsConst ? "ConstantField" : "FieldDeclaration";
            var requested = Shared.RequestedBaseForMember(field.ClrName);
            ctx.Renamer.ReserveMemberName(field.StableId, requested, typeScope, reason, field.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ev in type.Members.Events.OrderBy(e => e.ClrName))
        {
            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (ev.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for event with Unspecified EmitScope: {ev.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (ev.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed
            // Pass class scope and isStatic to TryGetDecision
            var eventCheckScope = ScopeFactory.ClassSurface(type, ev.IsStatic);
            if (ctx.Renamer.TryGetDecision(ev.StableId, eventCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var requested = Shared.RequestedBaseForMember(ev.ClrName);
            ctx.Renamer.ReserveMemberName(ev.StableId, requested, typeScope, reason: "EventDeclaration", ev.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ctor in type.Members.Constructors)
        {
            // Check if already renamed
            // Pass class scope and isStatic to TryGetDecision
            var ctorCheckScope = ScopeFactory.ClassSurface(type, ctor.IsStatic);
            if (ctx.Renamer.TryGetDecision(ctor.StableId, ctorCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            ctx.Renamer.ReserveMemberName(ctor.StableId, "constructor", typeScope, "ConstructorDeclaration", ctor.IsStatic, "NameReservation");
            reserved++;
        }

        return (reserved, skipped);
    }

    /// <summary>
    /// M5: Reserve view member names in view-scoped namespace (separate from class surface).
    /// Each view gets its own scope: (TypeStableId, InterfaceStableId, isStatic).
    /// Uses PeekFinalMemberName to detect collisions with actual class-surface names.
    /// Returns (Reserved, Skipped) counts.
    /// </summary>
    internal static (int Reserved, int Skipped) ReserveViewMemberNamesOnly(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol type,
        HashSet<string> classAllNames)
    {
        int reserved = 0;
        int skipped = 0;

        // DEBUG: Log entry for canary types
        var canaryTypes = new[] { "System.Decimal", "System.Array", "System.CharEnumerator", "System.Enum", "System.TypeInfo" };
        if (canaryTypes.Contains(type.ClrFullName))
        {
            ctx.Log("NameReservation", $"[DEBUG] ReserveViewMemberNamesOnly CALLED: type={type.StableId} ExplicitViews.Length={type.ExplicitViews.Length}");
            foreach (var view in type.ExplicitViews)
            {
                ctx.Log("NameReservation", $"  view={view.ViewPropertyName} ViewMembers.Length={view.ViewMembers.Length}");
            }
        }

        // Check if type has any explicit views
        if (type.ExplicitViews.Length == 0)
            return (0, 0);

        // For each view, create a separate scope and reserve names (deterministic order)
        // Sort views by interface StableId for consistent ordering
        var sortedViews = type.ExplicitViews.OrderBy(v => ScopeFactory.GetInterfaceStableId(v.InterfaceReference));

        foreach (var view in sortedViews)
        {
            // Get interface StableId from TypeReference (no graph lookup)
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

            // Create view-specific BASE scope (ReserveMemberName will add #instance/#static)
            var viewScope = ScopeFactory.ViewBase(type, interfaceStableId);

            // Create class surface BASE scope for collision detection (must match scope used in ReserveMemberNamesOnly)
            var classSurfaceScope = ScopeFactory.ClassBase(type);

            // Reserve names for each ViewOnly member (deterministic order)
            // ViewOnly members get separate view-scoped names even if they exist on class surface
            foreach (var viewMember in view.ViewMembers.OrderBy(vm => vm.Kind).ThenBy(vm => vm.StableId.ToString()))
            {
                // DEBUG: Log entry for canaries
                var canaryNames = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "IndexOf", "Current", "TryFormat", "GetMethods", "GetFields" };
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[trace:resv:view] ENTER loop: member={viewMember.ClrName} stableId={viewMember.StableId}");
                }

                // DO NOT skip! ViewOnly members need separate view-scoped decisions
                // even if they also exist on ClassSurface with the same StableId.
                // The collision detection below will apply $view suffix if needed.

                // This is a ViewOnly member - verify by checking EmitScope
                bool isViewOnly = false;
                switch (viewMember.Kind)
                {
                    case ViewPlanner.ViewMemberKind.Method:
                        var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                        isViewOnly = method?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case ViewPlanner.ViewMemberKind.Property:
                        var prop = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                        isViewOnly = prop?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case ViewPlanner.ViewMemberKind.Event:
                        var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                        isViewOnly = evt?.EmitScope == EmitScope.ViewOnly;
                        break;
                }

                if (!isViewOnly)
                {
                    skipped++;
                    continue; // Not a ViewOnly member
                }

                // Find the actual member symbol to get isStatic
                var isStatic = Shared.FindMemberIsStatic(type, viewMember);

                // Compute base requested name using centralized function (same as class surface)
                var requested = Shared.RequestedBaseForMember(viewMember.ClrName);

                // Peek at what the view member would get in its scope
                var peek = ctx.Renamer.PeekFinalMemberName(viewScope, requested, isStatic);

                // DEBUG: Log peek result for canaries
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] TYPE={type.ClrFullName} member={viewMember.ClrName}");
                    ctx.Log("trace:resv:view", $"[DEBUG] requested={requested} peek={peek} isStatic={isStatic}");
                    ctx.Log("trace:resv:view", $"[DEBUG] classAllNames.Count={classAllNames.Count} Contains(peek)={classAllNames.Contains(peek)}");
                }

                // Collision if the view's final name equals ANY class-surface final name (static or instance)
                var collided = classAllNames.Contains(peek);

                // DEBUG: Log collision result
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] collided={collided}");
                }

                string finalRequested;
                string reason;
                string applySuffix;

                if (collided)
                {
                    // Collision with class surface - apply $view suffix
                    finalRequested = requested + "$view";

                    // DEBUG: Log suffix application
                    if (canaryNames.Contains(viewMember.ClrName))
                    {
                        ctx.Log("trace:resv:view", $"[DEBUG] APPLYING $view suffix: finalRequested={finalRequested}");
                    }

                    // If $view is also taken in the view scope, try $view2, $view3, etc.
                    var suffix = 1;
                    while (ctx.Renamer.IsNameTaken(viewScope, finalRequested, isStatic))
                    {
                        suffix++;
                        finalRequested = requested + "$view" + suffix;
                    }

                    reason = "ViewCollision";
                    applySuffix = finalRequested;  // e.g., "toSByte$view"
                }
                else
                {
                    finalRequested = requested;
                    reason = $"ViewMember:{view.ViewPropertyName}";
                    applySuffix = "none";
                }

                // B2) Trace: view reservation with detailed collision info
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view",
                        $"[trace:resv:view] scope=view:{type.StableId}:{interfaceStableId}:{isStatic} member={Plan.Validation.Scopes.FormatMemberStableId(viewMember.StableId)}");
                    ctx.Log("trace:resv:view",
                        $"  requested={requested} peek={peek} classAllHit={collided} applySuffix={applySuffix} final={finalRequested}");
                }

                // Reserve in view scope
                ctx.Renamer.ReserveMemberName(
                    viewMember.StableId,
                    finalRequested,
                    viewScope,
                    reason,
                    isStatic,
                    "NameReservation");

                reserved++;
            }
        }

        return (reserved, skipped);
    }

    /// <summary>
    /// Compute a canonical TypeScript-level parameter signature for grouping overloads.
    /// Methods with the same erased signature would be duplicate overloads in TypeScript.
    /// Uses TypeSignatureCanon for consistency with Names.cs validation.
    /// </summary>
    private static string ComputeErasedParameterSignature(MethodSymbol method)
    {
        return TypeSignatureCanon.ComputeMethodSignature(method.Arity, method.Parameters.Select(p => p.Type));
    }
}
