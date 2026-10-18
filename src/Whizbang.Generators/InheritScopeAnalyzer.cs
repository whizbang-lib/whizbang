// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that catches misuse of <c>[InheritScope]</c>. The perspective runner reads the
/// attribute from the perspective's model type, so on a perspective class over another model it is
/// silently ignored (WHIZ170); declared with all-None flags, it gives rows no scope at all (WHIZ171).
/// </summary>
/// <remarks>
/// The attribute on a class that implements no perspective interface is not reported: that class may be
/// the model of a perspective in this compilation or in another assembly, and that is where the attribute
/// belongs.
/// </remarks>
/// <docs>operations/diagnostics/whiz170</docs>
/// <tests>tests/Whizbang.Generators.Tests/InheritScopeAnalyzerTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/InheritScopeAnalyzerCoverageTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class InheritScopeAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Security";
  private const string PERSPECTIVES_NAMESPACE = "Whizbang.Core.Perspectives";

  // The perspective interfaces whose first type argument is the model the runner reads [InheritScope] from.
  private static readonly ImmutableHashSet<string> _perspectiveInterfaceNames =
    ["IPerspectiveFor", "IPerspectiveWithActionsFor", "IGlobalPerspectiveFor"];

  /// <summary>
  /// WHIZ170: Warning. <c>[InheritScope]</c> on a perspective class whose model is another type. The runner
  /// reads the attribute from the model, so here it has no effect.
  /// </summary>
  /// <docs>operations/diagnostics/whiz170</docs>
  /// <tests>tests/Whizbang.Generators.Tests/InheritScopeAnalyzerTests.cs:Analyzer_InheritScopeOnPerspectiveClass_ReportsWhiz170NamingTheModelAsync</tests>
  public static readonly DiagnosticDescriptor InheritScopeOnPerspectiveClass = new(
    id: "WHIZ170",
    title: "[InheritScope] on a perspective class has no effect",
    messageFormat: "'{0}' is a perspective over '{1}', and the perspective runner reads [InheritScope] from the model. Move the attribute to '{1}'.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "The perspective runner decides a row's scope from the [InheritScope] on the perspective's model type. On the perspective class it is ignored, and rows get the default: every scope field at creation, set once."
  );

  /// <summary>
  /// WHIZ171: Info. <c>[InheritScope(OnCreate = None, Always = None)]</c> gives the model's rows no scope at
  /// all, not even a tenant, so tenant- or user-filtered lenses do not return them.
  /// </summary>
  /// <docs>operations/diagnostics/whiz171</docs>
  /// <tests>tests/Whizbang.Generators.Tests/InheritScopeAnalyzerTests.cs:Analyzer_InheritScopeAllNone_ReportsWhiz171SayingRowsGetNoScopeAsync</tests>
  public static readonly DiagnosticDescriptor InheritScopeGivesNoScope = new(
    id: "WHIZ171",
    title: "[InheritScope] with all-None flags gives rows no scope",
    messageFormat: "[InheritScope(OnCreate = None, Always = None)] on '{0}' gives its rows no scope, not even a tenant, so a lens filtered by tenant or user does not return them. Name the fields the rows should carry, or keep this if the rows are meant to be unscoped.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true,
    description: "With both OnCreate and Always set to ScopeFields.None, rows are written with an empty scope. Without the attribute, rows copy every scope field from the message that creates them."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    InheritScopeOnPerspectiveClass,
    InheritScopeGivesNoScope,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    // Registered for SymbolKind.NamedType, so the cast test can only fail if that registration ever
    // widens; it shares the "not a class" exit rather than standing on a line no input reaches.
    if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } symbol) {
      return;
    }

    AttributeData? inheritScopeAttr = null;
    foreach (var attr in symbol.GetAttributes()) {
      var name = TypeNameUtilities.SimpleNameOrNull(attr.AttributeClass);
      if (name == "InheritScopeAttribute" || name == "InheritScope") {
        inheritScopeAttr = attr;
        break;
      }
    }

    if (inheritScopeAttr is null) {
      return;
    }

    var location = LocationUtilities.FirstOrNone(symbol);

    // WHIZ170: on a perspective class whose model is another type, the runner never reads it.
    if (_otherModel(symbol) is { } model) {
      context.ReportDiagnostic(Diagnostic.Create(
        InheritScopeOnPerspectiveClass,
        location,
        symbol.Name,
        model.Name));
    }

    // WHIZ171: both OnCreate and Always set to None. OnCreate defaults to every field (63).
    var onCreate = _readEnumProp(inheritScopeAttr, "OnCreate", defaultValue: 63L);
    var always = _readEnumProp(inheritScopeAttr, "Always", defaultValue: 0L);
    if (onCreate == 0 && always == 0) {
      context.ReportDiagnostic(Diagnostic.Create(
        InheritScopeGivesNoScope,
        location,
        symbol.Name));
    }
  }

  /// <summary>
  /// The model of the first perspective interface the class implements, when that model is not the class
  /// itself; null when the class is no perspective, or is its own model.
  /// </summary>
  private static ITypeSymbol? _otherModel(INamedTypeSymbol symbol) {
    foreach (var iface in symbol.AllInterfaces) {
      if (!_perspectiveInterfaceNames.Contains(iface.Name)
          || TypeNameUtilities.Display(iface.ContainingNamespace) != PERSPECTIVES_NAMESPACE) {
        continue;
      }
      var model = iface.TypeArguments[0];
      return SymbolEqualityComparer.Default.Equals(model, symbol) ? null : model;
    }
    return null;
  }

  private static long _readEnumProp(AttributeData attr, string name, long defaultValue) {
    foreach (var named in attr.NamedArguments) {
      if (named.Key == name && named.Value.Value is { } v) {
        return System.Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
      }
    }
    return defaultValue;
  }
}
