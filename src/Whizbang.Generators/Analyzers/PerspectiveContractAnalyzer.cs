// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Holds a perspective's <c>Apply</c> methods to the contract its interfaces declare (#1936). The interfaces
/// (<c>IPerspectiveFor</c>, <c>IPerspectiveWithActionsFor</c>, <c>IGlobalPerspectiveFor</c>) are the strongly-typed
/// contract the compiler checks, and the runner routes only the events they list; it never routes by a method's name
/// alone. So an <c>Apply</c> the contract does not cover is code that never runs, and the build says so.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveUnlistedApplyTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDuplicateApplyTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyReturnShapeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveMarkerOnlyEventTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveContractAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";

  /// <summary>
  /// WHIZ109: Error - an <c>Apply(TModel, E)</c> handles an event no perspective interface on the class lists for that
  /// model, so the runner never delivers the event to it.
  /// </summary>
  /// <docs>operations/diagnostics/whiz109</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveUnlistedApplyTests.cs:AnApplyForAnEventNoInterfaceLists_IsAnErrorAtTheMethodAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveUnlistedApplyTests.cs:AnInheritedUnlistedApply_IsAnErrorAtTheDerivedPerspectiveAsync</tests>
  public static readonly DiagnosticDescriptor ApplyForUnlistedEvent = new(
      id: "WHIZ109",
      title: "Apply handles an event no perspective interface lists",
      messageFormat: "Apply{0} on perspective '{1}' handles '{2}', which no perspective interface on '{1}' lists for model '{3}', so the runner never routes the event to it. List '{2}' under IPerspectiveFor<{3}, ...> (or IPerspectiveWithActionsFor<{3}, ...> when it returns ApplyResult<{3}>), or remove the method.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "A perspective's interfaces are the strongly-typed contract the compiler checks, and the runner delivers exactly the events they list. An Apply for any other event compiles and never runs, so a handler meant to keep the model current silently does nothing. Implement either interface family as many times as needed to list every event."
  );

  /// <summary>
  /// WHIZ114: Error - two <c>Apply</c> methods handle the same event: an explicit interface implementation and another
  /// <c>Apply</c> with the same parameters. The runner calls <c>Apply</c> through the interface (#1936), so the other
  /// one never runs.
  /// </summary>
  /// <docs>operations/diagnostics/whiz114</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDuplicateApplyTests.cs:TheApplyBesideAnExplicitInterfaceApply_IsAnErrorAtTheOneThatNeverRunsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDuplicateApplyTests.cs:TheApplyBesideAnExplicitInterfaceApply_NeverRuns_WhateverItReturnsAsync</tests>
  public static readonly DiagnosticDescriptor DuplicateApplyForEvent = new(
      id: "WHIZ114",
      title: "Two Apply methods handle the same event",
      messageFormat: "'{0}' has an explicit interface Apply and another Apply for '{1}'. The runner calls the interface's implementation ('{2}'), so '{3}' never runs. Keep one Apply per event.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "When a perspective declares an explicit interface Apply and another Apply with the same parameters, the generated runner calls the interface's implementation, so the other Apply is dead code that looks like the handler. The two often disagree (one keeps the row the other purges). Keep one Apply per event and return what the interface declares; IPerspectiveWithActionsFor with ApplyResult<TModel> covers delete and purge."
  );

  /// <summary>
  /// WHIZ115: Error - an <c>Apply</c> for a routed event returns something the interface that lists the event does not
  /// declare (a bare <c>ModelAction</c>, the <c>(TModel?, ModelAction)</c> tuple, or the other family's return).
  /// </summary>
  /// <docs>operations/diagnostics/whiz115</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyReturnShapeTests.cs:ABareModelActionReturn_ForAnIPerspectiveForEvent_IsAnErrorNamingTheFixAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyReturnShapeTests.cs:ANullableModelReturn_ImplementsTheInterface_AndIsNotReportedAsync</tests>
  public static readonly DiagnosticDescriptor UndeclaredApplyReturn = new(
      id: "WHIZ115",
      title: "Apply returns a shape its interface does not declare",
      messageFormat: "Apply{0} for '{1}' on perspective '{2}' returns '{3}', but the interface that lists '{1}' declares '{4}', so this Apply is not the interface's: it compiles only beside an explicit implementation, which is the Apply the runner calls, so this one never runs. Return '{4}'. To delete or purge, list '{1}' under IPerspectiveWithActionsFor<{5}, ...> and return ApplyResult<{5}>: 'return ModelAction.Purge;' converts implicitly.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "IPerspectiveFor declares TModel Apply(TModel, TEvent) and IPerspectiveWithActionsFor declares ApplyResult<TModel> Apply(TModel, TEvent). ApplyResult<TModel> converts implicitly from the model, from ModelAction and from the (TModel?, ModelAction) tuple, and one class may implement both families any number of times, so every outcome has an interface-declared shape. A TModel? return implements TModel Apply (nullability does not take part in matching), so it is accepted."
  );

  /// <summary>
  /// WHIZ117: Error - an event is listed only through a custom marker interface that extends <c>IPerspectiveBase</c>
  /// without declaring an <c>Apply</c> for it, so it counts as routed while no interface-declared <c>Apply</c> handles
  /// it. Custom marker interfaces may be supported in a future release.
  /// </summary>
  /// <docs>operations/diagnostics/whiz117</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveMarkerOnlyEventTests.cs:AnEventListedOnlyByAMarkerWithNoApply_IsAnErrorAtThePerspectiveAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveMarkerOnlyEventTests.cs:AnEventAMarkerAndADeclaringInterfaceBothList_IsNotReportedAsync</tests>
  public static readonly DiagnosticDescriptor EventWithNoDeclaredApply = new(
      id: "WHIZ117",
      title: "Event is listed only by an interface that declares no Apply for it",
      messageFormat: "Perspective '{0}' lists '{1}' for model '{2}' only through a marker interface that declares no Apply for it, so nothing strongly typed handles the event. List '{1}' under IPerspectiveFor<{2}, ...>, IPerspectiveWithActionsFor<{2}, ...> or IGlobalPerspectiveFor<{2}, TPartitionKey, ...>, which declare its Apply. Custom marker interfaces may be supported in a future release.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "IPerspectiveBase and an interface that extends it without declaring Apply only mark the events a perspective handles. Such an event is counted as routed (it is registered and associated with the perspective), yet no interface-declared Apply exists for it, so nothing the compiler checks handles it. Every event needs an interface that declares its Apply. Support for custom marker interfaces may come in a future release."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [ApplyForUnlistedEvent, DuplicateApplyForEvent, UndeclaredApplyReturn, EventWithNoDeclaredApply];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var perspective = (INamedTypeSymbol)context.Symbol;
    if (perspective.TypeKind != TypeKind.Class || !PerspectiveDiscoveryHelper.IsPerspectiveClass(perspective)) {
      return;
    }

    var contract = PerspectiveContract.Of(perspective);
    List<(IMethodSymbol Method, bool Inherited)> applies = [.. PerspectiveContract.OrdinaryApplies(perspective)];
    foreach (var (apply, inherited) in applies) {
      var model = apply.Parameters[0].Type;
      var eventType = apply.Parameters[1].Type;
      if (!contract.HasModel(model) || !PerspectiveContract.IsEvent(eventType)) {
        continue;
      }
      if (!contract.Routes(model, eventType)) {
        context.ReportDiagnostic(Diagnostic.Create(
            ApplyForUnlistedEvent,
            _location(apply, perspective, inherited),
            _inheritedFrom(apply, inherited),
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(eventType),
            TypeNameUtilities.MinimallyQualified(model)));
      } else if (_undeclaredReturn(contract, apply) is { } declared) {
        context.ReportDiagnostic(Diagnostic.Create(
            UndeclaredApplyReturn,
            _location(apply, perspective, inherited),
            _inheritedFrom(apply, inherited),
            TypeNameUtilities.MinimallyQualified(eventType),
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(apply.ReturnType),
            declared,
            TypeNameUtilities.MinimallyQualified(model)));
      }
    }
    _reportDuplicateApplies(context, perspective, applies);
    _reportEventsWithNoDeclaredApply(context, perspective, contract);
  }

  /// <summary>
  /// WHIZ117: every event the perspective lists only through a marker interface that declares no Apply for it.
  /// </summary>
  private static void _reportEventsWithNoDeclaredApply(
      SymbolAnalysisContext context, INamedTypeSymbol perspective, PerspectiveContract contract) {
    foreach (var (model, eventType) in contract.RoutedEvents.Where(r => contract.DeclaredReturns(r.Model, r.Event).Count == 0)) {
      context.ReportDiagnostic(Diagnostic.Create(
          EventWithNoDeclaredApply,
          perspective.Locations[0],
          perspective.Name,
          TypeNameUtilities.MinimallyQualified(eventType),
          TypeNameUtilities.MinimallyQualified(model)));
    }
  }

  /// <summary>
  /// What the interfaces declare for <paramref name="apply"/>'s event when its return is none of it, or null when it
  /// returns a declared shape or no interface declares an Apply for the event. Nullability does not take part, as it
  /// does not in matching an interface member.
  /// </summary>
  private static string? _undeclaredReturn(PerspectiveContract contract, IMethodSymbol apply) {
    var declared = contract.DeclaredReturns(apply.Parameters[0].Type, apply.Parameters[1].Type);
    return declared.Count == 0 || declared.Any(r => SymbolEqualityComparer.Default.Equals(r, apply.ReturnType))
      ? null
      : string.Join("' or '", declared.Select(TypeNameUtilities.MinimallyQualified).Distinct(System.StringComparer.Ordinal));
  }

  /// <summary>
  /// WHIZ114: every Apply (own or inherited) that has the same parameters as an explicit interface Apply of the
  /// perspective, whatever either returns. The runner calls the interface's implementation, so the other one never
  /// runs; it is reported where it is declared, or at the perspective when it is inherited.
  /// </summary>
  private static void _reportDuplicateApplies(
      SymbolAnalysisContext context, INamedTypeSymbol perspective, List<(IMethodSymbol Method, bool Inherited)> applies) {
    var explicitApplies = perspective.GetMembers().OfType<IMethodSymbol>()
      .Where(static m => m.MethodKind == MethodKind.ExplicitInterfaceImplementation
        && m.ExplicitInterfaceImplementations[0].Name == PerspectiveContract.APPLY);
    foreach (var explicitApply in explicitApplies) {
      // A match has the other Apply's two parameters, so the explicit one has an event to name.
      foreach (var (other, inherited) in applies.Where(a => _sameParameters(a.Method, explicitApply))) {
        context.ReportDiagnostic(Diagnostic.Create(
            DuplicateApplyForEvent,
            _location(other, perspective, inherited),
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(explicitApply.Parameters[1].Type),
            $"{TypeNameUtilities.MinimallyQualified(explicitApply.ExplicitInterfaceImplementations[0].ContainingType)}.Apply",
            _signature(other, inherited)));
      }
    }
  }

  /// <summary>An Apply as a reader finds it: its return and parameter types, and where an inherited one comes from.</summary>
  private static string _signature(IMethodSymbol apply, bool inherited) =>
    $"{TypeNameUtilities.MinimallyQualified(apply.ReturnType)} Apply({TypeNameUtilities.MinimallyQualified(apply.Parameters[0].Type)}, {TypeNameUtilities.MinimallyQualified(apply.Parameters[1].Type)})"
    + _inheritedFrom(apply, inherited);

  private static bool _sameParameters(IMethodSymbol a, IMethodSymbol b) =>
    a.Parameters.Select(static p => p.Type).SequenceEqual(b.Parameters.Select(static p => p.Type), SymbolEqualityComparer.Default);

  /// <summary>
  /// An own method is reported at its name. An inherited one is declared elsewhere, possibly in another assembly, so it
  /// is reported at the perspective that inherits it, whose contract is what it fails.
  /// </summary>
  private static Location _location(IMethodSymbol apply, INamedTypeSymbol perspective, bool inherited) =>
    inherited ? perspective.Locations[0] : apply.Locations[0];

  private static string _inheritedFrom(IMethodSymbol apply, bool inherited) =>
    inherited ? $" (inherited from '{apply.ContainingType.Name}')" : "";
}
