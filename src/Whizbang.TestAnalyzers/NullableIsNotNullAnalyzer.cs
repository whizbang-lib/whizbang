// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHZ0004: reports TUnit's <c>IsNotNull()</c> called on a <c>Nullable&lt;T&gt;</c> subject. Not yet implemented.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NullableIsNotNullAnalyzer : DiagnosticAnalyzer {
  /// <summary>WHZ0004.</summary>
  public static readonly DiagnosticDescriptor IsNotNullOnNullable = new(
    id: "WHZ0004",
    title: "IsNotNull() on a Nullable<T> subject cannot fail",
    messageFormat: "'{0}' is a Nullable<{1}>",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [IsNotNullOnNullable];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
