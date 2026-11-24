// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHIZ610: a type's name is formatted for a log call only once the log level that consumes it is enabled (#2548).
/// </summary>
/// <remarks>
/// <para>
/// The runtime caches a type's name behind a weak handle, so any garbage collection can drop it and the next read
/// allocates again. A per-message path that formats a type name for a debug log before checking the level therefore
/// allocates after every collection with debug off. WHIZ607 already routes every type-name read in the library through
/// the shared naming code (the types marked <c>[TypeNameAuthority]</c>), so this rule looks at those calls only: a call
/// that takes a <see cref="System.Type"/> and whose result is an argument of a log call at <c>Information</c> or below
/// is reported unless the log call sits behind an <c>IsEnabled</c> check. The same holds for the name passed to
/// <c>ActivitySource.StartActivity</c> or <c>CreateActivity</c>, which is built even when nothing listens, behind a
/// <c>HasListeners</c> check. A local that holds such a name and is read
/// only by log calls is held to the same rule at its declaration.
/// </para>
/// <para>
/// Recognized log calls: a method marked <c>[LoggerMessage]</c> (its level from the attribute; a level passed as a
/// parameter counts as below <c>Warning</c>), the <c>LoggerExtensions</c> methods <c>LogTrace</c>, <c>LogDebug</c>,
/// <c>LogInformation</c> and <c>Log</c>, and <c>ILogger.Log</c>. Warnings and errors are on in production and fire on
/// rare paths, so they are not reported.
/// </para>
/// <para>
/// Recognized checks (<c>IsEnabled</c>, or <c>HasListeners</c>), on any enclosing construct: <c>if (...IsEnabled(...)...)</c> with the call in the then-branch,
/// <c>...IsEnabled(...) ? call : ...</c>, <c>...IsEnabled(...) &amp;&amp; call</c>, an earlier
/// <c>if (!...IsEnabled(...)) return;</c> (or another jump) in an enclosing block, and any of those through a local
/// <c>bool</c> initialized from an <c>IsEnabled</c> call; <c>logger?.IsEnabled(...) == true</c> counts as the call. A read
/// inside a <c>catch</c> clause is not reported: it runs only once an exception was thrown, which costs far more.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/EagerLogTypeNameAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EagerLogTypeNameAnalyzer : DiagnosticAnalyzer {
  private const string AUTHORITY_ATTRIBUTE = "TypeNameAuthorityAttribute";
  private const string LOGGER_MESSAGE_ATTRIBUTE = "Microsoft.Extensions.Logging.LoggerMessageAttribute";
  private const string LOGGER_EXTENSIONS = "Microsoft.Extensions.Logging.LoggerExtensions";
  private const string LOGGER = "Microsoft.Extensions.Logging.ILogger";
  private const string LOG_LEVEL = "Microsoft.Extensions.Logging.LogLevel";
  private const string ACTIVITY_SOURCE = "System.Diagnostics.ActivitySource";
  private const int WARNING_LEVEL = 3;

  /// <summary>WHIZ610: Error. A type name is formatted for a log call before the log level is checked.</summary>
  /// <docs>operations/diagnostics/whiz610</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/EagerLogTypeNameAnalyzerTests.cs:UnguardedLogArgument_IsReportedAsync</tests>
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ610",
    title: "A type name is formatted for a log call before the log level is checked",
    messageFormat: "'{0}' reads a type name for a log call that has not checked its level; read it inside an IsEnabled check",
    category: "Performance",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "A type's name is cached behind a weak handle, so reading it with the level off allocates after every garbage collection (#2548).");

  private static readonly ImmutableHashSet<string> _lowLevelExtensions =
    ImmutableHashSet.Create("LogTrace", "LogDebug", "LogInformation");

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var known = new KnownTypes(start.Compilation);
      start.RegisterOperationAction(c => _invocation(c, known), OperationKind.Invocation);
    });
  }

  private sealed class KnownTypes(Compilation compilation) {
    public INamedTypeSymbol? Type { get; } = compilation.GetTypeByMetadataName("System.Type");
    public INamedTypeSymbol? LoggerMessage { get; } = compilation.GetTypeByMetadataName(LOGGER_MESSAGE_ATTRIBUTE);
    public INamedTypeSymbol? LoggerExtensions { get; } = compilation.GetTypeByMetadataName(LOGGER_EXTENSIONS);
    public INamedTypeSymbol? Logger { get; } = compilation.GetTypeByMetadataName(LOGGER);
    public INamedTypeSymbol? LogLevel { get; } = compilation.GetTypeByMetadataName(LOG_LEVEL);
    public INamedTypeSymbol? ActivitySource { get; } = compilation.GetTypeByMetadataName(ACTIVITY_SOURCE);
  }

  private static void _invocation(OperationAnalysisContext context, KnownTypes known) {
    var read = (IInvocationOperation)context.Operation;
    if (!_readsATypeName(read.TargetMethod, known)) {
      return;
    }
    var (logCall, local) = _consumer(read, known);
    if (logCall is not null) {
      _reportUnlessGuarded(context, read, logCall.Syntax);
    } else if (local is not null && _onlyLogCallsRead(local, read, known)) {
      _reportUnlessGuarded(context, read, read.Syntax);
    }
  }

  private static void _reportUnlessGuarded(OperationAnalysisContext context, IInvocationOperation read, SyntaxNode site) {
    if (!_isGuarded(site, read.SemanticModel!)) {
      context.ReportDiagnostic(Diagnostic.Create(_rule, read.Syntax.GetLocation(), read.Syntax.ToString()));
    }
  }

  /// <summary>A method of the shared naming code that renders a name from a <see cref="System.Type"/>.</summary>
  private static bool _readsATypeName(IMethodSymbol method, KnownTypes known) =>
    method.ContainingType.GetAttributes().Any(a => a.AttributeClass?.Name == AUTHORITY_ATTRIBUTE)
    && method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, known.Type));

  /// <summary>
  /// The log call whose argument the read becomes, climbing through the expressions that carry a value into an argument,
  /// or else the local the read initializes.
  /// </summary>
  private static (IInvocationOperation? LogCall, ILocalSymbol? Local) _consumer(IOperation read, KnownTypes known) {
    var current = read.Parent;
    while (_carriesTheValueOn(current, known)) {
      current = current!.Parent;
    }
    return current switch {
      IInvocationOperation invocation when _isConsumer(invocation, known) => (invocation, null),
      IVariableDeclaratorOperation declarator => (null, declarator.Symbol),
      _ => (null, null),
    };
  }

  /// <summary>An operation that passes its operand's value on towards an argument or a local, short of a consumer.</summary>
  private static bool _carriesTheValueOn(IOperation? operation, KnownTypes known) => operation switch {
    IInvocationOperation invocation => !_isConsumer(invocation, known),
    IArgumentOperation or IConversionOperation or IInterpolationOperation or IInterpolatedStringOperation
      or IConditionalOperation or ICoalesceOperation or IBinaryOperation or IArrayInitializerOperation
      or IArrayCreationOperation or IParenthesizedOperation or IVariableInitializerOperation => true,
    _ => false,
  };

  private static bool _isConsumer(IInvocationOperation invocation, KnownTypes known) =>
    _isLowLevelLogCall(invocation, known) || _startsAnActivity(invocation, known);

  /// <summary>Whether every read of <paramref name="local"/> in its method is an argument of a log call.</summary>
  private static bool _onlyLogCallsRead(ILocalSymbol local, IOperation read, KnownTypes known) {
    var root = read;
    while (root.Parent is not null) {
      root = root.Parent;
    }
    var uses = root.Descendants().OfType<ILocalReferenceOperation>()
      .Where(r => SymbolEqualityComparer.Default.Equals(r.Local, local))
      .ToList();
    return uses.Count > 0 && uses.All(use => _consumer(use, known).LogCall is not null);
  }

  /// <summary><c>ActivitySource.StartActivity</c> or <c>CreateActivity</c>: its name is built whether or not anyone listens.</summary>
  private static bool _startsAnActivity(IInvocationOperation invocation, KnownTypes known) =>
    invocation.TargetMethod.Name is "StartActivity" or "CreateActivity"
    && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, known.ActivitySource);

  private static bool _isLowLevelLogCall(IInvocationOperation invocation, KnownTypes known) {
    var method = invocation.TargetMethod;
    var loggerMessage = method.GetAttributes()
      .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, known.LoggerMessage));
    if (loggerMessage is not null) {
      return _attributeLevel(loggerMessage, known) is not { } level || level < WARNING_LEVEL;
    }
    if (SymbolEqualityComparer.Default.Equals(method.ContainingType, known.LoggerExtensions)) {
      return _lowLevelExtensions.Contains(method.Name) || (method.Name == "Log" && _levelArgumentIsLow(invocation, known));
    }
    return method.Name == "Log" && SymbolEqualityComparer.Default.Equals(method.ContainingType, known.Logger)
      && _levelArgumentIsLow(invocation, known);
  }

  /// <summary>The level a <c>[LoggerMessage]</c> declares, named or positional; null when the method takes it.</summary>
  private static int? _attributeLevel(AttributeData attribute, KnownTypes known) {
    var named = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Level");
    if (named.Key is not null) {
      return named.Value.Value as int?;
    }
    var positional = attribute.ConstructorArguments.FirstOrDefault(p => SymbolEqualityComparer.Default.Equals(p.Type, known.LogLevel));
    return positional.Type is null ? null : positional.Value as int?;
  }

  /// <summary>A level passed to <c>Log(level, ...)</c> is low unless it is a constant at <c>Warning</c> or above.</summary>
  private static bool _levelArgumentIsLow(IInvocationOperation invocation, KnownTypes known) {
    // Every Log overload takes a level
    var level = invocation.Arguments.First(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.Type, known.LogLevel)).Value.ConstantValue;
    return !level.HasValue || (int)level.Value! < WARNING_LEVEL;
  }

  // ===== the level check =====

  /// <summary>
  /// Whether something enclosing <paramref name="child"/> runs it only when the level is enabled, climbing to the
  /// enclosing member, which every read in a method body, field or property has.
  /// </summary>
  private static bool _isGuarded(SyntaxNode child, SemanticModel model) {
    var ancestor = child.Parent!;
    switch (ancestor) {
      case IfStatementSyntax ifStatement when ifStatement.Statement == child && _checksEnabled(ifStatement.Condition, model):
      case ConditionalExpressionSyntax conditional when conditional.WhenTrue == child && _checksEnabled(conditional.Condition, model):
      case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } conjunction when conjunction.Right == child && _checksEnabled(conjunction.Left, model):
        return true;
      case BlockSyntax block when _earlierExitWhenDisabled(block.Statements, child, model):
      case SwitchSectionSyntax section when _earlierExitWhenDisabled(section.Statements, child, model):
        return true;
      case CatchClauseSyntax:
        // Runs only once an exception was thrown, which allocates far more than the name
        return true;
      case MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax:
        return false;
      default:
        return _isGuarded(ancestor, model);
    }
  }

  /// <summary>Whether a statement before <paramref name="child"/> leaves the block when the level is off.</summary>
  private static bool _earlierExitWhenDisabled(SyntaxList<StatementSyntax> statements, SyntaxNode child, SemanticModel model) =>
    statements.TakeWhile(statement => statement != child).Any(statement =>
      statement is IfStatementSyntax { Else: null } exit
      && exit.Condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
      && _checksEnabled(not.Operand, model)
      && _jumps(exit.Statement));

  private static bool _jumps(StatementSyntax statement) => statement switch {
    ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax => true,
    BlockSyntax { Statements.Count: > 0 } block => _jumps(block.Statements.Last()),
    _ => false,
  };

  /// <summary>
  /// Whether <paramref name="condition"/> is true only when a level is enabled: an <c>IsEnabled</c> call (also as
  /// <c>logger?.IsEnabled(...) == true</c>), a local initialized from one, or a conjunction holding either.
  /// </summary>
  private static bool _checksEnabled(ExpressionSyntax condition, SemanticModel model) => condition switch {
    ParenthesizedExpressionSyntax parenthesized => _checksEnabled(parenthesized.Expression, model),
    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } conjunction =>
      _checksEnabled(conjunction.Left, model) || _checksEnabled(conjunction.Right, model),
    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } equals =>
      (equals.Right.IsKind(SyntaxKind.TrueLiteralExpression) && _checksEnabled(equals.Left, model))
      || (equals.Left.IsKind(SyntaxKind.TrueLiteralExpression) && _checksEnabled(equals.Right, model)),
    ConditionalAccessExpressionSyntax { WhenNotNull: InvocationExpressionSyntax invocation } => _isEnabledCall(invocation),
    InvocationExpressionSyntax invocation => _isEnabledCall(invocation),
    IdentifierNameSyntax identifier => _isEnabledLocal(identifier, model),
    _ => false,
  };

  private static bool _isEnabledCall(InvocationExpressionSyntax invocation) => invocation.Expression switch {
    MemberAccessExpressionSyntax member => _isCheckName(member.Name.Identifier.ValueText),
    MemberBindingExpressionSyntax binding => _isCheckName(binding.Name.Identifier.ValueText),
    IdentifierNameSyntax name => _isCheckName(name.Identifier.ValueText),
    _ => false,
  };

  // ILogger.IsEnabled for a log call; ActivitySource.HasListeners for an activity's name
  private static bool _isCheckName(string name) => name is "IsEnabled" or "HasListeners";

  private static bool _isEnabledLocal(IdentifierNameSyntax identifier, SemanticModel model) {
    if (model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol local) {
      return false;
    }
    return local.DeclaringSyntaxReferences
      .Select(r => r.GetSyntax())
      .OfType<VariableDeclaratorSyntax>()
      .Any(d => d.Initializer?.Value is InvocationExpressionSyntax init && _isEnabledCall(init));
  }
}
