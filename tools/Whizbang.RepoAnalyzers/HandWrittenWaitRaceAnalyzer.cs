// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHIZ612: a wait raced in <c>Task.WhenAny</c> or <c>Task.WaitAny</c> by hand instead of on a <c>WakeSet</c>.
/// </summary>
/// <remarks>
/// <para>
/// The loser of a race is abandoned, and a wait that loses stays registered wherever it waits until it completes or its
/// token is canceled: a channel keeps its queued reader, a semaphore its waiter, a delay its timer and its registration
/// on the token. A loop that races fresh waits every pass on the host's stopping token grows by the losers of every pass
/// for the life of the process (#728, #2613). <c>Whizbang.Core.Async.WakeSet</c> writes that race once, with nothing
/// left behind by construction, and is the one place it may be written: its own source file is the only exemption, by
/// exact repository path. No shape of a hand-written race is accepted, including a per-race token canceled once the race
/// is decided, and there is no suppression.
/// </para>
/// <para>
/// A wait is a task that completes when something happens elsewhere: <c>Task.Delay</c>; a channel's
/// <c>WaitToReadAsync</c>, <c>ReadAsync</c>, <c>WaitToWriteAsync</c> or <c>WriteAsync</c>; <c>SemaphoreSlim.WaitAsync</c>;
/// a <c>TaskCompletionSource</c>'s <c>Task</c>; <c>WakeSignal.WaitAsync</c>; or any method marked
/// <c>[WaitSource]</c> (or overriding one that is). It is followed through conversions, conditionals, null-conditional access, null-coalescing,
/// the task adapters <c>AsTask</c>, <c>WaitAsync</c>, <c>ContinueWith</c> and <c>ConfigureAwait</c>, arrays and
/// collection expressions, and the locals of the same member. A task the rule cannot place (a parameter, a field, a call
/// to any other method) is taken as compute and not reported.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz612</docs>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/HandWrittenWaitRaceAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandWrittenWaitRaceAnalyzer : DiagnosticAnalyzer {
  /// <summary>The one file a wait may be raced in by hand: the primitive that writes the race once.</summary>
  internal const string WAKE_SET_PATH = "src/Whizbang.Core/Async/WakeSet.cs";

  /// <summary>How many locals deep a wait is followed; a local assigned from itself ends the search.</summary>
  private const int MAX_DEPTH = 8;

  private const string WAIT_ASYNC = "WaitAsync";

  /// <summary>WHIZ612: Error. A wait is raced in Task.WhenAny or Task.WaitAny by hand.</summary>
  /// <docs>operations/diagnostics/whiz612</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/HandWrittenWaitRaceAnalyzerTests.cs:ADelayRaced_ReportsTheRaceAndNamesTheWaitAsync</tests>
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ612",
    title: "A wait is raced in Task.WhenAny by hand instead of on a WakeSet",
    messageFormat: "Task.{0} races '{1}', a wait that stays registered wherever it waits when it loses, until it completes or its token is canceled; wait on a WakeSet (Whizbang.Core.Async), which keeps one pending wait per source and leaves nothing behind",
    category: "Reliability",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Repository guard against waits left behind by hand-written races (#728, #2613).");

  private static readonly ImmutableHashSet<string> _channelWaitNames =
    ["WaitToReadAsync", "ReadAsync", "WaitToWriteAsync", "WriteAsync"];

  private static readonly ImmutableHashSet<string> _adapterNames =
    ["AsTask", WAIT_ASYNC, "ContinueWith", "ConfigureAwait"];

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var types = new WaitTypes(start.Compilation);
      start.RegisterOperationAction(operation => _analyzeRace(operation, types), OperationKind.Invocation);
    });
  }

  private static void _analyzeRace(OperationAnalysisContext context, WaitTypes types) {
    var race = (IInvocationOperation)context.Operation;
    if (race.TargetMethod.Name is not ("WhenAny" or "WaitAny")
        || !SymbolEqualityComparer.Default.Equals(race.TargetMethod.ContainingType, types.Task)
        || IsWakeSetFile(race.Syntax.SyntaxTree.FilePath)) {
      return;
    }
    var root = _root(race);
    foreach (var argument in race.Arguments) {
      if (_findWait(argument.Value, root, types, 0) is { } wait) {
        context.ReportDiagnostic(Diagnostic.Create(_rule, race.Syntax.GetLocation(), race.TargetMethod.Name, wait));
        return;
      }
    }
  }

  /// <summary>Whether <paramref name="path"/> is WakeSet's own source file, wherever the repository is checked out.</summary>
  internal static bool IsWakeSetFile(string path) {
    var normalized = path.Replace('\\', '/');
    return normalized == WAKE_SET_PATH || normalized.EndsWith("/" + WAKE_SET_PATH, System.StringComparison.Ordinal);
  }

  /// <summary>The name of the first wait <paramref name="value"/> is, or holds; null when it holds none the rule knows.</summary>
  private static string? _findWait(IOperation value, IOperation root, WaitTypes types, int depth) {
    if (depth > MAX_DEPTH) {
      return null;
    }
    return value switch {
      IConversionOperation conversion => _findWait(conversion.Operand, root, types, depth),
      IConditionalOperation conditional =>
        // A conditional expression always has both branches; only an if statement lacks one.
        _findWait(conditional.WhenTrue, root, types, depth) ?? _findWait(conditional.WhenFalse!, root, types, depth),
      IConditionalAccessOperation access => _findWait(access.WhenNotNull, root, types, depth),
      ICoalesceOperation coalesce =>
        _findWait(coalesce.Value, root, types, depth) ?? _findWait(coalesce.WhenNull, root, types, depth),
      IArrayCreationOperation { Initializer: { } initializer } => _firstWait(initializer.ElementValues, root, types, depth),
      ICollectionExpressionOperation collection => _firstWait(collection.Elements, root, types, depth),
      IPropertyReferenceOperation property => _isCompletionSourceTask(property, types) ? property.Property.Name : null,
      IInvocationOperation call => _invokedWait(call, root, types, depth),
      ILocalReferenceOperation local => _localWait(local.Local, root, types, depth + 1),
      _ => null,
    };
  }

  private static string? _firstWait(ImmutableArray<IOperation> elements, IOperation root, WaitTypes types, int depth) {
    foreach (var element in elements) {
      if (_findWait(element, root, types, depth) is { } wait) {
        return wait;
      }
    }
    return null;
  }

  private static string? _invokedWait(IInvocationOperation call, IOperation root, WaitTypes types, int depth) {
    var method = call.TargetMethod;
    if (_isWaitMethod(method, types)) {
      return method.Name;
    }
    return _adapterNames.Contains(method.Name) && call.Instance is { } instance
      ? _findWait(instance, root, types, depth)
      : null;
  }

  private static bool _isWaitMethod(IMethodSymbol method, WaitTypes types) {
    var owner = method.ContainingType.OriginalDefinition;
    var eq = SymbolEqualityComparer.Default;
    return (method.Name == "Delay" && eq.Equals(owner, types.Task))
      || (method.Name == WAIT_ASYNC && (eq.Equals(owner, types.Semaphore) || eq.Equals(owner, types.WakeSignal)))
      || (_channelWaitNames.Contains(method.Name) && _declaringType(method) is { } declared
          && (eq.Equals(declared, types.Reader) || eq.Equals(declared, types.Writer)))
      || _isMarkedWaitSource(method, types);
  }

  /// <summary>The type that first declared <paramref name="method"/>, past every override.</summary>
  private static INamedTypeSymbol _declaringType(IMethodSymbol method) {
    var declared = method;
    while (declared.OverriddenMethod is { } overridden) {
      declared = overridden;
    }
    return declared.ContainingType.OriginalDefinition;
  }

  private static bool _isMarkedWaitSource(IMethodSymbol method, WaitTypes types) {
    for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod) {
      if (current.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, types.WaitSource))) {
        return true;
      }
    }
    return false;
  }

  private static bool _isCompletionSourceTask(IPropertyReferenceOperation property, WaitTypes types) {
    var owner = property.Property.ContainingType.OriginalDefinition;
    return property.Property.Name == "Task"
      && (SymbolEqualityComparer.Default.Equals(owner, types.CompletionSource)
        || SymbolEqualityComparer.Default.Equals(owner, types.GenericCompletionSource));
  }

  /// <summary>The first wait any value stored in <paramref name="local"/> within this member is, or holds.</summary>
  private static string? _localWait(ILocalSymbol local, IOperation root, WaitTypes types, int depth) {
    foreach (var stored in root.Descendants()) {
      var value = stored switch {
        IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local) =>
          declarator.Initializer?.Value,
        IAssignmentOperation { Target: ILocalReferenceOperation target } assignment
          when SymbolEqualityComparer.Default.Equals(target.Local, local) => assignment.Value,
        _ => null,
      };
      if (value is not null && _findWait(value, root, types, depth) is { } wait) {
        return wait;
      }
    }
    return null;
  }

  private static IOperation _root(IOperation operation) {
    var root = operation;
    while (root.Parent is not null) {
      root = root.Parent;
    }
    return root;
  }

  /// <summary>The types a wait is recognized by, resolved once per compilation; a type the compilation lacks is null.</summary>
  private sealed class WaitTypes(Compilation compilation) {
    public INamedTypeSymbol? Task { get; } = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
    public INamedTypeSymbol? Semaphore { get; } = compilation.GetTypeByMetadataName("System.Threading.SemaphoreSlim");
    public INamedTypeSymbol? CompletionSource { get; } =
      compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource");
    public INamedTypeSymbol? GenericCompletionSource { get; } =
      compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource`1");
    public INamedTypeSymbol? Reader { get; } = compilation.GetTypeByMetadataName("System.Threading.Channels.ChannelReader`1");
    public INamedTypeSymbol? Writer { get; } = compilation.GetTypeByMetadataName("System.Threading.Channels.ChannelWriter`1");
    public INamedTypeSymbol? WakeSignal { get; } = compilation.GetTypeByMetadataName("Whizbang.Core.Async.WakeSignal");
    public INamedTypeSymbol? WaitSource { get; } =
      compilation.GetTypeByMetadataName("Whizbang.Core.Async.WaitSourceAttribute");
  }
}
