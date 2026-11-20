// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHIZ609: reports a class that has a TUnit <c>[After(Test)]</c> hook and is also <see cref="IDisposable"/> or
/// <see cref="IAsyncDisposable"/>, a fixture whose teardown runs twice.
/// </summary>
/// <tests>tests/Whizbang.TestAnalyzers.Tests/SingleTeardownAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingleTeardownAnalyzer : DiagnosticAnalyzer {
  /// <summary>
  /// WHIZ609: Error. A fixture with an <c>[After(Test)]</c> hook is also disposable, so its teardown runs twice.
  /// </summary>
  /// <docs>operations/diagnostics/whiz609</docs>
  /// <tests>tests/Whizbang.TestAnalyzers.Tests/SingleTeardownAnalyzerTests.cs:AsyncDisposableFixture_WithATestTeardownHook_ReportsWhiz609Async</tests>
  public static readonly DiagnosticDescriptor TeardownRunsTwice = new(
    id: "WHIZ609",
    title: "A fixture with an [After(Test)] hook must not also be disposable",
    messageFormat: "'{0}' has an [After(Test)] hook and is {1}, so TUnit tears it down twice",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TeardownRunsTwice];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
