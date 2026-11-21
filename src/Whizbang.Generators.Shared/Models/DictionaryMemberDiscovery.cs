// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Every closed dictionary type a perspective model holds, at any depth, for the generated registration of its stored
/// form (#1911).
/// </summary>
/// <remarks>
/// <para>
/// A dictionary in a perspective document is stored as an array of key/value objects, and both writers (the
/// serializer and Entity Framework's mapping) need its form registered by its closed type, because neither may
/// construct a generic type at run time. The author writes a plain <c>Dictionary</c>, <c>IDictionary</c> or
/// <c>IReadOnlyDictionary</c>; the generator registers each one it finds here.
/// </para>
/// <para>
/// The walk follows what the document stores: every stored property, own and inherited; the element of an array or a
/// collection; a nullable's underlying type; a dictionary's value, which may itself be or hold a dictionary. A member
/// the document leaves out (<c>[JsonIgnore]</c>, <c>[NotMapped]</c>) is not walked. Each type is walked once, so a
/// recursive model ends.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Generators.Tests/DictionaryMemberDiscoveryTests.cs</tests>
public static class DictionaryMemberDiscovery {
  private static readonly string[] _dictionaryDefinitions = [
    "System.Collections.Generic.Dictionary<TKey, TValue>",
    "System.Collections.Generic.IDictionary<TKey, TValue>",
    "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
  ];

  /// <summary>The closed dictionary types the model holds, as fully qualified key and value type names, once each.</summary>
  /// <param name="modelType">The perspective's model type, or null.</param>
  /// <returns>The key and value type of each, in the order the walk meets them.</returns>
  public static IReadOnlyList<(string KeyType, string ValueType)> From(INamedTypeSymbol? modelType) => _walk(modelType).Forms;

  /// <summary>
  /// Every property, on every type the model stores, whose declared type is a dictionary: the type that holds it (as
  /// a <c>typeof</c> names it), the property's name and its declared type.
  /// </summary>
  /// <param name="modelType">The perspective's model type, or null.</param>
  /// <returns>The members, once each, in the order the walk meets them.</returns>
  /// <remarks>
  /// What lets a context built without the Whizbang options extension map a dictionary inside a document: Entity
  /// Framework's model holds the type, and this names the member, so the generated configuration can add it as a value
  /// with its stored form, with no reflection over the type.
  /// </remarks>
  public static IReadOnlyList<(string OwnerType, string PropertyName, string MemberType)> MembersFrom(INamedTypeSymbol? modelType) =>
    _walk(modelType).Members;

  private sealed class Found {
    public List<(string KeyType, string ValueType)> Forms { get; } = [];
    public List<(string OwnerType, string PropertyName, string MemberType)> Members { get; } = [];
    public HashSet<ITypeSymbol> Visited { get; } = new(SymbolEqualityComparer.Default);
  }

  private static Found _walk(INamedTypeSymbol? modelType) {
    var found = new Found();
    if (modelType is not null) {
      _walkType(modelType, found);
    }
    return found;
  }

  /// <summary>Whether the type is one of the dictionary shapes a model declares.</summary>
  /// <param name="type">A type.</param>
  /// <returns>Whether it is <c>Dictionary</c>, <c>IDictionary</c> or <c>IReadOnlyDictionary</c> of two types.</returns>
  public static bool IsDictionary(ITypeSymbol type) =>
    type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } named
    && System.Array.IndexOf(_dictionaryDefinitions, TypeNameUtilities.Display(named.ConstructedFrom)) >= 0;

  private static void _walkType(ITypeSymbol type, Found found) {
    if (!found.Visited.Add(type)) {
      return;
    }

    if (type is IArrayTypeSymbol array) {
      _walkType(array.ElementType, found);
      return;
    }

    if (type is not INamedTypeSymbol named) {
      return;
    }

    if (IsDictionary(named)) {
      var pair = (TypeNameUtilities.FullyQualified(named.TypeArguments[0]), TypeNameUtilities.FullyQualified(named.TypeArguments[1]));
      if (!found.Forms.Contains(pair)) {
        found.Forms.Add(pair);
      }
      _walkType(named.TypeArguments[1], found);
      return;
    }

    // A collection, a nullable or any other generic System type: what it holds is what the document stores.
    var ns = TypeNameUtilities.DisplayOrEmpty(TypeNameUtilities.NamespaceOf(named));
    if (ns.StartsWith("System", System.StringComparison.Ordinal)) {
      foreach (var argument in named.TypeArguments) {
        _walkType(argument, found);
      }
      return;
    }

    foreach (var property in named.GetAllProperties()) {
      if (!PolymorphicModelDiscovery.IsAnalyzableProperty(property)) {
        continue;
      }
      if (IsDictionary(property.Type)) {
        found.Members.Add((TypeNameUtilities.FullyQualified(named), property.Name, TypeNameUtilities.FullyQualified(property.Type)));
      }
      _walkType(property.Type, found);
    }
  }
}
