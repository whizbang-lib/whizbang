// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
  /// empty string. Each clause compares the source type with <c>typeof</c> the listed type, so only that type passes,
  /// never another id of the same simple name.
  /// </summary>
  /// <remarks>
  /// A listed type the generator cannot resolve is an id the strong-id generator emits from a property's or record
  /// parameter's <c>[WhizbangId]</c>, which a sibling generator cannot see. It is named the way the strong-id generator
  /// names it (<see cref="_discoveredName"/>), then the attribute's <c>typeof</c> is bound again with those ids
  /// declared, so it resolves by the C# rules the final compilation applies: qualified, through an alias or a using, or
  /// from the id's own namespace. A listed type that still does not resolve (no such id, or two in scope) adds no
  /// clause, so it is refused rather than matched loosely.
  /// </remarks>
  /// <param name="streamIdAttribute">The <c>[StreamId]</c>, whose <c>InheritFrom</c> lists the types.</param>
  /// <param name="ownType">The stream id's own type name, which is always accepted and is left out.</param>
  /// <param name="compilation">The compilation the attribute is in, which declares the strong-id generator's ids.</param>
  internal static string AcceptClauses(AttributeData streamIdAttribute, string ownType, Compilation compilation) {
    var listed = InheritFrom(streamIdAttribute);
    var resolved = listed.Where(t => t.TypeKind != TypeKind.Error).Select(TypeNameUtilities.FullyQualified);
    var discovered = listed.Any(t => t.TypeKind == TypeKind.Error)
      ? _listedDiscoveredIds(streamIdAttribute, compilation)
      : [];
    return string.Concat(resolved.Concat(discovered)
      .Where(name => name != ownType)
      .Select(name => $" || sourceStreamIdType == typeof({name})"));
  }

  /// <summary>
  /// The listed types the compilation cannot resolve, each bound again in a copy of the compilation that declares the
  /// ids the strong-id generator will emit under their names; the <c>global::</c> name of each that then resolves.
  /// </summary>
  private static IEnumerable<string> _listedDiscoveredIds(AttributeData streamIdAttribute, Compilation compilation) {
    // A source [StreamId] always has its syntax, and InheritFrom is the only Type it takes
    var attribute = streamIdAttribute.ApplicationSyntaxReference!.GetSyntax();
    var tree = attribute.SyntaxTree;
    var model = compilation.GetSemanticModel(tree);
    var unresolved = new List<TypeSyntax>();
    var names = new HashSet<string>();
    foreach (var listed in attribute.DescendantNodes().OfType<TypeOfExpressionSyntax>()) {
      if (model.GetTypeInfo(listed.Type).Type is IErrorTypeSymbol error) {
        unresolved.Add(listed.Type);
        names.Add(error.Name);
      }
    }
    var declarations = new List<SyntaxTree>();
    foreach (var id in new HashSet<(string? Namespace, string Name)>(_discoveredIds(compilation, names))) {
      declarations.Add(CSharpSyntaxTree.ParseText(_declaration(id), (CSharpParseOptions)tree.Options));
    }
    var withIds = compilation.AddSyntaxTrees(declarations).GetSemanticModel(tree);
    foreach (var listed in unresolved) {
      // A type syntax always binds to a type, an error type when it does not resolve
      var type = withIds.GetTypeInfo(listed).Type!;
      if (type is not IErrorTypeSymbol) {
        yield return TypeNameUtilities.FullyQualified(type);
      }
    }
  }

  /// <summary>
  /// The ids, named in <paramref name="names"/>, that the strong-id generator emits from a <c>[WhizbangId]</c> on a
  /// property or a parameter whose type does not exist yet: the namespace each lands in and its name.
  /// </summary>
  private static IEnumerable<(string? Namespace, string Name)> _discoveredIds(Compilation compilation, HashSet<string> names) {
    foreach (var tree in compilation.SyntaxTrees) {
      var model = compilation.GetSemanticModel(tree);
      var nodes = tree.GetRoot().DescendantNodes();
      var properties = nodes.OfType<PropertyDeclarationSyntax>().Where(p => p.AttributeLists.Any())
        .Select(p => model.GetDeclaredSymbol(p)).OfType<IPropertySymbol>().Select(p => ((ISymbol)p, p.Type));
      var parameters = nodes.OfType<ParameterSyntax>().Where(p => p.AttributeLists.Any())
        .Select(p => model.GetDeclaredSymbol(p)).OfType<IParameterSymbol>().Select(p => ((ISymbol)p, p.Type));
      foreach (var (symbol, type) in properties.Concat(parameters)) {
        if (type.TypeKind == TypeKind.Error && names.Contains(type.Name) && _whizbangId(symbol) is { } attribute) {
          yield return (_discoveredNamespace(symbol, attribute), type.Name);
        }
      }
    }
  }

  /// <summary>A placeholder declaration of a discovered id, enough for the <c>typeof</c> that lists it to bind.</summary>
  private static string _declaration((string? Namespace, string Name) id) =>
    id.Namespace is null
      ? $"internal struct {id.Name} {{ }}"
      : $"namespace {id.Namespace} {{ internal struct {id.Name} {{ }} }}";

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
  private static string _discoveredName(string typeName, ISymbol declaredOn, AttributeData attribute) =>
    _discoveredNamespace(declaredOn, attribute) is { } ns ? $"global::{ns}.{typeName}" : $"global::{typeName}";

  /// <summary>The namespace of an id the strong-id generator emits (see <see cref="_discoveredName"/>), or null for the global one.</summary>
  private static string? _discoveredNamespace(ISymbol declaredOn, AttributeData attribute) =>
    _nonBlank(attribute.ConstructorArguments.FirstOrDefault().Value)
      ?? _nonBlank(attribute.NamedArguments.FirstOrDefault(a => a.Key == NAMESPACE).Value.Value)
      ?? (declaredOn.ContainingNamespace.IsGlobalNamespace ? null : TypeNameUtilities.Display(declaredOn.ContainingNamespace));

  private static string? _nonBlank(object? value) => value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
