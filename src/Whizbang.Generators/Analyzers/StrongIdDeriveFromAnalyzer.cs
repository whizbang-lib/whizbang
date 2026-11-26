// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports WHIZ118 (error) at a call to a strong id's <c>DeriveFrom(handling, purpose)</c> when the id has no pinned id
/// (#2604). The generator gives every strong id <c>DeriveFrom</c>; a pinned id also gets the <c>PinnedId</c> constant the
/// derivation reads, so an id without it has nothing that survives a rename to derive from, and its <c>DeriveFrom</c>
/// only throws.
/// </summary>
/// <docs>operations/diagnostics/whiz118</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StrongIdDeriveFromAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class StrongIdDeriveFromAnalyzer : DiagnosticAnalyzer {
  private const string DERIVE_FROM = "DeriveFrom";
  private const string PINNED_ID_CONSTANT = "PinnedId";
  private const string HANDLING = "global::Whizbang.Core.Messaging.Handling";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DiagnosticDescriptors.StrongIdDeriveFromNeedsPinnedId];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterOperationAction(_analyzeInvocation, OperationKind.Invocation);
  }

  private static void _analyzeInvocation(OperationAnalysisContext context) {
    var method = ((IInvocationOperation)context.Operation).TargetMethod;
    if (method.Name != DERIVE_FROM || !method.IsStatic || method.Parameters.Length != 2 ||
        !TypeNameUtilities.IsFullyQualifiedNamed(method.Parameters[0].Type, HANDLING)) {
      return;
    }
    var idType = method.ContainingType;
    if (!TypeNameHelper.ImplementsInterface(idType, StandardInterfaceNames.I_WHIZBANG_ID)) {
      return;
    }
    if (idType.GetMembers(PINNED_ID_CONSTANT).Any(member => member is IFieldSymbol { IsConst: true })) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.StrongIdDeriveFromNeedsPinnedId,
        context.Operation.Syntax.GetLocation(),
        TypeNameUtilities.SimpleName(idType)));
  }
}
