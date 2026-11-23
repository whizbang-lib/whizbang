// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// WHIZ037 (#2528): a Guid built from bytes or a string (<c>new Guid(...)</c>) or parsed (<c>Guid.Parse</c>,
/// <c>Guid.TryParse</c>) flows into a message id, an event id or a <c>[StreamId]</c> member. Interception cannot reach a
/// constructor or a value from the wire, and such a value carries whatever version its source had, so the id may not be
/// the time-ordered UUIDv7 the framework requires of event and message ids (and prefers for stream ids).
/// </summary>
/// <remarks>
/// The flow followed is short on purpose: the value written directly, or through a local whose initializer (or
/// <c>Guid.TryParse</c> out argument) is one of the shapes. The targets are a property or field named <c>MessageId</c> or
/// <c>EventId</c>, a member marked <c>[StreamId]</c>, and the argument of <c>MessageId</c>/<c>EventId</c>'s unchecked
/// constructor (their <c>From</c> already refuses a non-v7 Guid at run time). A string literal that is itself a UUIDv7 is
/// not reported. The framework's own minting (<c>TrackedGuid.New()</c>, a strong id's <c>New()</c>, a derived id) is
/// never one of the shapes.
/// </remarks>
/// <docs>operations/diagnostics/whiz037</docs>
/// <tests>tests/Whizbang.Generators.Tests/NonTimeOrderedIdAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class NonTimeOrderedIdAnalyzer : DiagnosticAnalyzer {
  private const string GUID_TYPE = "System.Guid";
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";
  private const string MESSAGE_ID_TYPE = "Whizbang.Core.ValueObjects.MessageId";
  private const string EVENT_ID_TYPE = "Whizbang.Core.ValueObjects.EventId";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DiagnosticDescriptors.NonTimeOrderedIdFlowsIntoId];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterOperationAction(_analyzeAssignment, OperationKind.SimpleAssignment);
    context.RegisterOperationAction(_analyzeArgument, OperationKind.Argument);
  }

  private static void _analyzeAssignment(OperationAnalysisContext context) {
    var assignment = (ISimpleAssignmentOperation)context.Operation;
    var member = assignment.Target switch {
      IPropertyReferenceOperation property => (ISymbol)property.Property,
      IFieldReferenceOperation field => field.Field,
      _ => null
    };
    if (member is null || !_isIdMember(member)) {
      return;
    }
    _reportIfNonTimeOrdered(context, assignment.Value, member.Name);
  }

  private static void _analyzeArgument(OperationAnalysisContext context) {
    var argument = (IArgumentOperation)context.Operation;
    // The unchecked constructor only: From, Parse and the explicit cast already refuse a non-v7 Guid at run time.
    var target = argument.Parent is IObjectCreationOperation creation ? creation.Type : null;
    var targetName = TypeNameUtilities.DisplayOrNull(target);
    if (targetName is not (MESSAGE_ID_TYPE or EVENT_ID_TYPE)) {
      return;
    }
    _reportIfNonTimeOrdered(context, argument.Value, TypeNameUtilities.SimpleName(target!));
  }

  private static bool _isIdMember(ISymbol member) =>
    member.Name is "MessageId" or "EventId"
    || member.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID_ATTRIBUTE));

  private static void _reportIfNonTimeOrdered(OperationAnalysisContext context, IOperation value, string target) {
    var source = _unwrap(value);
    if (source is ILocalReferenceOperation local) {
      // An operation the compiler hands an operation action always carries its semantic model (only an operation a
      // caller builds through the operation factories has none), so the model is never null here.
      source = _localSource(local.Local, context.Operation.SemanticModel!, context.CancellationToken);
    }
    if (source is not null && _shape(source) is { } shape) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NonTimeOrderedIdFlowsIntoId, value.Syntax.GetLocation(), shape, target));
    }
  }

  private static IOperation _unwrap(IOperation operation) {
    while (operation is IConversionOperation conversion) {
      operation = conversion.Operand;
    }
    return operation;
  }

  // The operation a local's value comes from: its initializer, or the Guid.TryParse call it is the out argument of.
  private static IOperation? _localSource(ILocalSymbol local, SemanticModel model, System.Threading.CancellationToken ct) {
    foreach (var reference in local.DeclaringSyntaxReferences) {
      var syntax = reference.GetSyntax(ct);
      if (syntax is VariableDeclaratorSyntax { Initializer.Value: { } initializer }) {
        // The compiler binds every initializer of a method it analyzes, so GetOperation answers with an operation (an
        // invalid one for code in error), never null; _unwrap would pass a null through and the caller's null check
        // stays the guard all the same.
        return _unwrap(model.GetOperation(initializer, ct)!);
      }
      if (syntax.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { } invocation
          && model.GetOperation(invocation, ct) is IInvocationOperation call && _isGuidMethod(call.TargetMethod, "TryParse")) {
        return call;
      }
    }
    return null;
  }

  // The shape's display text, or null when the value is not one of the flagged shapes.
  private static string? _shape(IOperation operation) => _isV7Literal(operation) ? null : operation switch {
    IObjectCreationOperation { Arguments.Length: > 0 } creation when TypeNameUtilities.DisplayOrNull(creation.Type) == GUID_TYPE =>
      "new Guid(...)",
    IInvocationOperation invocation when _isGuidMethod(invocation.TargetMethod, "Parse") => "Guid.Parse",
    IInvocationOperation invocation when _isGuidMethod(invocation.TargetMethod, "TryParse") => "Guid.TryParse",
    _ => null
  };

  // A constructor or Parse whose first argument is a string constant that is itself a UUIDv7.
  private static bool _isV7Literal(IOperation operation) {
    var arguments = operation switch {
      IObjectCreationOperation creation => creation.Arguments,
      IInvocationOperation invocation => invocation.Arguments,
      _ => default
    };
    if (arguments.IsDefaultOrEmpty) {
      return false;
    }
    var first = _unwrap(arguments[0].Value);
    if (first.ConstantValue is { HasValue: true, Value: string text }) {
      return System.Guid.TryParse(text, out var parsed) && parsed.ToString("D")[14] == '7';
    }
    // An interpolated string whose leading text fixes the version nibble: "0199b000-0000-7000-8000-{n:D12}".
    return first is IInterpolatedStringOperation { Parts: [IInterpolatedStringTextOperation { Text.ConstantValue.Value: string head }, ..] }
      && head.Length > 14 && head[8] == '-' && head[13] == '-' && head[14] == '7';
  }

  private static bool _isGuidMethod(IMethodSymbol method, string name) =>
    method.Name == name && TypeNameUtilities.DisplayOrNull(method.ContainingType) == GUID_TYPE;
}
