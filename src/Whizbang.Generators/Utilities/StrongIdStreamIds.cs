// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Which <c>[StreamId]</c> properties hold a strong id, and which source id types a strong-id stream id may inherit
/// from (#2072). Shared by the stream-id generator, which emits the checks, and the analyzer, which reports misuse.
/// </summary>
/// <remarks>
/// A strong id is recognized three ways: the type carries <c>[WhizbangId]</c>, the type implements <c>IWhizbangId</c>
/// (an id compiled into a referenced assembly), or the property or record parameter carries <c>[WhizbangId]</c> (an id
/// the strong-id generator emits from that declaration). A generator cannot see a sibling generator's output, so in
/// the last case the type is unresolved there; it is named the way the strong-id generator names it.
/// </remarks>
/// <docs>extending/attributes/streamid#inheriting-a-stream-of-another-id-type</docs>
/// <tests>tests/Whizbang.Generators.Tests/StrongIdStreamInheritanceGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdInheritFromAnalyzerTests.cs</tests>
internal static class StrongIdStreamIds {
  private const string WHIZBANG_ID_ATTRIBUTE = "Whizbang.Core.WhizbangIdAttribute";
  private const string INHERIT_FROM = "InheritFrom";
  private const string NAMESPACE = "Namespace";

  /// <summary>A strong-id stream id: the id type as generated code names it, and whether it has a <c>From(Guid)</c>.</summary>
  /// <param name="TypeName">The id type, <c>global::</c> qualified.</param>
  /// <param name="HasGuidFactory">Whether the generated setter can build the id with <c>From(Guid)</c>.</param>
  internal sealed record StrongId(string TypeName, bool HasGuidFactory);

  /// <summary>
  /// The strong id a <c>[StreamId]</c> property holds, or null when it is not a strong id (a nullable strong id is not
  /// supported as a stream id, and is not one here). An id declared with <c>[WhizbangId]</c> (on the type, the property or the record parameter) is
  /// one the strong-id generator emits, with its <c>From(Guid)</c>; an id known only by <c>IWhizbangId</c> has one only
  /// if it declares it.
  /// </summary>
  /// <param name="property">The stream-id property.</param>
  /// <param name="parameter">The record parameter that carried <c>[StreamId]</c>, or null.</param>
  internal static StrongId? Resolve(IPropertySymbol property, IParameterSymbol? parameter) {
    var type = property.Type;
    var declared = _whizbangId(property) ?? (parameter is null ? null : _whizbangId(parameter));
    if (declared is not null) {
      var name = type.TypeKind == TypeKind.Error
        ? _discoveredName(type.Name, property, declared)
        : TypeNameUtilities.FullyQualified(type);
      return new StrongId(name, HasGuidFactory: true);
    }
    if (_whizbangId(type) is not null) {
      return new StrongId(TypeNameUtilities.FullyQualified(type), HasGuidFactory: true);
    }
    return TypeNameHelper.ImplementsInterface(type, StandardInterfaceNames.I_WHIZBANG_ID)
      ? new StrongId(TypeNameUtilities.FullyQualified(type), _declaresGuidFactory(type))
      : null;
  }

  /// <summary>Whether <paramref name="type"/> is a strong id: <c>[WhizbangId]</c> on it, or it implements <c>IWhizbangId</c>.</summary>
  internal static bool IsStrongIdType(ITypeSymbol type) =>
    _whizbangId(type) is not null || TypeNameHelper.ImplementsInterface(type, StandardInterfaceNames.I_WHIZBANG_ID);

  /// <summary>The types a <c>[StreamId]</c> lists in <c>InheritFrom</c>; empty when it lists none.</summary>
  internal static ImmutableArray<ITypeSymbol> InheritFrom(AttributeData streamIdAttribute) {
    var argument = streamIdAttribute.NamedArguments.FirstOrDefault(a => a.Key == INHERIT_FROM).Value;
    return argument.Kind == TypedConstantKind.Array
      ? [.. argument.Values.Select(v => v.Value).OfType<ITypeSymbol>()]
      : [];
  }

  /// <summary>
  /// The part of the generated setter's accept test that admits the listed types, one <c> || </c> clause each, or an
  /// empty string. A listed type the generator cannot resolve, because the strong-id generator emits it from a
  /// property's <c>[WhizbangId]</c>, is matched by name; the source type is always a strong id, so the name suffices.
  /// </summary>
  /// <param name="listed">The listed types, from <see cref="InheritFrom"/>.</param>
  /// <param name="ownType">The stream id's own type name, which is always accepted and is left out.</param>
  internal static string AcceptClauses(ImmutableArray<ITypeSymbol> listed, string ownType) {
    var clauses = new List<string>();
    foreach (var type in listed) {
      if (type.TypeKind == TypeKind.Error) {
        clauses.Add($" || sourceStreamIdType.Name == \"{type.Name}\"");
        continue;
      }
      var name = TypeNameUtilities.FullyQualified(type);
      if (name != ownType) {
        clauses.Add($" || sourceStreamIdType == typeof({name})");
      }
    }
    return string.Concat(clauses);
  }

  private static AttributeData? _whizbangId(ISymbol symbol) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, WHIZBANG_ID_ATTRIBUTE));

  /// <summary>Whether <paramref name="type"/> declares a static <c>From(Guid)</c> returning itself.</summary>
  private static bool _declaresGuidFactory(ITypeSymbol type) =>
    type.GetMembers("From").OfType<IMethodSymbol>().Any(m =>
      m.IsStatic && m.Parameters.Length == 1 && TypeNameUtilities.IsNamed(m.Parameters[0].Type, "System.Guid"));

  /// <summary>
  /// The name the strong-id generator gives an id it emits from <paramref name="declaredOn"/>'s <c>[WhizbangId]</c>:
  /// the namespace the attribute names (its constructor argument, else its <c>Namespace</c>), else the declaring
  /// type's namespace. It mirrors the strong-id generator's own rule, which a probe test holds it to.
  /// </summary>
  private static string _discoveredName(string typeName, ISymbol declaredOn, AttributeData attribute) {
    var ns = _nonBlank(attribute.ConstructorArguments.FirstOrDefault().Value)
      ?? _nonBlank(attribute.NamedArguments.FirstOrDefault(a => a.Key == NAMESPACE).Value.Value)
      ?? (declaredOn.ContainingNamespace.IsGlobalNamespace ? null : TypeNameUtilities.Display(declaredOn.ContainingNamespace));
    return ns is null ? $"global::{typeName}" : $"global::{ns}.{typeName}";
  }

  private static string? _nonBlank(object? value) => value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
