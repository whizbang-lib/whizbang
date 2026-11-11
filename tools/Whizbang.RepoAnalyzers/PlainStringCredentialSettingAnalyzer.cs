// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHIZ606: a settings property whose name says it holds a credential (a connection string, password, secret, key or
/// token) is a plain <see cref="string"/>. Declared as <c>SensitiveString</c> instead, the value redacts itself in
/// <c>ToString</c>, logs, serialization and the startup settings report, so it cannot leak by being written out.
/// </summary>
/// <remarks>
/// A settings property is a public instance property with a public setter (or init) on a type whose name ends in
/// <c>Options</c> or <c>Settings</c>: the shape configuration binds. A property whose name only refers to a credential
/// (<c>ConnectionStringName</c>, <c>KeyId</c>) does not end in a credential word and is not reported. A property that
/// names where a credential is found rather than holding it suppresses the rule with a justification.
/// </remarks>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/PlainStringCredentialSettingAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PlainStringCredentialSettingAnalyzer : DiagnosticAnalyzer {
  /// <summary>WHIZ606: Error. A credential-named settings property is a plain string.</summary>
  /// <docs>operations/diagnostics/whiz606</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/PlainStringCredentialSettingAnalyzerTests.cs:ACredentialNamedStringSetting_ReportsAsync</tests>
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ606",
    title: "A credential-named settings property is a plain string",
    messageFormat: "'{0}.{1}' holds a credential in a plain string; declare it as SensitiveString so logs, serialization and the startup settings report redact it",
    category: "Security",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Settings that hold credentials are SensitiveString, so writing one out writes a redaction.");

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  // A property ending in one of these words holds the credential itself; one ending in Name, Id or Path refers to it.
  private static readonly string[] _credentialWords =
    ["ConnectionString", "Password", "Secret", "Token", "Key", "Credential", "Credentials"];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeProperty, SymbolKind.Property);
  }

  private static void _analyzeProperty(SymbolAnalysisContext context) {
    var property = (IPropertySymbol)context.Symbol;
    if (property.Type.SpecialType != SpecialType.System_String
        || property.IsStatic
        || property.DeclaredAccessibility != Accessibility.Public
        || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
        || !_isSettingsType(property.ContainingType.Name)
        || !_isCredentialName(property.Name)) {
      return;
    }
    context.ReportDiagnostic(Diagnostic.Create(_rule, property.Locations[0], property.ContainingType.Name, property.Name));
  }

  private static bool _isSettingsType(string name) =>
    name.EndsWith("Options", StringComparison.Ordinal) || name.EndsWith("Settings", StringComparison.Ordinal);

  private static bool _isCredentialName(string name) =>
    _credentialWords.Any(word => name.EndsWith(word, StringComparison.OrdinalIgnoreCase));
}
