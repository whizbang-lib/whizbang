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

  /// <summary>
  /// The type's nearest <c>[Superseding]</c>, on the type or a base type (the attribute is inherited, and the nearest
  /// declaration wins, as attribute inheritance reads it): whether there is one, and the superseding group it names
  /// (#2124), null when it names none or names the empty string. The group is taken as written, never derived from a type
  /// name, since it is stored with every event. A type with no declaration walks up to System.Object.
  /// </summary>
  public static (bool IsDeclared, string? Group) Declaration(INamedTypeSymbol type) {
    for (var level = type; level is not null; level = level.BaseType) {
      var declaration = level.GetAttributes()
        .FirstOrDefault(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, SUPERSEDING_ATTRIBUTE));
      if (declaration is not null) {
        var group = declaration.NamedArguments.FirstOrDefault(a => a.Key == "Group").Value.Value as string;
        return (true, string.IsNullOrEmpty(group) ? null : group);
      }
    }
    return (false, null);
  }

  /// <summary>
  /// The properties marked <c>[SupersedingKey]</c>, on the type and its base types, nearest first, one per name: a
  /// property carrying the attribute, or the property a primary-constructor parameter carrying it declares.
  /// </summary>
  public static List<IPropertySymbol> KeyProperties(INamedTypeSymbol type) {
    var keys = new List<IPropertySymbol>();
    // Up to and including System.Object, which declares no key: one loop condition, so nothing is special-cased.
    for (var level = type; level is not null; level = level.BaseType) {
      var properties = level.GetMembers().OfType<IPropertySymbol>().ToList();
      var marked = properties.Where(_isKey)
        .Concat(level.InstanceConstructors.SelectMany(c => c.Parameters).Where(_isKey)
          .Select(p => properties.FirstOrDefault(q => string.Equals(q.Name, p.Name, System.StringComparison.OrdinalIgnoreCase)))
          .OfType<IPropertySymbol>());
      keys.AddRange(marked.Where(property => !keys.Exists(known => known.Name == property.Name)));
    }
    return keys;
  }

  /// <summary>The names of <see cref="KeyProperties"/>, nearest first.</summary>
  public static List<string> KeyNames(INamedTypeSymbol type) => [.. KeyProperties(type).Select(p => p.Name)];

  /// <summary>
  /// The superseding registration the generator emits: whether the type is superseding (an event, declared, not
  /// ephemeral, at most one key), its key property or null for one key per stream, the key's <c>[JsonPropertyName]</c>
  /// (#2123) and its group (#2124). A type the analyzer refuses (WHIZ135, WHIZ136) is not superseding, so a suppressed
  /// error keeps every event.
  /// </summary>
  public static SupersedingRegistration Resolve(INamedTypeSymbol type) {
    var (declared, group) = Declaration(type);
    if (!declared || EphemeralResolver.IsEphemeral(type)) {
      return default;
    }
    var keys = KeyProperties(type);
    return keys.Count switch {
      0 => new SupersedingRegistration(true, null, null, group),
      1 => new SupersedingRegistration(true, keys[0].Name, KeyJsonName(keys[0]), group),
      _ => default,
    };
  }

  private const string JSON_PROPERTY_NAME_ATTRIBUTE = "global::System.Text.Json.Serialization.JsonPropertyNameAttribute";

  /// <summary>
  /// The <c>[JsonPropertyName]</c> of a key property, read from the declaration <see cref="KeyProperties"/> found (the
  /// nearest, as System.Text.Json reads it), or null when it has none (#2123): the name the framework's generated JSON
  /// contexts write the key under, so a payload missing the key can be told from one carrying a default value.
  /// </summary>
  public static string? KeyJsonName(IPropertySymbol property) =>
    property.GetAttributes()
      .FirstOrDefault(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, JSON_PROPERTY_NAME_ATTRIBUTE))
      ?.ConstructorArguments[0].Value as string;

  private static bool _isKey(ISymbol symbol) =>
    symbol.GetAttributes().Any(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, KEY_ATTRIBUTE));
}

/// <summary>
/// What the catalog generator registers for a superseding type: whether it is superseding, its key property (null for one
/// key per stream), the key's own <c>[JsonPropertyName]</c> and its superseding group. The default is not superseding.
/// </summary>
internal readonly record struct SupersedingRegistration(bool IsSuperseding, string? Key, string? KeyJsonName, string? Group);
