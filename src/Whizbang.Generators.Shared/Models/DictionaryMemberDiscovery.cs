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
  public static IReadOnlyList<(string KeyType, string ValueType)> From(INamedTypeSymbol? modelType) {
    var found = new List<(string KeyType, string ValueType)>();
    if (modelType is not null) {
      _walkType(modelType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), found);
    }
    return found;
  }

  /// <summary>Whether the type is one of the dictionary shapes a model declares.</summary>
  /// <param name="type">A type.</param>
  /// <returns>Whether it is <c>Dictionary</c>, <c>IDictionary</c> or <c>IReadOnlyDictionary</c> of two types.</returns>
  public static bool IsDictionary(ITypeSymbol type) =>
    type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } named
    && System.Array.IndexOf(_dictionaryDefinitions, TypeNameUtilities.Display(named.ConstructedFrom)) >= 0;

  private static void _walkType(ITypeSymbol type, HashSet<ITypeSymbol> visited, List<(string KeyType, string ValueType)> found) {
    if (!visited.Add(type)) {
      return;
    }

    if (type is IArrayTypeSymbol array) {
      _walkType(array.ElementType, visited, found);
      return;
    }

    if (type is not INamedTypeSymbol named) {
      return;
    }

    if (IsDictionary(named)) {
      var pair = (TypeNameUtilities.FullyQualified(named.TypeArguments[0]), TypeNameUtilities.FullyQualified(named.TypeArguments[1]));
      if (!found.Contains(pair)) {
        found.Add(pair);
      }
      _walkType(named.TypeArguments[1], visited, found);
      return;
    }

    // A collection, a nullable or any other generic System type: what it holds is what the document stores.
    var ns = TypeNameUtilities.DisplayOrEmpty(TypeNameUtilities.NamespaceOf(named));
    if (ns.StartsWith("System", System.StringComparison.Ordinal)) {
      foreach (var argument in named.TypeArguments) {
        _walkType(argument, visited, found);
      }
      return;
    }

    foreach (var property in named.GetAllProperties()) {
      if (PolymorphicModelDiscovery.IsAnalyzableProperty(property)) {
        _walkType(property.Type, visited, found);
      }
    }
  }
}
