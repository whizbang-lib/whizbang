// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// The one definition of a perspective fan-out (#2618): an implementation of
/// <c>IPerspectiveFanOutFor&lt;TModel, TEvent&gt;</c> whose <c>Targets</c> is <c>FanOut.By&lt;TModel&gt;(row =&gt; row.Key)</c>
/// naming a key property that is a physical field, indexed, and able to hold a stream id. Every reader uses it: the
/// discovery generator that registers the fan-outs it accepts, the analyzer that reports the ones it does not
/// (WHIZ124 to WHIZ128), and the purity analyzer that holds a fan-out's <c>Set</c> to the set-based apply rule (WHIZ106).
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveFanOutDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveFanOutAnalyzerTests.cs</tests>
internal static class FanOutShape {
  internal const string INTERFACE_NAME = "IPerspectiveFanOutFor";
  internal const string TARGETS = "Targets";
  private const string SET = "Set";
  private const string INTERFACE_FQN = "Whizbang.Core.Perspectives.IPerspectiveFanOutFor<TModel, TEvent>";
  private const string FAN_OUT_FQN = "Whizbang.Core.Perspectives.FanOut";
  private const string SETTERS_FQN = "Whizbang.Core.Perspectives.ICollectiveSetters<TModel>";
  private const string PHYSICAL_FIELD = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string INDEXED = "Whizbang.Core.Perspectives.IndexedAttribute";

  /// <summary>
  /// What a <c>Targets</c> names: the key property, or null when it is not <c>FanOut.By&lt;TModel&gt;(row =&gt; row.Key)</c>,
  /// and where to report either.
  /// </summary>
  internal readonly record struct KeyRead(IPropertySymbol? Key, Location Location);

  /// <summary>Whether a base-list entry names the fan-out interface, by syntax alone.</summary>
  internal static bool NamesTheInterface(TypeSyntax type) =>
    type switch {
      QualifiedNameSyntax qualified => NamesTheInterface(qualified.Right),
      AliasQualifiedNameSyntax alias => NamesTheInterface(alias.Name),
      _ => type is GenericNameSyntax { Identifier.ValueText: INTERFACE_NAME, TypeArgumentList.Arguments.Count: 2 },
    };

  /// <summary>The fan-out interfaces <paramref name="type"/> implements, its own and inherited.</summary>
  internal static IEnumerable<INamedTypeSymbol> Interfaces(INamedTypeSymbol type) =>
    type.AllInterfaces.Where(static i => TypeNameUtilities.IsNamed(i.OriginalDefinition, INTERFACE_FQN));

  /// <summary>The property of <paramref name="type"/> that implements <paramref name="fanOut"/>'s <c>Targets</c>, or null.</summary>
  internal static IPropertySymbol? TargetsOf(INamedTypeSymbol type, INamedTypeSymbol fanOut) =>
    type.FindImplementationForInterfaceMember(fanOut.GetMembers(TARGETS)[0]) as IPropertySymbol;

  /// <summary>Whether <paramref name="method"/> implements a fan-out's <c>Set</c> for its containing type.</summary>
  internal static bool IsSet(IMethodSymbol method) =>
    Interfaces(method.ContainingType).Any(i => SymbolEqualityComparer.Default.Equals(
      method.ContainingType.FindImplementationForInterfaceMember(i.GetMembers(SET)[0]), method));

  /// <summary>
  /// Reads the key a <c>Targets</c> declaration names. The value is the expression the property returns: its expression
  /// body, its getter's expression body or single <c>return</c>, or the initializer of an auto-property.
  /// </summary>
  internal static KeyRead ReadKey(PropertyDeclarationSyntax property, SemanticModel model, CancellationToken ct) {
    var value = _returned(property);
    if (value is null) {
      return new KeyRead(null, property.Identifier.GetLocation());
    }
    if (value is not InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax { ExpressionBody: { } body } }] } invocation
        || model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol by
        || !TypeNameUtilities.IsNamed(by.ContainingType, FAN_OUT_FQN)) {
      return new KeyRead(null, value.GetLocation());
    }
    return new KeyRead(MemberOfParameter(body, model, ct), body.GetLocation());
  }

  /// <summary>
  /// The property <paramref name="body"/> reads directly off a lambda's parameter (<c>row.Key</c>), or null for any other
  /// expression: a constant, a nested member, a method call.
  /// </summary>
  internal static IPropertySymbol? MemberOfParameter(SyntaxNode body, SemanticModel model, CancellationToken ct) =>
    body is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } access
    && model.GetSymbolInfo(receiver, ct).Symbol is IParameterSymbol
    ? model.GetSymbolInfo(access, ct).Symbol as IPropertySymbol
    : null;

  /// <summary>
  /// The model and property a set-based apply assigns with
  /// <c>ICollectiveSetters&lt;TModel&gt;.SetProperty(row =&gt; row.X, ...)</c>, with the selector to report at; null for
  /// any other call.
  /// </summary>
  internal static (ITypeSymbol Model, IPropertySymbol Property, Location Location)? Assigned(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct) {
    if (invocation is not { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetProperty" }, ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax { ExpressionBody: { } body } selector }, ..] }
        || model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol setter
        || !TypeNameUtilities.IsNamed(setter.ContainingType.OriginalDefinition, SETTERS_FQN)
        || MemberOfParameter(body, model, ct) is not { } property) {
      return null;
    }
    return (setter.ContainingType.TypeArguments[0], property, selector.GetLocation());
  }

  /// <summary>Whether the key is a <c>[PhysicalField]</c>, so the rows are found by a column.</summary>
  internal static bool IsPhysicalField(IPropertySymbol key) => _attribute(key, PHYSICAL_FIELD) is not null;

  /// <summary>Whether the key carries <c>[Indexed]</c>, so a foreign event finds its rows without a scan.</summary>
  internal static bool IsIndexed(IPropertySymbol key) => _attribute(key, INDEXED) is not null;

  /// <summary>
  /// Whether the key's type holds a stream id: a <see cref="System.Guid"/> or a strongly typed id, nullable or not.
  /// </summary>
  internal static bool CanHoldStreamId(IPropertySymbol key) {
    var type = key.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : key.Type;
    return TypeNameUtilities.IsNamed(type, "System.Guid")
      || StrongIdStreamIds.IsStrongIdType(type)
      || StrongIdStreamIds.Resolve(key, parameter: null) is not null;
  }

  /// <summary>Whether a key passes every rule, so the generator registers the fan-out.</summary>
  internal static bool IsValidKey(IPropertySymbol key) => IsPhysicalField(key) && IsIndexed(key) && CanHoldStreamId(key);

  /// <summary>The key's column: its <c>[PhysicalField(ColumnName)]</c>, else its snake_case name, as the schema names it.</summary>
  internal static string ColumnName(IPropertySymbol key) =>
    _attribute(key, PHYSICAL_FIELD)!.NamedArguments.FirstOrDefault(static a => a.Key == "ColumnName").Value.Value as string
      ?? NamingConventionUtilities.ToSnakeCase(key.Name);

  private static ExpressionSyntax? _returned(PropertyDeclarationSyntax property) {
    if (property.ExpressionBody is { } expressionBody) {
      return expressionBody.Expression;
    }
    var getter = property.AccessorList!.Accessors.First(static a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
    return getter switch {
      { ExpressionBody: { } getterBody } => getterBody.Expression,
      { Body.Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
      { Body: null } => property.Initializer?.Value,
      _ => null,
    };
  }

  private static AttributeData? _attribute(ISymbol symbol, string name) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, name));
}
