// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
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

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
