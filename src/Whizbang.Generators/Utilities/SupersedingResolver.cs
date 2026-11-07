// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Resolves a type's superseding declaration (<c>[Superseding]</c>, <c>[SupersedingKey]</c>, #1729) at compile time: the
/// single source of truth shared by the catalog generator (which registers the type and its key accessor) and the
/// analyzer (which reports WHIZ135 and WHIZ136), so the two can never disagree.
/// </summary>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_SupersedingEvent_StampsTheEntryAndRegistersTheKeyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/SupersedingEventAnalyzerTests.cs</tests>
internal static class SupersedingResolver {
  private const string SUPERSEDING_ATTRIBUTE = "global::Whizbang.Core.Attributes.SupersedingAttribute";
  private const string KEY_ATTRIBUTE = "global::Whizbang.Core.Attributes.SupersedingKeyAttribute";

  /// <summary><c>[Superseding]</c> on the type or a base type (the attribute is inherited).</summary>
  public static bool IsDeclared(INamedTypeSymbol type) {
    for (var level = type; level is not null; level = level.BaseType) {
      if (level.GetAttributes().Any(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, SUPERSEDING_ATTRIBUTE))) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// The properties marked <c>[SupersedingKey]</c>, on the type and its base types, nearest first, one per name: a
  /// property carrying the attribute, or the property a primary-constructor parameter carrying it declares.
  /// </summary>
  public static List<string> KeyNames(INamedTypeSymbol type) {
    var names = new List<string>();
    // Up to and including System.Object, which declares no key: one loop condition, so nothing is special-cased.
    for (var level = type; level is not null; level = level.BaseType) {
      var properties = level.GetMembers().OfType<IPropertySymbol>().ToList();
      var marked = properties.Where(_isKey)
        .Concat(level.InstanceConstructors.SelectMany(c => c.Parameters).Where(_isKey)
          .Select(p => properties.FirstOrDefault(q => string.Equals(q.Name, p.Name, System.StringComparison.OrdinalIgnoreCase)))
          .OfType<IPropertySymbol>());
      foreach (var property in marked.Where(property => !names.Contains(property.Name))) {
        names.Add(property.Name);
      }
    }
    return names;
  }

  /// <summary>
  /// The superseding registration the generator emits: whether the type is superseding (an event, declared, not
  /// ephemeral, at most one key) and its key property, or null for one key per stream. A type the analyzer refuses
  /// (WHIZ135, WHIZ136) is not superseding, so a suppressed error keeps every event.
  /// </summary>
  public static (bool IsSuperseding, string? Key) Resolve(INamedTypeSymbol type) {
    if (!IsDeclared(type) || EphemeralResolver.IsEphemeral(type)) {
      return (false, null);
    }
    var keys = KeyNames(type);
    return keys.Count switch {
      0 => (true, null),
      1 => (true, keys[0]),
      _ => (false, null),
    };
  }

  private const string JSON_PROPERTY_NAME_ATTRIBUTE = "global::System.Text.Json.Serialization.JsonPropertyNameAttribute";

  /// <summary>
  /// The <c>[JsonPropertyName]</c> of the key property <paramref name="key"/>, read from its nearest declaration as
  /// System.Text.Json reads it, or null when it has none (#2123): the name the framework's generated JSON contexts write
  /// the key under, so a payload missing the key can be told from one carrying a default value.
  /// </summary>
  /// <remarks><paramref name="key"/> is a name <see cref="KeyNames"/> returned, so the type or a base type declares it.</remarks>
  public static string? KeyJsonName(INamedTypeSymbol type, string key) {
    var property = _levels(type).SelectMany(level => level.GetMembers(key)).OfType<IPropertySymbol>().First();
    return property.GetAttributes()
      .FirstOrDefault(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, JSON_PROPERTY_NAME_ATTRIBUTE))
      ?.ConstructorArguments[0].Value as string;
  }

  private static IEnumerable<INamedTypeSymbol> _levels(INamedTypeSymbol type) {
    for (var level = type; level is not null; level = level.BaseType) {
      yield return level;
    }
  }

  private static bool _isKey(ISymbol symbol) =>
    symbol.GetAttributes().Any(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, KEY_ATTRIBUTE));
}
