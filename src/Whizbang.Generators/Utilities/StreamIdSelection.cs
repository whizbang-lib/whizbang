// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Decides which <c>[StreamId]</c> is a type's stream id, once, for the generators and the analyzer (#1512, #1833).
/// The type and then each base type are searched, nearest first. At each level a <c>[StreamId(Override = true)]</c>,
/// on a property or on a record's primary-constructor parameter, is taken first, then a plain <c>[StreamId]</c>
/// property. Only when no level has either is the type's own plain <c>[StreamId]</c> constructor parameter taken.
/// </summary>
/// <docs>extending/attributes/streamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdOverrideGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdAttributeAnalyzerTests.cs</tests>
internal static class StreamIdSelection {
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";
  private const string OVERRIDE = "Override";

  /// <summary>
  /// One <c>[StreamId]</c> a type declares itself. <see cref="Property"/> is the property the generated code reads,
  /// null for a constructor parameter no property matches; <see cref="Parameter"/> is set when the attribute is on a
  /// constructor parameter.
  /// </summary>
  internal sealed record Declaration(string Name, AttributeData Attribute, IPropertySymbol? Property, IParameterSymbol? Parameter) {
    /// <summary>Whether the attribute says <c>Override = true</c>.</summary>
    public bool IsOverride { get; } = Attribute.NamedArguments.Any(a => a.Key == OVERRIDE && a.Value.Value is true);
  }

  /// <summary>
  /// The chosen stream id: the property generated code reads, the constructor parameter that carried the attribute
  /// when it was one, and whether it is an override.
  /// </summary>
  internal sealed record Choice(IPropertySymbol Property, IParameterSymbol? Parameter, bool IsOverride);

  /// <summary>
  /// Each <c>[StreamId]</c> the type declares itself: properties in member order, then constructor parameters, one
  /// per name (a record parameter and its property count once, the property first).
  /// </summary>
  internal static List<Declaration> Declared(INamedTypeSymbol type) {
    var properties = type.GetMembers().OfType<IPropertySymbol>().ToList();
    return properties
      .Select(p => (Symbol: (ISymbol)p, Attribute: _streamIdAttribute(p)))
      .Concat(type.InstanceConstructors.SelectMany(c => c.Parameters)
        .Select(p => (Symbol: (ISymbol)p, Attribute: _streamIdAttribute(p))))
      .Where(d => d.Attribute is not null)
      .Select(d => d.Symbol is IPropertySymbol property
        ? new Declaration(property.Name, d.Attribute!, property, null)
        : new Declaration(d.Symbol.Name, d.Attribute!,
            properties.FirstOrDefault(p => string.Equals(p.Name, d.Symbol.Name, System.StringComparison.OrdinalIgnoreCase)),
            (IParameterSymbol)d.Symbol))
      .GroupBy(d => d.Name, System.StringComparer.OrdinalIgnoreCase)
      .Select(g => g.First())
      .ToList();
  }

  /// <summary>The stream id the generators use for <paramref name="type"/>, or null when it has none.</summary>
  internal static Choice? Select(INamedTypeSymbol type) =>
    SelectInherited(type, includeSelf: true)
      ?? _toChoice(Declared(type).FirstOrDefault(d => d.Parameter is not null && d.Property is not null));

  /// <summary>
  /// The stream id found by searching the levels only, without the type's own plain constructor parameter: from the
  /// type itself when <paramref name="includeSelf"/>, else from its base type (what the type inherits).
  /// </summary>
  internal static Choice? SelectInherited(INamedTypeSymbol type, bool includeSelf) {
    for (var level = includeSelf ? type : type.BaseType;
        level is not null && level.SpecialType != SpecialType.System_Object;
        level = level.BaseType) {
      var declared = Declared(level);
      var chosen = declared.FirstOrDefault(d => d.IsOverride && d.Property is not null)
        ?? declared.FirstOrDefault(d => d.Parameter is null);
      if (chosen is not null) {
        return _toChoice(chosen);
      }
    }
    return null;
  }

  /// <summary>Whether any base type of <paramref name="type"/> declares a <c>[StreamId]</c>, on a property or a parameter.</summary>
  internal static bool AnyInherited(INamedTypeSymbol type) {
    for (var level = type.BaseType; level is not null && level.SpecialType != SpecialType.System_Object; level = level.BaseType) {
      if (Declared(level).Count > 0) {
        return true;
      }
    }
    return false;
  }

  private static Choice? _toChoice(Declaration? declaration) =>
    declaration is null ? null : new Choice(declaration.Property!, declaration.Parameter, declaration.IsOverride);

  private static AttributeData? _streamIdAttribute(ISymbol symbol) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID_ATTRIBUTE));
}
