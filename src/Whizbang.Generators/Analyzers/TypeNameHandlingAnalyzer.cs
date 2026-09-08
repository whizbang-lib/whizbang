using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// The residue the banned-API layer and the typed keys cannot catch (issue #698): a type name
/// composed, dissected, compared or assigned by hand. WHIZ160 flags concatenation or
/// interpolation that joins a type-name value with the CLR nested separator (<c>+</c>) or the
/// wire assembly separator (<c>, </c>); WHIZ161 flags <c>Split</c>, <c>IndexOf('+')</c>,
/// <c>Substring</c> and <c>Replace("global::", ...)</c> applied to a type-name value; WHIZ162 flags
/// <c>==</c>, <c>!=</c> and <c>string.Equals</c> between two type-name values; WHIZ163 flags a
/// type-name key (a member or parameter named <c>*ClrTypeName*</c>, <c>*TypeName*</c> or
/// <c>*EventType*</c>) assigned from an interpolated or concatenated string.
/// </summary>
/// <remarks>
/// A "type-name value" is a <c>typeof(...)</c>- or <c>GetType()</c>-rooted <c>FullName</c> /
/// <c>AssemblyQualifiedName</c> access (<c>Name</c> is display only), a call on one of the shared helpers
/// (<c>TypeNameFormatter</c>, <c>TypeNameUtilities</c>, <c>EventTypeMatchingHelper</c>), or an
/// identifier or member whose name carries <c>TypeName</c>, <c>ClrTypeName</c>, <c>EventType</c> or
/// <c>EnvelopeType</c>. The helpers themselves are exempt: they are where the one rendering per
/// form lives. Heuristic by nature, so every rule is a warning.
/// </remarks>
/// <docs>operations/diagnostics/whiz160</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/TypeNameHandlingAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypeNameHandlingAnalyzer : DiagnosticAnalyzer {
  private static readonly string[] _helperTypes = ["TypeNameFormatter", "TypeNameUtilities", "EventTypeMatchingHelper", "EnvelopeTypeNameHelper", "TypeFormatter"];
  private static readonly string[] _keyNameMarkers = ["clrtypename", "typename", "eventtype", "envelopetype"];
  // Type.Name is display only (a simple name is never a key), so it is not a type-name value here.
  private static readonly string[] _rawNameMembers = ["FullName", "AssemblyQualifiedName"];
  private static readonly string[] _dissectors = ["Split", "Substring", "IndexOf", "LastIndexOf", "Replace"];

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.TypeNameComposedByHand,
    DiagnosticDescriptors.TypeNameDissectedByHand,
    DiagnosticDescriptors.TypeNamesComparedWithoutTheMatchingHelper,
    DiagnosticDescriptors.TypeNameKeyAssignedFromAHandBuiltString,
  ];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSyntaxNodeAction(_analyzeConcatenation, SyntaxKind.AddExpression);
    context.RegisterSyntaxNodeAction(_analyzeInterpolation, SyntaxKind.InterpolatedStringExpression);
    context.RegisterSyntaxNodeAction(_analyzeInvocation, SyntaxKind.InvocationExpression);
    context.RegisterSyntaxNodeAction(_analyzeEquality, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    context.RegisterSyntaxNodeAction(_analyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    context.RegisterSyntaxNodeAction(_analyzeArgument, SyntaxKind.Argument);
    context.RegisterSyntaxNodeAction(_analyzeDeclarator, SyntaxKind.VariableDeclarator);
  }

  // ---- WHIZ160: composition -------------------------------------------------------------------

  private static void _analyzeConcatenation(SyntaxNodeAnalysisContext context) {
    var binary = (BinaryExpressionSyntax)context.Node;
    // Only the outermost + of a chain reports, so one expression yields one diagnostic.
    if (binary.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }) {
      return;
    }
    if (_isInsideAHelper(binary)) {
      return;
    }
    var operands = new List<ExpressionSyntax>();
    _flatten(binary, operands);
    // The separator must join two parts of a name: a type-name value on its left and another
    // value (not a literal) on its right. ", " that merely ends a sentence in a message is prose.
    for (var i = 1; i < operands.Count - 1; i++) {
      var separator = _separatorLiteral(operands[i]);
      if (separator is null || _isLiteral(operands[i + 1])) {
        continue;
      }
      if (_isTypeNameValue(operands[i - 1], context.SemanticModel)) {
        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameComposedByHand, binary.GetLocation(), separator));
        return;
      }
    }
  }

  private static void _analyzeInterpolation(SyntaxNodeAnalysisContext context) {
    var interpolated = (InterpolatedStringExpressionSyntax)context.Node;
    if (_isInsideAHelper(interpolated)) {
      return;
    }
    // The separator text must sit BETWEEN two holes, with a type-name value on its left:
    // $"{typeName}, {assembly}" composes a name; $"...{typeName}, " ends a sentence.
    var contents = interpolated.Contents;
    for (var i = 1; i < contents.Count - 1; i++) {
      if (contents[i] is not InterpolatedStringTextSyntax text || contents[i - 1] is not InterpolationSyntax left || contents[i + 1] is not InterpolationSyntax) {
        continue;
      }
      var separator = _separatorText(text.TextToken.ValueText);
      if (separator is not null && _isTypeNameValue(left.Expression, context.SemanticModel)) {
        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameComposedByHand, interpolated.GetLocation(), separator));
        return;
      }
    }
  }

  // ---- WHIZ161: dissection --------------------------------------------------------------------

  private static void _analyzeInvocation(SyntaxNodeAnalysisContext context) {
    var invocation = (InvocationExpressionSyntax)context.Node;
    if (_isInsideAHelper(invocation)) {
      return;
    }
    if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } access
        && Array.IndexOf(_dissectors, method) >= 0
        && _isTypeNameValue(access.Expression, context.SemanticModel)
        && _dissectsATypeName(method, invocation.ArgumentList)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameDissectedByHand, invocation.GetLocation(), method));
      return;
    }
    // string.Equals(a, b[, comparison]) between two type-name values.
    if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" } equalsAccess
        && invocation.ArgumentList.Arguments.Count >= 2
        && _isStringType(equalsAccess.Expression)
        && _isTypeNameValue(invocation.ArgumentList.Arguments[0].Expression, context.SemanticModel)
        && _isTypeNameValue(invocation.ArgumentList.Arguments[1].Expression, context.SemanticModel)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNamesComparedWithoutTheMatchingHelper, invocation.GetLocation(),
        invocation.ArgumentList.Arguments[0].Expression.ToString(), invocation.ArgumentList.Arguments[1].Expression.ToString()));
    }
  }

  private static bool _dissectsATypeName(string method, ArgumentListSyntax arguments) {
    // Substring on a type name is always a hand parse; the others only when the argument is a
    // separator the helpers own (',' for the assembly, '+' for nesting, "global::" for the C# form).
    if (method == "Substring") {
      return true;
    }
    if (arguments.Arguments.Count == 0) {
      return false;
    }
    var first = arguments.Arguments[0].Expression;
    var text = first switch {
      LiteralExpressionSyntax literal => literal.Token.ValueText,
      _ => null,
    };
    if (text is null) {
      return false;
    }
    return method switch {
      "Replace" => text == "global::",
      "Split" => text is "," or ", " or "+",
      "IndexOf" or "LastIndexOf" => text is "+" or "," or "`",
      _ => false,
    };
  }

  // ---- WHIZ162: comparison --------------------------------------------------------------------

  private static void _analyzeEquality(SyntaxNodeAnalysisContext context) {
    var binary = (BinaryExpressionSyntax)context.Node;
    if (_isInsideAHelper(binary)) {
      return;
    }
    if (_isKeyNamed(binary.Left) && _isKeyNamed(binary.Right)
        && _isString(binary.Left, context.SemanticModel) && _isString(binary.Right, context.SemanticModel)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNamesComparedWithoutTheMatchingHelper, binary.GetLocation(),
        binary.Left.ToString(), binary.Right.ToString()));
    }
  }

  // ---- WHIZ163: assignment --------------------------------------------------------------------

  private static void _analyzeAssignment(SyntaxNodeAnalysisContext context) {
    var assignment = (AssignmentExpressionSyntax)context.Node;
    if (_isInsideAHelper(assignment)) {
      return;
    }
    if (_isKeyNamed(assignment.Left) && _isString(assignment.Left, context.SemanticModel) && _isHandBuiltString(assignment.Right)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameKeyAssignedFromAHandBuiltString, assignment.GetLocation(), assignment.Left.ToString()));
    }
  }

  private static void _analyzeArgument(SyntaxNodeAnalysisContext context) {
    var argument = (ArgumentSyntax)context.Node;
    if (_isInsideAHelper(argument) || !_isHandBuiltString(argument.Expression)) {
      return;
    }
    var name = argument.NameColon?.Name.Identifier.ValueText ?? _parameterNameFor(argument, context.SemanticModel);
    if (name is not null && _carriesKeyMarker(name)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameKeyAssignedFromAHandBuiltString, argument.GetLocation(), name));
    }
  }

  private static void _analyzeDeclarator(SyntaxNodeAnalysisContext context) {
    var declarator = (VariableDeclaratorSyntax)context.Node;
    if (_isInsideAHelper(declarator) || declarator.Initializer is null) {
      return;
    }
    if (_carriesKeyMarker(declarator.Identifier.ValueText) && _isHandBuiltString(declarator.Initializer.Value)) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.TypeNameKeyAssignedFromAHandBuiltString, declarator.GetLocation(), declarator.Identifier.ValueText));
    }
  }

  private static string? _parameterNameFor(ArgumentSyntax argument, SemanticModel model) {
    if (argument.Parent is not BaseArgumentListSyntax list) {
      return null;
    }
    var index = list.Arguments.IndexOf(argument);
    var symbol = model.GetSymbolInfo(list.Parent!).Symbol as IMethodSymbol;
    if (symbol is null || index < 0 || index >= symbol.Parameters.Length) {
      return null;
    }
    return symbol.Parameters[index].Name;
  }

  // ---- shared recognizers ---------------------------------------------------------------------

  private static bool _isHandBuiltString(ExpressionSyntax expression) => expression switch {
    InterpolatedStringExpressionSyntax interpolated => interpolated.Contents.OfType<InterpolationSyntax>().Any(),
    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } binary => _isStringConcatenation(binary),
    ParenthesizedExpressionSyntax parenthesized => _isHandBuiltString(parenthesized.Expression),
    _ => false,
  };

  private static bool _isStringConcatenation(BinaryExpressionSyntax binary) {
    var operands = new List<ExpressionSyntax>();
    _flatten(binary, operands);
    return operands.Any(o => o is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax);
  }

  private static void _flatten(ExpressionSyntax expression, List<ExpressionSyntax> operands) {
    if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } binary) {
      _flatten(binary.Left, operands);
      _flatten(binary.Right, operands);
    } else if (expression is ParenthesizedExpressionSyntax parenthesized) {
      _flatten(parenthesized.Expression, operands);
    } else {
      operands.Add(expression);
    }
  }

  private static string? _separatorLiteral(ExpressionSyntax expression) =>
    expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
      ? _separatorText(literal.Token.ValueText)
      : null;

  /// <summary>The CLR nested separator and the wire assembly separator; '.' is display, not a key.</summary>
  private static string? _separatorText(string text) => text switch {
    "+" => "+",
    ", " => ", ",
    _ => null,
  };

  private static bool _isTypeNameValue(ExpressionSyntax expression, SemanticModel model) {
    expression = _unwrap(expression);
    switch (expression) {
      case MemberAccessExpressionSyntax { Name.Identifier.ValueText: var member } access when Array.IndexOf(_rawNameMembers, member) >= 0:
        // typeof(X).FullName, x.GetType().Name, and any other Type-typed receiver.
        if (access.Expression is TypeOfExpressionSyntax) {
          return true;
        }
        if (access.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetType" } }) {
          return true;
        }
        return _isSystemType(access.Expression, model);
      case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var receiver } }:
        return receiver is IdentifierNameSyntax { Identifier.ValueText: var helper } && Array.IndexOf(_helperTypes, helper) >= 0
            || receiver is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var qualified } && Array.IndexOf(_helperTypes, qualified) >= 0;
      default:
        return _isKeyNamed(expression);
    }
  }

  private static bool _isString(ExpressionSyntax expression, SemanticModel model) =>
    model.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_String;

  private static bool _isLiteral(ExpressionSyntax expression) =>
    _unwrap(expression) is LiteralExpressionSyntax;

  private static bool _isSystemType(ExpressionSyntax receiver, SemanticModel model) {
    var type = model.GetTypeInfo(receiver).Type;
    return type is { Name: "Type", ContainingNamespace.Name: "System" };
  }

  private static bool _isStringType(ExpressionSyntax expression) =>
    expression is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.StringKeyword }
      || expression is IdentifierNameSyntax { Identifier.ValueText: "String" }
      || expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "String" };

  private static bool _isKeyNamed(ExpressionSyntax expression) {
    expression = _unwrap(expression);
    var name = expression switch {
      IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
      MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
      _ => null,
    };
    return name is not null && _carriesKeyMarker(name);
  }

  private static bool _carriesKeyMarker(string name) {
    var lower = name.ToLowerInvariant();
    if (Array.IndexOf(_helperTypes, name) >= 0 || lower.EndsWith("formatter", StringComparison.Ordinal) || lower.EndsWith("helper", StringComparison.Ordinal)) {
      return false;
    }
    foreach (var marker in _keyNameMarkers) {
      if (lower.Contains(marker)) {
        return true;
      }
    }
    return false;
  }

  private static ExpressionSyntax _unwrap(ExpressionSyntax expression) {
    while (true) {
      switch (expression) {
        case ParenthesizedExpressionSyntax parenthesized:
          expression = parenthesized.Expression;
          continue;
        case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed:
          expression = suppressed.Operand;
          continue;
        default:
          return expression;
      }
    }
  }

  /// <summary>The helpers are where the one rendering per form lives; they may do all of this.</summary>
  private static bool _isInsideAHelper(SyntaxNode node) {
    foreach (var ancestor in node.Ancestors()) {
      if (ancestor is TypeDeclarationSyntax type && Array.IndexOf(_helperTypes, type.Identifier.ValueText) >= 0) {
        return true;
      }
    }
    return false;
  }
}
