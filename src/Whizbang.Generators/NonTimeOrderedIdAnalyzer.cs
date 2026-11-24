// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
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
/// <para>
/// The targets are a property or field named <c>MessageId</c> or <c>EventId</c>, a member marked <c>[StreamId]</c>, and
/// the argument of <c>MessageId</c>/<c>EventId</c>'s unchecked constructor (their <c>From</c> already refuses a non-v7
/// Guid at run time). A string literal that is itself a UUIDv7 is not reported. The framework's own minting
/// (<c>TrackedGuid.New()</c>, a strong id's <c>New()</c>, a derived id) is never one of the shapes. Code inside a method
/// or type carrying <c>[SuppressGuidOrderingValidation]</c> is a deliberate opt-out and is not reported.
/// </para>
/// <para>
/// The value is followed one step: written directly, or through a local. A local is followed with a reaching-assignment
/// analysis over the method body's control flow graph (no other method is looked at): the assignments to it, its
/// initializer, and an <c>out</c> argument it is passed to, in source and flow order. Every assignment that can reach
/// the use without being overwritten on the way counts: the last one in a straight line, each branch's last one where
/// branches join, and an assignment later in a loop body round the loop's back edge. If any reaching assignment is one
/// of the shapes (an <c>out</c> argument of <c>Guid.TryParse</c> is one), the use is reported; a flagged assignment
/// that every path overwrites with a clean value before the use is not. The sets only ever grow, so loops converge.
/// </para>
/// <para>
/// Not followed, so quiet: a parameter (its value is the caller's, and following it means following every call site);
/// a field or property copied into the local (its writers are anywhere in the type); a conditional expression, which is
/// not one of the shapes; a write to the local from inside a lambda or local function that captures it, seen from the
/// enclosing method; and an assignment inside a <c>finally</c> block, past it. Inside a lambda or local function, a
/// captured local may hold any value the enclosing method assigns to it, since the function may run at any point; a
/// <c>catch</c> or <c>finally</c> block may start from any assignment in the method.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz037</docs>
/// <tests>tests/Whizbang.Generators.Tests/NonTimeOrderedIdAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class NonTimeOrderedIdAnalyzer : DiagnosticAnalyzer {
  private const string GUID_TYPE = "System.Guid";
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";
  private const string MESSAGE_ID_TYPE = "Whizbang.Core.ValueObjects.MessageId";
  private const string EVENT_ID_TYPE = "Whizbang.Core.ValueObjects.EventId";
  private const string SUPPRESS_ATTRIBUTE = "Whizbang.Core.SuppressGuidOrderingValidationAttribute";

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
    if (_inSuppressedScope(context.ContainingSymbol)) {
      return;
    }
    var source = _unwrap(value);
    var shape = source is ILocalReferenceOperation local
      ? _reachingShape(context.GetControlFlowGraph(), local, [], context.CancellationToken)
      : _shape(source);
    if (shape is not null) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NonTimeOrderedIdFlowsIntoId, value.Syntax.GetLocation(), shape, target));
    }
  }

  // A method, or a type containing it at any depth, that carries [SuppressGuidOrderingValidation]: a deliberate opt-out.
  private static bool _inSuppressedScope(ISymbol symbol) {
    // Every chain ends at a namespace (the global one at least), so ContainingSymbol is never null before it does.
    for (var current = symbol; current is not INamespaceSymbol; current = current.ContainingSymbol!) {
      if (current.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, SUPPRESS_ATTRIBUTE))) {
        return true;
      }
    }
    return false;
  }

  private static IOperation _unwrap(IOperation operation) {
    while (operation is IConversionOperation conversion) {
      operation = conversion.Operand;
    }
    return operation;
  }

  // One assignment to the local being followed, in a control flow graph: where it sits and the shape it writes (null
  // when it is clean).
  private readonly record struct Assignment(int Block, int Position, string? Shape);

  // What an operation writes to the local being followed: the shape, or null for a clean value.
  private readonly record struct Write(string? Shape);

  // What one function's graph holds for the local being followed: its assignments in flow order, the graphs of the
  // lambdas met on the way, and where the use sits (null when it is not in this function).
  private sealed class GraphScan {
    public List<Assignment> Assignments { get; } = [];
    public List<ControlFlowGraph> Nested { get; } = [];
    public (int Block, int Position)? Use { get; set; }
  }

  // The shape of an assignment that reaches the use of a local, searching the graph of the function the use sits in.
  // A function nested in another starts from every assignment of its enclosing functions (outer), because it can run
  // at any point. Null when no reaching assignment is a shape, or when the use is not in this graph or one nested in it.
  private static string? _reachingShape(ControlFlowGraph graph, ILocalReferenceOperation use, List<string?> outer,
      System.Threading.CancellationToken ct) {
    var scan = _scan(graph, use, ct);
    var assignments = scan.Assignments;
    if (scan.Use is not { } at) {
      // Not in this function: search the functions nested in it, each starting from this one's assignments too
      var inherited = new List<string?>(outer);
      inherited.AddRange(assignments.Select(a => a.Shape));
      scan.Nested.AddRange(graph.LocalFunctions.Select(function => graph.GetLocalFunctionControlFlowGraph(function, ct)));
      return scan.Nested.Select(g => _reachingShape(g, use, inherited, ct)).FirstOrDefault(shape => shape is not null);
    }
    // An assignment earlier in the use's own block is the only one reaching it; otherwise what reaches the block
    var before = assignments.FindLastIndex(a => a.Block == at.Block && a.Position < at.Position);
    IEnumerable<int> reaching = before >= 0 ? [before] : _reachingAtEntry(graph.Blocks, assignments, outer.Count)[at.Block];
    return reaching.Select(i => i < 0 ? outer[~i] : assignments[i].Shape).FirstOrDefault(shape => shape is not null);
  }

  private static GraphScan _scan(ControlFlowGraph graph, ILocalReferenceOperation use, System.Threading.CancellationToken ct) {
    var scan = new GraphScan();
    foreach (var block in graph.Blocks) {
      var operations = block.BranchValue is null ? block.Operations : block.Operations.Add(block.BranchValue);
      for (var position = 0; position < operations.Length; position++) {
        foreach (var operation in operations[position].DescendantsAndSelf()) {
          _note(scan, graph, operation, use, block.Ordinal, position, ct);
        }
      }
    }
    return scan;
  }

  private static void _note(GraphScan scan, ControlFlowGraph graph, IOperation operation, ILocalReferenceOperation use,
      int block, int position, System.Threading.CancellationToken ct) {
    if (operation is IFlowAnonymousFunctionOperation function) {
      scan.Nested.Add(graph.GetAnonymousFunctionControlFlowGraph(function, ct));
    } else if (operation is ILocalReferenceOperation reference && reference.Syntax == use.Syntax) {
      scan.Use = (block, position);
    } else if (_written(operation, use.Local) is { } write) {
      scan.Assignments.Add(new Assignment(block, position, write.Shape));
    }
  }

  // The assignments reaching each block's entry, as indexes into the function's assignments (inherited assignment i is
  // ~i). The entry block starts from the inherited ones; a block nothing flows into (the start of a catch or finally
  // block, entered only by an exception or a region exit) starts from those and every assignment in the function. The
  // sets only grow, so the passes stop at the first that changes nothing, and loops converge.
  private static HashSet<int>[] _reachingAtEntry(ImmutableArray<BasicBlock> blocks, List<Assignment> assignments, int inheritedCount) {
    var entry = new HashSet<int>[blocks.Length];
    var exit = new HashSet<int>[blocks.Length];
    var last = new int[blocks.Length];
    foreach (var block in blocks) {
      var b = block.Ordinal;
      entry[b] = _startingSet(block, assignments.Count, inheritedCount);
      exit[b] = [];
      last[b] = assignments.FindLastIndex(a => a.Block == b);
    }
    while (_pass(blocks, entry, exit, last)) {
      // Repeat until a pass changes nothing
    }
    return entry;
  }

  private static HashSet<int> _startingSet(BasicBlock block, int assignmentCount, int inheritedCount) {
    if (!block.Predecessors.IsEmpty) {
      return [];
    }
    var start = new HashSet<int>(Enumerable.Range(0, inheritedCount).Select(i => ~i));
    if (block.Kind != BasicBlockKind.Entry) {
      start.UnionWith(Enumerable.Range(0, assignmentCount));
    }
    return start;
  }

  // One pass in block order; true when any block passed on something it had not before.
  private static bool _pass(ImmutableArray<BasicBlock> blocks, HashSet<int>[] entry, HashSet<int>[] exit, int[] last) {
    var changed = false;
    foreach (var block in blocks) {
      var b = block.Ordinal;
      foreach (var predecessor in block.Predecessors) {
        entry[b].UnionWith(exit[predecessor.Source.Ordinal]);
      }
      // A block that assigns the local passes on only its last assignment; one that does not passes on what reached it
      IEnumerable<int> passed = last[b] >= 0 ? [last[b]] : entry[b];
      if (!exit[b].IsSupersetOf(passed)) {
        exit[b].UnionWith(passed);
        changed = true;
      }
    }
    return changed;
  }

  // What the operation writes to the local, or null when it does not write it: an assignment writes its value; an out
  // argument writes what the method returns through it, a shape only for Guid.TryParse.
  private static Write? _written(IOperation operation, ILocalSymbol local) => operation switch {
    ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
      when SymbolEqualityComparer.Default.Equals(target.Local, local) => new Write(_shape(_unwrap(assignment.Value))),
    IArgumentOperation { Parameter.RefKind: RefKind.Out } argument
      when SymbolEqualityComparer.Default.Equals(_outLocal(argument.Value), local) =>
      new Write(_isGuidMethod((IMethodSymbol)argument.Parameter.ContainingSymbol, "TryParse") ? "Guid.TryParse" : null),
    _ => null
  };

  // The local an out argument writes (`out id` or `out var id`), or null for a discard or a field.
  private static ILocalSymbol? _outLocal(IOperation value) => value switch {
    ILocalReferenceOperation reference => reference.Local,
    IDeclarationExpressionOperation declaration => _outLocal(declaration.Expression),
    _ => null
  };

  // The shape's display text, or null when the value is not one of the flagged shapes.
  private static string? _shape(IOperation operation) => _isV7Literal(operation) ? null : operation switch {
    IObjectCreationOperation { Arguments.Length: > 0 } creation when TypeNameUtilities.DisplayOrNull(creation.Type) == GUID_TYPE =>
      "new Guid(...)",
    IInvocationOperation invocation when _isGuidMethod(invocation.TargetMethod, "Parse") => "Guid.Parse",
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
