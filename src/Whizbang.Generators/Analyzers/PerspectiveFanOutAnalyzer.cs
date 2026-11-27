// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a perspective fan-out (<c>IPerspectiveFanOutFor&lt;TModel, TEvent&gt;</c>) the framework cannot run as declared
/// (#2618): a <c>Targets</c> that does not name a key property (WHIZ124), a key that is not a physical field (WHIZ125),
/// has no index (WHIZ126) or cannot hold a stream id (WHIZ127), and a set-based apply that assigns a fan-out key
/// (WHIZ128). The discovery generator leaves every such declaration out, so without these the fan-out would never run.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveFanOutAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerspectiveFanOutAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.FanOutTargetsNamesNoKey,
    DiagnosticDescriptors.FanOutKeyNotPhysicalField,
    DiagnosticDescriptors.FanOutKeyNotIndexed,
    DiagnosticDescriptors.FanOutKeyCannotHoldStreamId,
    DiagnosticDescriptors.FanOutKeyAssigned,
  ];

  /// <summary>A key a fan-out targets rows by: the model and the property.</summary>
  private readonly record struct DeclaredKey(string Model, string Property);

  /// <summary>A set-based apply's assignment of a property, and where and by what it is made.</summary>
  private readonly record struct Assignment(DeclaredKey Key, string ModelName, Location Location, string AssignedBy);

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(static start => {
      var keys = new ConcurrentDictionary<DeclaredKey, string>();
      var assignments = new ConcurrentBag<Assignment>();
      start.RegisterSyntaxNodeAction(c => _analyzeTargets(c, keys), SyntaxKind.PropertyDeclaration);
      start.RegisterSyntaxNodeAction(c => _collectAssignment(c, assignments), SyntaxKind.InvocationExpression);
      start.RegisterCompilationEndAction(end => _reportAssignedKeys(end, keys, assignments));
    });
  }

  private static void _analyzeTargets(SyntaxNodeAnalysisContext context, ConcurrentDictionary<DeclaredKey, string> keys) {
    var declaration = (PropertyDeclarationSyntax)context.Node;
    if (declaration.Identifier.ValueText != FanOutShape.TARGETS) {
      return;
    }
    // A property declaration in its own compilation always has its symbol, and a property always has a containing type.
    var targets = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)!;
    var type = targets.ContainingType;
    var fanOuts = FanOutShape.Interfaces(type)
      .Where(i => SymbolEqualityComparer.Default.Equals(FanOutShape.TargetsOf(type, i), targets))
      .ToList();
    if (fanOuts.Count == 0) {
      return;
    }
    var perspective = TypeNameUtilities.SimpleName(type);
    var model = fanOuts[0].TypeArguments[0];
    var read = FanOutShape.ReadKey(declaration, context.SemanticModel, context.CancellationToken);
    if (read.Key is not { } key) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.FanOutTargetsNamesNoKey, read.Location, perspective, TypeNameUtilities.SimpleName(model)));
      return;
    }
    keys.TryAdd(new DeclaredKey(TypeNameUtilities.FullyQualified(model), key.Name), perspective);
    var modelName = TypeNameUtilities.SimpleName(model);
    if (!FanOutShape.IsPhysicalField(key)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.FanOutKeyNotPhysicalField, read.Location, modelName, key.Name, perspective));
    } else if (!FanOutShape.IsIndexed(key)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.FanOutKeyNotIndexed, read.Location, modelName, key.Name, perspective));
    }
    if (!FanOutShape.CanHoldStreamId(key)) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.FanOutKeyCannotHoldStreamId, read.Location, modelName, key.Name, perspective, TypeNameUtilities.MinimallyQualified(key.Type)));
    }
  }

  private static void _collectAssignment(SyntaxNodeAnalysisContext context, ConcurrentBag<Assignment> assignments) {
    var invocation = (InvocationExpressionSyntax)context.Node;
    if (FanOutShape.Assigned(invocation, context.SemanticModel, context.CancellationToken) is not { } assigned) {
      return;
    }
    var model = assigned.Model;
    var by = context.ContainingSymbol;
    while (by is IMethodSymbol { MethodKind: MethodKind.LambdaMethod } lambda) {
      by = lambda.ContainingSymbol;
    }
    assignments.Add(new Assignment(
      new DeclaredKey(TypeNameUtilities.FullyQualified(model), assigned.Property.Name),
      TypeNameUtilities.SimpleName(model),
      assigned.Location,
      TypeNameUtilities.SimpleName(by!.ContainingType) + "." + by.Name));
  }

  private static void _reportAssignedKeys(
      CompilationAnalysisContext context, ConcurrentDictionary<DeclaredKey, string> keys, ConcurrentBag<Assignment> assignments) {
    foreach (var assignment in assignments) {
      if (keys.TryGetValue(assignment.Key, out var perspective)) {
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.FanOutKeyAssigned, assignment.Location,
          assignment.AssignedBy, assignment.ModelName, assignment.Key.Property, perspective));
      }
    }
  }
}
