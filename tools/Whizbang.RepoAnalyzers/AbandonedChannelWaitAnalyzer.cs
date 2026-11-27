// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHIZ611: a channel wait raced in <c>Task.WhenAny</c> whose token nothing cancels once the race is decided.
/// </summary>
/// <remarks>
/// <para>
/// A wait that loses a <c>Task.WhenAny</c> race is abandoned, and a channel keeps an abandoned wait queued until an
/// item is written or the wait's token is canceled. A loop that asks for a fresh wait on a long-lived token every pass,
/// and races it against a timeout, queues one more waiter per idle pass for as long as the token lives; on the host's
/// stopping token that is the life of the process (#2613). Disposing a linked source does not cancel its token, so
/// a <c>using</c> alone does not release the wait either.
/// </para>
/// <para>
/// The rule follows each <c>WaitToReadAsync</c>, <c>ReadAsync</c>, <c>WaitToWriteAsync</c> and <c>WriteAsync</c> on a
/// <c>ChannelReader&lt;T&gt;</c> or <c>ChannelWriter&lt;T&gt;</c> (or a type derived from one) that is turned into a
/// task with <c>AsTask()</c>, and reports it when that task reaches <c>Task.WhenAny</c>, directly or through a local of
/// the same method, unless the wait's token is the <c>Token</c> of a local <c>CancellationTokenSource</c> that the same
/// method cancels. The two accepted shapes are therefore: a per-race source canceled once <c>WhenAny</c> returns, or a
/// single pending wait kept across passes (<c>Whizbang.Core.Async.PendingChannelWait&lt;T&gt;</c>) and asked for again
/// only once it has completed, which the rule does not see because no fresh wait is made per race.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/AbandonedChannelWaitAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AbandonedChannelWaitAnalyzer : DiagnosticAnalyzer {
  /// <summary>WHIZ611: Error. A channel wait raced in <c>Task.WhenAny</c> is never released when it loses.</summary>
  /// <docs>operations/diagnostics/whiz611</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/AbandonedChannelWaitAnalyzerTests.cs:WaitRacedDirectly_OnALongLivedToken_ReportsAsync</tests>
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ611",
    title: "A channel wait raced in Task.WhenAny is never released",
    messageFormat: "'{0}' is raced in Task.WhenAny and nothing cancels it when another task wins, so the channel keeps the waiter until its token is canceled; keep one pending wait across passes (PendingChannelWait), or pass the Token of a local CancellationTokenSource and cancel it once WhenAny returns",
    category: "Reliability",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Repository guard against abandoned channel waiters (#2613).");

  private static readonly ImmutableHashSet<string> _waitNames =
    ["WaitToReadAsync", "ReadAsync", "WaitToWriteAsync", "WriteAsync"];

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var reader = start.Compilation.GetTypeByMetadataName("System.Threading.Channels.ChannelReader`1");
      if (reader is null) {
        return;
      }

      // The channels assembly declares the writer and is built on the other two, so all three are present with it.
      var writer = start.Compilation.GetTypeByMetadataName("System.Threading.Channels.ChannelWriter`1")!;
      var task = start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task")!;
      var source = start.Compilation.GetTypeByMetadataName("System.Threading.CancellationTokenSource")!;
      start.RegisterOperationAction(
        operation => _analyzeAsTask(operation, reader, writer, task, source), OperationKind.Invocation);
    });
  }

  private static void _analyzeAsTask(
      OperationAnalysisContext context,
      INamedTypeSymbol reader,
      INamedTypeSymbol writer,
      INamedTypeSymbol task,
      INamedTypeSymbol source) {
    var asTask = (IInvocationOperation)context.Operation;
    if (asTask.TargetMethod.Name != "AsTask"
        || asTask.Instance is not IInvocationOperation wait
        || !_isChannelWait(wait.TargetMethod, reader, writer)) {
      return;
    }

    var root = _root(asTask);
    if (!_isRaced(asTask, root, task) || _tokenIsCanceledHere(wait, root, source)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(_rule, wait.Syntax.GetLocation(), wait.TargetMethod.Name));
  }

  private static bool _isChannelWait(IMethodSymbol method, INamedTypeSymbol reader, INamedTypeSymbol writer) {
    if (!_waitNames.Contains(method.Name)) {
      return false;
    }
    var declared = method;
    while (declared.OverriddenMethod is { } overridden) {
      declared = overridden;
    }
    var owner = declared.ContainingType.OriginalDefinition;
    return SymbolEqualityComparer.Default.Equals(owner, reader)
      || SymbolEqualityComparer.Default.Equals(owner, writer);
  }

  /// <summary>
  /// The task reaches <c>Task.WhenAny</c>: as an argument of it, or through a local of this method that is one.
  /// </summary>
  private static bool _isRaced(IOperation asTask, IOperation root, INamedTypeSymbol task) {
    if (_isWhenAnyArgument(asTask, task)) {
      return true;
    }
    var local = _assignedLocal(asTask);
    return local is not null && root.Descendants()
      .OfType<ILocalReferenceOperation>()
      .Any(reference => SymbolEqualityComparer.Default.Equals(reference.Local, local)
        && _isWhenAnyArgument(reference, task));
  }

  /// <summary>The nearest call that takes the value is <c>Task.WhenAny</c>.</summary>
  private static bool _isWhenAnyArgument(IOperation value, INamedTypeSymbol task) {
    for (var parent = value.Parent; parent is not null; parent = parent.Parent) {
      if (parent is IInvocationOperation invocation) {
        return invocation.TargetMethod.Name == "WhenAny"
          && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, task);
      }
    }
    return false;
  }

  /// <summary>The local the task is stored in, through a conversion or a conditional, or null.</summary>
  private static ILocalSymbol? _assignedLocal(IOperation asTask) {
    var value = asTask;
    while (value.Parent is IConversionOperation or IConditionalOperation) {
      value = value.Parent;
    }
    return value.Parent switch {
      IVariableInitializerOperation initializer => ((IVariableDeclaratorOperation)initializer.Parent!).Symbol,
      ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } => target.Local,
      _ => null,
    };
  }

  /// <summary>
  /// The wait's token is the <c>Token</c> of a local <c>CancellationTokenSource</c> that this method cancels.
  /// </summary>
  private static bool _tokenIsCanceledHere(IInvocationOperation wait, IOperation root, INamedTypeSymbol source) {
    var token = wait.Arguments.Single(a => a.Parameter!.Type.Name == nameof(System.Threading.CancellationToken)).Value;
    // The only CancellationToken property of a CancellationTokenSource is its Token.
    if (token is not IPropertyReferenceOperation { Instance: ILocalReferenceOperation owner } property
        || !SymbolEqualityComparer.Default.Equals(property.Property.ContainingType, source)) {
      return false;
    }
    return root.Descendants()
      .OfType<IInvocationOperation>()
      .Any(call => call.TargetMethod.Name is "Cancel" or "CancelAsync"
        && call.Instance is ILocalReferenceOperation canceled
        && SymbolEqualityComparer.Default.Equals(canceled.Local, owner.Local));
  }

  private static IOperation _root(IOperation operation) {
    var root = operation;
    while (root.Parent is not null) {
      root = root.Parent;
    }
    return root;
  }
}
