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
/// WHIZ607 and WHIZ608: a type's name is produced or resolved only by the shared naming code (#2267).
/// </summary>
/// <remarks>
/// <para>
/// A type name is a key: a perspective's table, a message's route, a composite child's id, a priority rule. Every
/// place that renders one its own way is a place two services, or two builds of one assembly, can disagree, and #2266
/// was one. So the names come from one place: <c>TypeNameFormatter</c> and <c>EventTypeMatchingHelper</c> at runtime,
/// <c>TypeNameUtilities</c> in the generators, each marked <c>[TypeNameAuthority]</c>. Anywhere else, WHIZ607 reports:
/// </para>
/// <list type="bullet">
/// <item><description>runtime: <c>Name</c>, <c>FullName</c>, <c>Namespace</c>, <c>AssemblyQualifiedName</c> or
/// <c>ToString()</c> on a receiver statically typed <c>System.Type</c> (or <c>TypeInfo</c>, which derives from it);
/// <c>Assembly.FullName</c>, <c>Assembly.GetName()</c>, <c>AssemblyName.Name</c> and <c>AssemblyName.FullName</c>; and
/// a type resolved from a string (<c>Type.GetType(string, ...)</c>, <c>Assembly.GetType(string, ...)</c>);</description></item>
/// <item><description>generators: <c>Name</c>, <c>MetadataName</c>, <c>ContainingNamespace</c>,
/// <c>ContainingAssembly</c>, <c>Identity</c>, <c>ToDisplayString()</c> or <c>ToDisplayParts()</c> on a receiver that
/// is a type, namespace or assembly symbol.</description></item>
/// </list>
/// <para>
/// A member's name read through a member receiver (<c>MethodInfo.Name</c>, <c>IMethodSymbol.Name</c>) is not a type
/// name and is not reported. The one other way out is <c>[SuppressMessage]</c> with a Justification that says why;
/// WHIZ608 reports one with no justification, and any <c>#pragma warning disable WHIZ607</c>, which cannot carry one.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/TypeNameSourceAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypeNameSourceAnalyzer : DiagnosticAnalyzer {
  private const string AUTHORITY_ATTRIBUTE = "TypeNameAuthorityAttribute";
  private const string RULE_ID = "WHIZ607";

  /// <summary>WHIZ607: Error. A type's name is read or resolved outside the shared naming code.</summary>
  /// <docs>operations/diagnostics/whiz607</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/TypeNameSourceAnalyzerTests.cs:Runtime_ANameReadOffATypeOrAssembly_IsReportedAsync</tests>
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ607",
    title: "A type name is read outside the shared naming code",
    messageFormat: "'{0}' reads or resolves a type name outside the shared naming code; use TypeNameFormatter or EventTypeMatchingHelper (runtime) or TypeNameUtilities (generators), or suppress it with [SuppressMessage] and a Justification",
    category: "Naming",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Type names are keys; one rendering lives in the shared naming code, so two services or two builds never disagree (#2266, #2267).");

  /// <summary>WHIZ608: Error. A suppression of WHIZ607 does not say why.</summary>
  /// <docs>operations/diagnostics/whiz608</docs>
  /// <tests>tests/Whizbang.RepoAnalyzers.Tests/TypeNameSourceAnalyzerTests.cs:Suppression_WithoutAJustification_IsReportedAsync</tests>
  private static readonly DiagnosticDescriptor _unjustified = new(
    id: "WHIZ608",
    title: "A type-naming exception does not say why",
    messageFormat: "{0} suppresses WHIZ607 without saying why; use [SuppressMessage] with a non-empty Justification",
    category: "Naming",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Every exception to the type-naming rule records its reason where it is made (#2267).");

  private static readonly ImmutableHashSet<string> _runtimeTypeNames =
    ImmutableHashSet.Create("Name", "FullName", "Namespace", "AssemblyQualifiedName");

  private static readonly ImmutableHashSet<string> _symbolNames =
    ImmutableHashSet.Create("Name", "MetadataName", "ContainingNamespace", "ContainingAssembly", "Identity");

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule, _unjustified];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var known = new KnownTypes(start.Compilation);
      start.RegisterOperationAction(c => _property(c, known), OperationKind.PropertyReference);
      start.RegisterOperationAction(c => _invocation(c, known), OperationKind.Invocation);
      start.RegisterSyntaxNodeAction(c => _attribute(c, known), SyntaxKind.Attribute);
    });
    context.RegisterSyntaxTreeAction(_pragmas);
  }

  private sealed class KnownTypes(Compilation compilation) {
    public INamedTypeSymbol? Type { get; } = compilation.GetTypeByMetadataName("System.Type");
    public INamedTypeSymbol? Assembly { get; } = compilation.GetTypeByMetadataName("System.Reflection.Assembly");
    public INamedTypeSymbol? AssemblyName { get; } = compilation.GetTypeByMetadataName("System.Reflection.AssemblyName");
    public INamedTypeSymbol? SuppressMessage { get; } =
      compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.SuppressMessageAttribute");
    public ImmutableArray<INamedTypeSymbol> NamingSymbols { get; } = [.. new[] {
        "Microsoft.CodeAnalysis.ITypeSymbol",
        "Microsoft.CodeAnalysis.INamespaceSymbol",
        "Microsoft.CodeAnalysis.IAssemblySymbol",
      }.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()];
  }

  private static void _property(OperationAnalysisContext context, KnownTypes known) {
    var reference = (IPropertyReferenceOperation)context.Operation;
    var receiver = reference.Instance?.Type;
    var name = reference.Property.Name;
    var reported =
      (_runtimeTypeNames.Contains(name) && _isOrDerives(receiver, known.Type))
      || (name == "FullName" && _isOrDerives(receiver, known.Assembly))
      || ((name == "Name" || name == "FullName") && _isOrDerives(receiver, known.AssemblyName))
      || (_symbolNames.Contains(name) && _isNamingSymbol(receiver, known));
    _report(context, reported, reference.Syntax);
  }

  private static void _invocation(OperationAnalysisContext context, KnownTypes known) {
    var invocation = (IInvocationOperation)context.Operation;
    var method = invocation.TargetMethod;
    var receiver = invocation.Instance?.Type
      ?? (method.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value.Type : null);
    var resolvesByName = method.Name == "GetType" && method.Parameters.Length > 0
      && method.Parameters[0].Type.SpecialType == SpecialType.System_String;
    var reported = method.Name switch {
      "ToString" => method.Parameters.Length == 0 && _isOrDerives(receiver, known.Type),
      "GetName" => _isOrDerives(receiver, known.Assembly),
      "GetType" => resolvesByName
        && (SymbolEqualityComparer.Default.Equals(method.ContainingType, known.Type) || _isOrDerives(receiver, known.Assembly)),
      "ToDisplayString" or "ToDisplayParts" => _isNamingSymbol(receiver, known),
      _ => false,
    };
    _report(context, reported, invocation.Syntax);
  }

  private static void _report(OperationAnalysisContext context, bool reported, SyntaxNode syntax) {
    if (reported && !_insideAuthority(context.ContainingSymbol)) {
      context.ReportDiagnostic(Diagnostic.Create(_rule, syntax.GetLocation(), syntax.ToString()));
    }
  }

  private static void _attribute(SyntaxNodeAnalysisContext context, KnownTypes known) {
    var attribute = (AttributeSyntax)context.Node;
    if (known.SuppressMessage is null
        || context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol constructor
        || !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, known.SuppressMessage)) {
      return;
    }
    var arguments = attribute.ArgumentList?.Arguments ?? default;
    var positional = arguments.Where(a => a.NameEquals is null).ToList();
    var checkId = positional.Count > 1 ? _constant(context, positional[1].Expression) : null;
    if (checkId?.StartsWith(RULE_ID, System.StringComparison.Ordinal) != true) {
      return;
    }
    var justification = arguments
      .Where(a => a.NameEquals?.Name.Identifier.ValueText == "Justification")
      .Select(a => _constant(context, a.Expression))
      .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(justification)) {
      context.ReportDiagnostic(Diagnostic.Create(_unjustified, attribute.GetLocation(), "[SuppressMessage]"));
    }
  }

  private static string? _constant(SyntaxNodeAnalysisContext context, ExpressionSyntax expression) =>
    context.SemanticModel.GetConstantValue(expression, context.CancellationToken).Value as string;

  private static void _pragmas(SyntaxTreeAnalysisContext context) {
    var root = context.Tree.GetRoot(context.CancellationToken);
    foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true)) {
      if (trivia.GetStructure() is PragmaWarningDirectiveTriviaSyntax pragma
          && pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)
          && pragma.ErrorCodes.Any(code => code.ToString().Trim() == RULE_ID)) {
        context.ReportDiagnostic(Diagnostic.Create(_unjustified, pragma.GetLocation(), "#pragma warning disable"));
      }
    }
  }

  private static bool _insideAuthority(ISymbol? symbol) {
    for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type is not null; type = type.ContainingType) {
      if (type.GetAttributes().Any(a => a.AttributeClass?.Name == AUTHORITY_ATTRIBUTE)) {
        return true;
      }
    }
    return false;
  }

  private static bool _isOrDerives(ITypeSymbol? type, INamedTypeSymbol? target) {
    if (type is null || target is null) {
      return false;
    }
    for (var current = type; current is not null; current = current.BaseType) {
      if (SymbolEqualityComparer.Default.Equals(current, target)) {
        return true;
      }
    }
    return false;
  }

  private static bool _isNamingSymbol(ITypeSymbol? type, KnownTypes known) =>
    type is not null && known.NamingSymbols.Any(naming =>
      SymbolEqualityComparer.Default.Equals(type, naming)
      || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, naming)));
}
