// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for superseding (latest-wins) event types (#1729): WHIZ135, a type that is both ephemeral and
/// superseding, and WHIZ136, a superseding type that names more than one key. Both resolve the type the way the catalog
/// generator does (<see cref="SupersedingResolver"/>, <see cref="EphemeralResolver"/>), so what the analyzer reports is
/// exactly what the generator refuses to register.
/// </summary>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SupersedingEventAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Superseding";

  /// <summary>
  /// WHIZ135: Error. An event type is both ephemeral and superseding. An ephemeral event is already reaped once
  /// consumed, by the ephemeral reaper, and is never part of the durable log a newer event could supersede in. Error: the
  /// two declarations contradict each other, and the generator registers the type as ephemeral only.
  /// </summary>
  /// <docs>operations/diagnostics/whiz135</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:EphemeralAndSuperseding_ReportsWHIZ135ErrorAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:EphemeralByAProfile_AndSupersedingByABase_ReportsWHIZ135Async</tests>
  public static readonly DiagnosticDescriptor EphemeralAndSuperseding = new(
    id: "WHIZ135",
    title: "Event type is both ephemeral and superseding",
    messageFormat: "Event type '{0}' is both [Ephemeral] and [Superseding]. An ephemeral event is reaped once consumed and "
      + "never kept in the durable log, so nothing can supersede it: remove one of the two.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Superseding is for durable full-state events: the newest of each key is kept and the older ones are "
      + "reaped once consumed. Ephemeral events are reaped by their own reaper and are not superseded.");

  /// <summary>
  /// WHIZ136: Error. A superseding event type marks more than one property <c>[SupersedingKey]</c>, on itself or its base
  /// types. Its key would be ambiguous, so the generator does not register the type as superseding.
  /// </summary>
  /// <docs>operations/diagnostics/whiz136</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:TwoKeys_ReportsWHIZ136Error_NamingBothAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:AKeyOnTheBase_AndAnotherOnTheDerivedType_ReportsWHIZ136Async</tests>
  public static readonly DiagnosticDescriptor MoreThanOneKey = new(
    id: "WHIZ136",
    title: "Superseding event type has more than one key",
    messageFormat: "Superseding event type '{0}' marks more than one property [SupersedingKey] ({1}). A superseding event "
      + "has one key: keep the attribute on one property, or combine the parts into one key property.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "The key decides which older event a newer one replaces. With two keys the choice is ambiguous, so the "
      + "type is not registered as superseding and every event of it is kept.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [EphemeralAndSuperseding, MoreThanOneKey];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    // Registered for SymbolKind.NamedType only, so the symbol is always a named type.
    var symbol = (INamedTypeSymbol)context.Symbol;
    if (!SupersedingResolver.IsDeclared(symbol)) {
      return;
    }
    if (EphemeralResolver.IsEphemeral(symbol)) {
      context.ReportDiagnostic(Diagnostic.Create(EphemeralAndSuperseding, LocationUtilities.FirstOrNone(symbol), symbol.Name));
    }
    var keys = SupersedingResolver.KeyNames(symbol);
    if (keys.Count > 1) {
      context.ReportDiagnostic(Diagnostic.Create(
        MoreThanOneKey, LocationUtilities.FirstOrNone(symbol), symbol.Name, string.Join(", ", keys)));
    }
  }
}
