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
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDecoyApplyTests.cs</tests>
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
  /// WHIZ114: Error - an explicit interface <c>Apply</c> sits next to an ordinary <c>Apply</c> with the same parameters,
  /// so the runner, which calls the perspective's own <c>Apply</c>, never runs the explicit one.
  /// </summary>
  /// <docs>operations/diagnostics/whiz114</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDecoyApplyTests.cs:AnExplicitApplyHiddenByAnOrdinaryApply_IsAnErrorAtTheExplicitOneAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDecoyApplyTests.cs:AnExplicitApplyHiddenByAnOrdinaryApplyWithTheSameReturn_IsStillADecoyAsync</tests>
  public static readonly DiagnosticDescriptor HiddenExplicitApply = new(
      id: "WHIZ114",
      title: "Explicit interface Apply never runs",
      messageFormat: "The explicit implementation of {0}.Apply for '{1}' on perspective '{2}' never runs: the runner calls the perspective's own Apply for '{1}', which returns '{3}'. Keep one Apply per event, returning what the interface declares, and remove the explicit implementation.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "When a perspective declares an ordinary Apply and an explicit interface Apply with the same parameters, the generated runner calls the ordinary one, so the explicit one is a decoy that satisfies the compiler and never runs. It often does the opposite of the real handler (keeps the row the real one purges). Return what the interface declares from one Apply; IPerspectiveWithActionsFor with ApplyResult<TModel> covers delete and purge."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ApplyForUnlistedEvent, HiddenExplicitApply];

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
      if (contract.HasModel(model) && PerspectiveContract.IsEvent(eventType) && !contract.Routes(model, eventType)) {
        context.ReportDiagnostic(Diagnostic.Create(
            ApplyForUnlistedEvent,
            _location(apply, perspective, inherited),
            _inheritedFrom(apply, inherited),
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(eventType),
            TypeNameUtilities.MinimallyQualified(model)));
      }
    }
    _reportHiddenExplicitApplies(context, perspective, applies);
  }

  /// <summary>
  /// WHIZ114: every explicit interface <c>Apply</c> of the perspective that an ordinary <c>Apply</c> with the same
  /// parameters hides from the runner, whatever either returns.
  /// </summary>
  private static void _reportHiddenExplicitApplies(
      SymbolAnalysisContext context, INamedTypeSymbol perspective, List<(IMethodSymbol Method, bool Inherited)> applies) {
    var explicitApplies = perspective.GetMembers().OfType<IMethodSymbol>()
      .Where(static m => m.MethodKind == MethodKind.ExplicitInterfaceImplementation
        && m.ExplicitInterfaceImplementations[0].Name == PerspectiveContract.APPLY);
    foreach (var explicitApply in explicitApplies) {
      // A match has the ordinary Apply's two parameters, so the explicit one has an event to name.
      if (applies.Select(static a => a.Method).FirstOrDefault(o => _sameParameters(o, explicitApply)) is { } ordinary) {
        context.ReportDiagnostic(Diagnostic.Create(
            HiddenExplicitApply,
            explicitApply.Locations[0],
            TypeNameUtilities.MinimallyQualified(explicitApply.ExplicitInterfaceImplementations[0].ContainingType),
            TypeNameUtilities.MinimallyQualified(explicitApply.Parameters[1].Type),
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(ordinary.ReturnType)));
      }
    }
  }

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
