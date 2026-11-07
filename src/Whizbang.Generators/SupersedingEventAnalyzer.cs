// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for superseding (latest-wins) event types (#1729): WHIZ135, a type that is both ephemeral and
/// superseding, WHIZ136, a superseding type that names more than one key, and WHIZ138 (#2124), the types of a superseding
/// group that disagree on their key. Both resolve the type the way the catalog
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

  /// <summary>
  /// WHIZ138: Error. The types of one superseding group (#2124) disagree on their key: some declare a
  /// <c>[SupersedingKey]</c> and some do not, or their keys are of different types. The types of a group supersede each
  /// other by key, so a key must mean the same thing for each of them. Checked within one compilation; a key of a
  /// nullable value type agrees with the same type's key, since both are stored as the same text.
  /// </summary>
  /// <docs>operations/diagnostics/whiz138</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:AGroupWhoseTypesDisagreeOnTheKey_ReportsWHIZ138OnEachAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs:AGroupWhoseTypesAgree_OrTypesOutsideAnyGroup_NoWHIZ138Async</tests>
  public static readonly DiagnosticDescriptor GroupKeysDisagree = new(
    id: "WHIZ138",
    title: "Superseding group's types disagree on their key",
    messageFormat: "Superseding event type '{0}' is in group '{1}' with {2}, but other types in the group have {3}. The "
      + "types of a group supersede each other by key: give every one no [SupersedingKey], or a key of the same type.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "In a superseding group the newest event of any type replaces the older events of every type with the "
      + "same key, so the key must mean the same thing for each type of the group.",
    customTags: WellKnownDiagnosticTags.CompilationEnd);

  private const string NO_KEY = "no key";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [EphemeralAndSuperseding, MoreThanOneKey, GroupKeysDisagree];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var members = new ConcurrentBag<GroupMember>();
      start.RegisterSymbolAction(symbolContext => _analyzeType(symbolContext, members), SymbolKind.NamedType);
      start.RegisterCompilationEndAction(end => _reportGroups(end, members));
    });
  }

  private sealed record GroupMember(INamedTypeSymbol Type, string Group, string Key);

  private static void _analyzeType(SymbolAnalysisContext context, ConcurrentBag<GroupMember> members) {
    // Registered for SymbolKind.NamedType only, so the symbol is always a named type.
    var symbol = (INamedTypeSymbol)context.Symbol;
    if (!SupersedingResolver.IsDeclared(symbol)) {
      return;
    }
    if (SupersedingResolver.Group(symbol) is { } group) {
      members.Add(new GroupMember(symbol, group, _keyType(symbol)));
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

  // The key's type as the store sees it: Nullable<T> is T, since both are stored as the same text.
  private static string _keyType(INamedTypeSymbol type) {
    // Two keys are WHIZ136's to report; the first stands for the type here.
    var keys = SupersedingResolver.KeyNames(type);
    if (keys.Count == 0) {
      return NO_KEY;
    }
    var property = _properties(type).First(p => p.Name == keys[0]);
    var keyType = property.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : property.Type;
    return $"a {TypeNameUtilities.Display(keyType.WithNullableAnnotation(NullableAnnotation.NotAnnotated))} key";
  }

  private static IEnumerable<IPropertySymbol> _properties(INamedTypeSymbol type) {
    for (var level = type; level is not null; level = level.BaseType) {
      foreach (var property in level.GetMembers().OfType<IPropertySymbol>()) {
        yield return property;
      }
    }
  }

  private static void _reportGroups(CompilationAnalysisContext context, ConcurrentBag<GroupMember> members) {
    var disagreeing = members
      .GroupBy(m => m.Group, System.StringComparer.Ordinal)
      .Select(g => (Members: g.ToList(), Keys: g.Select(m => m.Key).Distinct(System.StringComparer.Ordinal).ToList()))
      .Where(g => g.Keys.Count > 1);
    foreach (var (group, keys) in disagreeing) {
      foreach (var member in group) {
        var others = string.Join(" or ", keys.Where(k => k != member.Key));
        context.ReportDiagnostic(Diagnostic.Create(
          GroupKeysDisagree, LocationUtilities.FirstOrNone(member.Type), member.Type.Name, member.Group, member.Key, others));
      }
    }
  }
}
