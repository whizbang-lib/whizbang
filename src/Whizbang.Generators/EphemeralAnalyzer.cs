// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for the ephemeral virality rules (WHIZ130, WHIZ131, WHIZ132 and WHIZ134). Ephemeral is <em>viral</em>: a
/// perspective fed by an ephemeral event is authoritative state, not a rebuildable cache. Nobody in the
/// industry <em>enforces</em> that; Whizbang does, safe by construction at build time. Each diagnostic
/// is an ordinary <see cref="DiagnosticAnalyzer"/> diagnostic, so it surfaces in both the IDE (squiggle)
/// and the compiler build (<c>dotnet build</c>).
/// </summary>
/// <remarks>
/// Compile-time guards: <c>WHIZ130</c> (mixed-mode Apply) and <c>WHIZ134</c> (ambiguous composition),
/// both statically resolvable from type symbols, and <c>WHIZ131</c> (a rebuild or rewind of an ephemeral-only
/// perspective), resolvable when the call site names the perspective by a compile-time constant or by
/// <c>typeof</c>, and <c>WHIZ132</c> (a receptor that turns an ephemeral message into a Sourced event), resolvable
/// from the receptor's declared response type. A rebuild or rewind that names a perspective by a runtime string, a stream given as a runtime
/// id, and re-emit dataflow are not statically resolvable, so they are enforced by the runtime guard rather
/// than by a diagnostic here.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class EphemeralAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Ephemeral";

  // IPerspectiveBase<TModel, TEvent1, ...> is the unified marker extended by IPerspectiveFor and
  // IPerspectiveWithActionsFor; the events are the type arguments from index 1 on.
  private const string PERSPECTIVE_BASE = "Whizbang.Core.Perspectives.IPerspectiveBase";
  private const string DANGEROUS_MIX_OPT_IN = "DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute";

  // WHIZ131: the three call sites that ask to replay a perspective, and the two ways a name is statically a type.
  private const string REBUILD_COMMAND = "Whizbang.Core.Commands.System.RebuildPerspectiveCommand";
  private const string PERSPECTIVE_NAMES = "PerspectiveNames";
  private const string REBUILDER = "Whizbang.Core.Perspectives.IPerspectiveRebuilder";
  private const string REBUILD_METHOD_PREFIX = "Rebuild";
  private const string RUNNER = "Whizbang.Core.Perspectives.IPerspectiveRunner";
  private const string REWIND_METHOD = "RewindAndRunAsync";
  private const string PERSPECTIVE_NAME_PARAMETER = "perspectiveName";
  private const string REBUILD = "rebuild";
  private const string REWIND = "rewind";
  private const string FULL_NAME = "FullName";
  private const string TYPE_NAME_FORMATTER = "Whizbang.Core.TypeNameFormatter";
  private const string FORMAT_CLR_TYPE_NAME = "FormatClrTypeName";

  // WHIZ132: the receptors with a response, what a response unwraps to, and the opt-in.
  private const string RECEPTOR = "Whizbang.Core.IReceptor<TMessage, TResponse>";
  private const string SYNC_RECEPTOR = "Whizbang.Core.ISyncReceptor<TMessage, TResponse>";
  private const string EVENT = "Whizbang.Core.IEvent";
  private const string ROUTED = "Whizbang.Core.Dispatch.Routed<T>";
  private const string LAUNDERING_OPT_IN = "DangerouslyAllowSourcedEventsFromEphemeralInputAttribute";

  /// <summary>
  /// WHIZ130: Warning — a perspective whose <c>Apply</c> methods span both modes (a viral ephemeral
  /// event AND a normal Sourced event). It cannot be both authoritative state and a rebuildable cache.
  /// A Warning (not error): the right first signal for an opt-in feature, backstopped by the runtime
  /// guard; escalate via <c>.editorconfig</c> / <c>TreatWarningsAsErrors</c>.
  /// </summary>
  /// <docs>operations/diagnostics/whiz130</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralAnalyzerTests.cs:PerspectiveMixesEphemeralAndSourcedApply_ReportsWHIZ130WarningAsync</tests>
  public static readonly DiagnosticDescriptor MixedModePerspective = new(
    id: "WHIZ130",
    title: "Perspective mixes ephemeral and Sourced Apply methods",
    messageFormat: "Perspective '{0}' applies both ephemeral event(s) ({1}) and Sourced event(s) ({2}). "
      + "A perspective cannot be both authoritative state (ephemeral) and a rebuildable cache (Sourced): "
      + "split it, or make the stream homogeneous.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "Ephemeral is viral: a perspective fed by an ephemeral event is authoritative state that "
      + "is never rebuilt from a log. Mixing a Sourced event into the same perspective is a contradiction.");

  /// <summary>
  /// WHIZ131: Error. A call site asks to rebuild or rewind a perspective whose every applied event is ephemeral:
  /// it constructs <c>RebuildPerspectiveCommand</c> naming it, calls an <c>IPerspectiveRebuilder</c> rebuild for it,
  /// or calls <c>IPerspectiveRunner.RewindAndRunAsync</c> against it. Such a perspective is authoritative state:
  /// its events self-destruct and their bodies are reaped, so the replay can never do anything. Error (not
  /// warning): unlike WHIZ130 there is nothing to accept, the call is dead on arrival. A perspective that mixes
  /// modes is not reported here; WHIZ130 covers it.
  /// </summary>
  /// <docs>operations/diagnostics/whiz131</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralRebuildAnalyzerTests.cs:RebuildCommand_NamingAnEphemeralOnlyPerspective_ReportsWHIZ131ErrorAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralRebuildAnalyzerTests.cs:RebuilderCalls_NamingAnEphemeralOnlyPerspective_ReportWHIZ131Async</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralRebuildAnalyzerTests.cs:RewindRequest_AgainstAnEphemeralOnlyPerspective_ReportsWHIZ131Async</tests>
  public static readonly DiagnosticDescriptor ReplayOfEphemeralOnlyPerspective = new(
    id: "WHIZ131",
    title: "Rebuild or rewind of an ephemeral-only perspective",
    messageFormat: "This {0} of perspective '{1}' can never replay anything: every event it applies is ephemeral "
      + "({2}), and ephemeral events self-destruct and have their bodies reaped. The perspective is authoritative "
      + "state, not a rebuildable cache: remove the {0}.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "A perspective fed only by ephemeral events is never rebuilt from a log, so a rebuild command, a "
      + "rebuilder call or a rewind request that names it is refused at run time and does nothing.");

  /// <summary>
  /// WHIZ132: Warning. A receptor whose input message is ephemeral returns or cascades a Sourced event, so what the
  /// ephemeral message carried becomes a durable fact in the event store (ephemeral-to-Sourced laundering). The rule is
  /// the simple one: any statically known Sourced event in the response, whether or not it copies the input. A
  /// Warning, matching WHIZ130; <c>[DangerouslyAllowSourcedEventsFromEphemeralInput]</c> on the receptor accepts a
  /// deliberate case. There is no runtime backstop.
  /// </summary>
  /// <docs>operations/diagnostics/whiz132</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralLaunderingAnalyzerTests.cs:EphemeralInput_ReturningASourcedEvent_ReportsWHIZ132WarningAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralLaunderingAnalyzerTests.cs:CascadedShapes_TupleArrayListAndRouted_ReportOnlyTheSourcedEventsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralLaunderingAnalyzerTests.cs:DeliberateReceptor_WithTheOptIn_NoWHIZ132Async</tests>
  public static readonly DiagnosticDescriptor EphemeralToSourcedLaundering = new(
    id: "WHIZ132",
    title: "Receptor turns an ephemeral message into a Sourced event",
    messageFormat: "Receptor '{0}' handles ephemeral message '{1}' and returns or cascades Sourced event(s) ({2}). "
      + "The ephemeral message self-destructs, but what it carried becomes a durable fact in the event store: return "
      + "an ephemeral event instead, or mark the receptor [DangerouslyAllowSourcedEventsFromEphemeralInput] if keeping "
      + "it is deliberate.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "An event is declared ephemeral because what it carries must not be kept. A Sourced event returned "
      + "from its receptor is cascaded into the event store and never reaped, which defeats that declaration.");

  /// <summary>
  /// WHIZ134: Error — a type inherits its ephemeral mode from two or more sibling profile interfaces
  /// that disagree, with no more-specific override to break the tie. Resolve it by declaring an explicit
  /// <c>[Ephemeral]</c> on the type. Error (not warning): the generator would otherwise pick one silently,
  /// so the type's runtime behaviour would be order-dependent.
  /// </summary>
  /// <docs>operations/diagnostics/whiz134</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EphemeralAnalyzerTests.cs:TwoConflictingProfiles_ReportsWHIZ134ErrorAsync</tests>
  public static readonly DiagnosticDescriptor AmbiguousComposition = new(
    id: "WHIZ134",
    title: "Ambiguous ephemeral composition",
    messageFormat: "Type '{0}' inherits [Ephemeral] from two or more profiles that disagree, and nothing "
      + "more specific breaks the tie. Declare an explicit [Ephemeral(...)] on '{0}' to resolve it.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "When a type composes conflicting ephemeral profiles, the effective mode is otherwise "
      + "resolved by a deterministic-but-arbitrary rule; an explicit declaration makes the choice clear.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [MixedModePerspective, ReplayOfEphemeralOnlyPerspective, EphemeralToSourcedLaundering, AmbiguousComposition];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
    context.RegisterOperationAction(_analyzeRebuildCommand, OperationKind.ObjectCreation);   // WHIZ131
    context.RegisterOperationAction(_analyzeReplayCall, OperationKind.Invocation);           // WHIZ131
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    if (context.Symbol is not INamedTypeSymbol symbol
        || (symbol.TypeKind != TypeKind.Class && symbol.TypeKind != TypeKind.Struct)) {
      return;
    }
    _checkMixedModePerspective(context, symbol);   // WHIZ130
    _checkLaundering(context, symbol);             // WHIZ132
    _checkAmbiguousComposition(context, symbol);   // WHIZ134
  }

  private static void _checkMixedModePerspective(SymbolAnalysisContext context, INamedTypeSymbol symbol) {
    var events = _appliedEventTypes(symbol);
    if (events.Count == 0) {
      return;   // not a perspective
    }

    var ephemeral = new List<string>();
    var sourced = new List<string>();
    foreach (var evt in events) {
      (EphemeralResolver.IsEphemeral(evt) ? ephemeral : sourced).Add(evt.Name);
    }

    if (ephemeral.Count > 0 && sourced.Count > 0 && !_hasDangerousMixOptIn(symbol)) {
      context.ReportDiagnostic(Diagnostic.Create(
        MixedModePerspective,
        LocationUtilities.FirstOrNone(symbol),
        symbol.Name,
        string.Join(", ", ephemeral),
        string.Join(", ", sourced)));
    }
  }

  private static void _checkAmbiguousComposition(SymbolAnalysisContext context, INamedTypeSymbol symbol) {
    if (EphemeralResolver.IsAmbiguousComposition(symbol)) {
      context.ReportDiagnostic(Diagnostic.Create(
        AmbiguousComposition,
        LocationUtilities.FirstOrNone(symbol),
        symbol.Name));
    }
  }

  /// <summary>
  /// WHIZ132: one diagnostic per receptor interface whose message is ephemeral and whose response holds a Sourced
  /// event. Void receptors return nothing to cascade, so only the two response-carrying shapes are read.
  /// </summary>
  private static void _checkLaundering(SymbolAnalysisContext context, INamedTypeSymbol symbol) {
    foreach (var receptor in symbol.AllInterfaces) {
      if (!_isResponseReceptor(receptor) || !EphemeralResolver.IsEphemeral(receptor.TypeArguments[0])) {
        continue;
      }
      var sourced = new List<string>();
      _collectSourcedEvents(receptor.TypeArguments[1], sourced);
      if (sourced.Count > 0 && !_hasLaunderingOptIn(symbol)) {
        context.ReportDiagnostic(Diagnostic.Create(
          EphemeralToSourcedLaundering,
          LocationUtilities.FirstOrNone(symbol),
          symbol.Name,
          receptor.TypeArguments[0].Name,
          string.Join(", ", sourced.Distinct(System.StringComparer.Ordinal))));
      }
    }
  }

  private static bool _isResponseReceptor(INamedTypeSymbol iface) =>
    TypeNameUtilities.IsNamed(iface.OriginalDefinition, RECEPTOR)
    || TypeNameUtilities.IsNamed(iface.OriginalDefinition, SYNC_RECEPTOR);

  /// <summary>
  /// The Sourced events a response cascades, unwrapped the way the cascade unwraps them: tuple elements (nested
  /// tuples too), array and collection elements, <c>Routed&lt;T&gt;</c> and nullable values. Only a concrete event
  /// counts: an interface or an abstract base may stand for either mode, so it is not guessed at.
  /// </summary>
  private static void _collectSourcedEvents(ITypeSymbol type, List<string> sourced) {
    switch (type) {
      case INamedTypeSymbol { IsTupleType: true } tuple:
        foreach (var element in tuple.TupleElements) {
          _collectSourcedEvents(element.Type, sourced);
        }
        break;
      case IArrayTypeSymbol array:
        _collectSourcedEvents(array.ElementType, sourced);
        break;
      case INamedTypeSymbol named when _isEvent(named):
        if (!named.IsAbstract && !EphemeralResolver.IsEphemeral(named)) {
          sourced.Add(named.Name);
        }
        break;
      case INamedTypeSymbol wrapper:
        var inner = _wrappedType(wrapper);
        if (inner is not null) {
          _collectSourcedEvents(inner, sourced);
        }
        break;
    }
  }

  private static bool _isEvent(INamedTypeSymbol type) =>
    type.AllInterfaces.Any(i => TypeNameUtilities.IsNamed(i, EVENT));

  /// <summary>The value a <c>Nullable&lt;T&gt;</c> or <c>Routed&lt;T&gt;</c> holds, or an enumerable's element; else null.</summary>
  private static ITypeSymbol? _wrappedType(INamedTypeSymbol type) {
    var definition = type.OriginalDefinition;
    if (definition.SpecialType == SpecialType.System_Nullable_T || TypeNameUtilities.IsNamed(definition, ROUTED)) {
      return type.TypeArguments[0];
    }
    return _enumerableOf(type)?.TypeArguments[0];
  }

  private static INamedTypeSymbol? _enumerableOf(INamedTypeSymbol type) =>
    type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
      ? type
      : type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);

  /// <summary>
  /// True when the receptor carries <c>[DangerouslyAllowSourcedEventsFromEphemeralInput]</c>, matched by simple name
  /// like WHIZ130's opt-in so it holds when the attribute does not bind.
  /// </summary>
  private static bool _hasLaunderingOptIn(INamedTypeSymbol receptor) =>
    receptor.GetAttributes().Any(a =>
      TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is LAUNDERING_OPT_IN or "DangerouslyAllowSourcedEventsFromEphemeralInput");

  /// <summary>
  /// WHIZ131 at <c>new RebuildPerspectiveCommand(...)</c>: each name in <c>PerspectiveNames</c>, given positionally,
  /// by name or in an object initializer, as an array or a collection expression.
  /// </summary>
  private static void _analyzeRebuildCommand(OperationAnalysisContext context) {
    var creation = (IObjectCreationOperation)context.Operation;
    if (!TypeNameUtilities.IsNamed(creation.Type, REBUILD_COMMAND)) {
      return;
    }
    foreach (var argument in creation.Arguments) {
      if (TypeNameUtilities.SimpleNameOrNull(argument.Parameter) == PERSPECTIVE_NAMES) {
        _checkNames(context, argument.Value);
      }
    }
    if (creation.Initializer is null) {
      return;
    }
    foreach (var assignment in creation.Initializer.Initializers.OfType<ISimpleAssignmentOperation>()) {
      // A member that did not bind is an invalid operation, not a property reference.
      if (assignment.Target is IPropertyReferenceOperation property && property.Property.Name == PERSPECTIVE_NAMES) {
        _checkNames(context, assignment.Value);
      }
    }
  }

  private static void _checkNames(OperationAnalysisContext context, IOperation names) {
    foreach (var name in _nameElements(names)) {
      _reportIfEphemeralOnly(context, name, REBUILD);
    }
  }

  /// <summary>
  /// The elements of an array creation or a collection expression; nothing for any other value. A collection
  /// expression reaches the parameter through its target-typed conversion, which is looked through.
  /// </summary>
  private static ImmutableArray<IOperation> _nameElements(IOperation names) => _withoutConversions(names) switch {
    IArrayCreationOperation array => array.Initializer?.ElementValues ?? [],
    ICollectionExpressionOperation collection => collection.Elements,
    _ => [],
  };

  private static IOperation _withoutConversions(IOperation operation) {
    while (operation is IConversionOperation conversion) {
      operation = conversion.Operand;
    }
    return operation;
  }

  /// <summary>
  /// WHIZ131 at a rebuilder's <c>Rebuild*</c> call or a runner's <c>RewindAndRunAsync</c>: the
  /// <c>perspectiveName</c> argument. The method is matched on the interface or on any type implementing it.
  /// </summary>
  private static void _analyzeReplayCall(OperationAnalysisContext context) {
    var invocation = (IInvocationOperation)context.Operation;
    var action = _replayAction(invocation.TargetMethod);
    if (action is null) {
      return;
    }
    foreach (var argument in invocation.Arguments) {
      if (TypeNameUtilities.SimpleNameOrNull(argument.Parameter) == PERSPECTIVE_NAME_PARAMETER) {
        _reportIfEphemeralOnly(context, argument.Value, action);
      }
    }
  }

  /// <summary>"rebuild", "rewind", or null when the method replays nothing.</summary>
  /// <remarks>Every method is declared on a type, which is what it is matched within.</remarks>
  private static string? _replayAction(IMethodSymbol method) {
    if (method.Name.StartsWith(REBUILD_METHOD_PREFIX, System.StringComparison.Ordinal)
        && _isOrImplements(method.ContainingType!, REBUILDER)) {
      return REBUILD;
    }
    return method.Name == REWIND_METHOD && _isOrImplements(method.ContainingType!, RUNNER) ? REWIND : null;
  }

  private static bool _isOrImplements(INamedTypeSymbol type, string interfaceName) =>
    TypeNameUtilities.IsNamed(type, interfaceName)
    || type.AllInterfaces.Any(i => TypeNameUtilities.IsNamed(i, interfaceName));

  private static void _reportIfEphemeralOnly(OperationAnalysisContext context, IOperation name, string action) {
    var perspective = _namedType(name, context.Compilation);
    if (perspective is null) {
      return;
    }
    var events = _appliedEventTypes(perspective);
    if (events.Count > 0 && events.TrueForAll(EphemeralResolver.IsEphemeral)) {
      context.ReportDiagnostic(Diagnostic.Create(
        ReplayOfEphemeralOnlyPerspective,
        name.Syntax.GetLocation(),
        action,
        TypeNameUtilities.BuildClrTypeName(perspective),
        string.Join(", ", events.Select(e => e.Name))));
    }
  }

  /// <summary>
  /// The type a perspective name statically denotes: the operand of <c>typeof(T).FullName</c> or of
  /// <c>TypeNameFormatter.FormatClrTypeName(typeof(T))</c>, or the type whose CLR name (the name the runtime
  /// registry matches, <c>Ns.Outer+Nested</c>) a compile-time constant string spells. Null for anything else.
  /// </summary>
  private static INamedTypeSymbol? _namedType(IOperation name, Compilation compilation) {
    var typeOf = _clrNameOperand(name);
    if (typeOf is not null) {
      return typeOf.TypeOperand as INamedTypeSymbol;
    }
    var constant = name.ConstantValue;
    return constant.HasValue && constant.Value is string text ? compilation.GetTypeByMetadataName(text) : null;
  }

  private static ITypeOfOperation? _clrNameOperand(IOperation name) => name switch {
    IPropertyReferenceOperation property when property.Property.Name == FULL_NAME => property.Instance as ITypeOfOperation,
    IInvocationOperation call when _isFormatClrTypeName(call.TargetMethod) => call.Arguments[0].Value as ITypeOfOperation,
    _ => null,
  };

  private static bool _isFormatClrTypeName(IMethodSymbol method) =>
    method.Name == FORMAT_CLR_TYPE_NAME && TypeNameUtilities.IsNamed(method.ContainingType, TYPE_NAME_FORMATTER);

  /// <summary>
  /// True when the perspective carries <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents]</c> — the
  /// developer's explicit, in-code acknowledgement of the mixed-mode path. Matched by simple name so it
  /// works even when the source compilation can't fully bind the attribute.
  /// </summary>
  private static bool _hasDangerousMixOptIn(INamedTypeSymbol perspective) =>
    perspective.GetAttributes().Any(a =>
      TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is DANGEROUS_MIX_OPT_IN or "DangerouslyAllowMixedEphemeralAndSourcedEvents");

  /// <summary>
  /// The distinct event types a perspective applies — the type arguments (from index 1) of every
  /// <c>IPerspectiveBase&lt;TModel, TEvent…&gt;</c> it implements (single interface with many events, or
  /// many single-event interfaces; the unified base covers both <c>IPerspectiveFor</c> and
  /// <c>IPerspectiveWithActionsFor</c>).
  /// </summary>
  private static List<INamedTypeSymbol> _appliedEventTypes(INamedTypeSymbol perspective) {
    var events = new List<INamedTypeSymbol>();
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    foreach (var iface in perspective.AllInterfaces) {
      var def = TypeNameUtilities.Display(iface.OriginalDefinition);
      if (!def.StartsWith(PERSPECTIVE_BASE + "<TModel, TEvent", System.StringComparison.Ordinal)
          || iface.TypeArguments.Length < 2) {
        continue;
      }
      foreach (var arg in iface.TypeArguments.Skip(1)) {
        if (arg is INamedTypeSymbol evt && seen.Add(TypeNameUtilities.Display(evt))) {
          events.Add(evt);
        }
      }
    }
    return events;
  }
}
