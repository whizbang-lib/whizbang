// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// What one <c>[WhizbangId]</c> declaration declares: the id's type name, the namespace it is generated into and its
/// pinned id (#2604). Read by <see cref="WhizbangIdDeclaration"/>, the one reader the strong id generator, the pinned-id
/// registry, the pinned-type ledger and the analyzers share, so they cannot disagree on what a declaration pins.
/// </summary>
/// <param name="TypeName">The id type's simple name.</param>
/// <param name="Namespace">The namespace the id is generated into; empty for the global namespace.</param>
/// <param name="RawPinnedId">The pinned id as written, or null when the declaration pins nothing.</param>
/// <param name="PinLocation">Where the pin is written, or where <c>[WhizbangId]</c> is when nothing is pinned.</param>
/// <param name="ConflictingRawPinnedId">
/// A second, different pin on the same declaration (a struct with both <c>[PinnedId]</c> and
/// <c>[WhizbangId(PinnedId = ...)]</c>), or null.
/// </param>
internal sealed record DeclaredStrongId(
  string TypeName,
  string Namespace,
  string? RawPinnedId,
  Location PinLocation,
  string? ConflictingRawPinnedId) {
  /// <summary>The id type's name with its namespace: the CLR form for the top-level type the generator emits.</summary>
  public string FullyQualifiedName => Namespace.Length == 0 ? TypeName : $"{Namespace}.{TypeName}";

  /// <summary>The pinned id in its lowercase hyphenated form, or null when there is none or it is not a GUID.</summary>
  public string? PinnedId => WhizbangIdDeclaration.NormalizePin(RawPinnedId);
}

/// <summary>Reads <c>[WhizbangId]</c> declarations on a struct, a property or a parameter.</summary>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdDeriveFromTests.cs</tests>
internal static class WhizbangIdDeclaration {
  private const string PINNED_ID_PROPERTY = "PinnedId";

  /// <summary>Whether an attribute is <c>[WhizbangId]</c>, matched by its class's simple name as the generator always has.</summary>
  internal static bool IsWhizbangIdAttribute(AttributeData attribute) =>
    TypeNameUtilities.SimpleNameOrNull(attribute.AttributeClass) is "WhizbangIdAttribute" or "WhizbangId";

  /// <summary>
  /// Reads the id <paramref name="symbol"/> declares: a struct carrying <c>[WhizbangId]</c>, or a property or parameter
  /// carrying it on its type. Null when the symbol carries no <c>[WhizbangId]</c>.
  /// </summary>
  internal static DeclaredStrongId? Read(ISymbol symbol, System.Threading.CancellationToken cancellationToken = default) {
    var attribute = symbol.GetAttributes().FirstOrDefault(IsWhizbangIdAttribute);
    return attribute is null ? null : FromAttribute(symbol, attribute, cancellationToken);
  }

  /// <summary>
  /// Reads the id <paramref name="symbol"/> declares through its <c>[WhizbangId]</c> <paramref name="attribute"/>, for a
  /// caller that has already found the attribute.
  /// </summary>
  internal static DeclaredStrongId FromAttribute(
      ISymbol symbol, AttributeData attribute, System.Threading.CancellationToken cancellationToken = default) {
    // A property or a parameter declares the id of its type; a struct declares itself
    var typeName = TypeNameUtilities.SimpleName(symbol switch {
      IPropertySymbol property => property.Type,
      IParameterSymbol parameter => parameter.Type,
      _ => symbol,
    });

    var attributeLocation = LocationUtilities.ApplicationOrFallback(attribute, Location.None, cancellationToken);
    var declaredPin = _namedString(attribute, PINNED_ID_PROPERTY);
    var pinnedIdAttribute = symbol is INamedTypeSymbol
      ? symbol.GetAttributes().FirstOrDefault(a =>
          TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, StandardInterfaceNames.PINNED_ID_ATTRIBUTE))
      : null;
    var attributePin = pinnedIdAttribute is { ConstructorArguments.Length: > 0 } &&
                       pinnedIdAttribute.ConstructorArguments[0].Value is string value && !string.IsNullOrWhiteSpace(value)
      ? value
      : null;

    // [PinnedId] is the pin when both are written; the other one only conflicts when it names a different id
    var raw = attributePin ?? declaredPin;
    var pinLocation = attributePin is not null
      ? LocationUtilities.ApplicationOrFallback(pinnedIdAttribute!, attributeLocation, cancellationToken)
      : attributeLocation;
    var conflict = attributePin is not null && declaredPin is not null &&
                   !string.Equals(NormalizePin(attributePin) ?? attributePin, NormalizePin(declaredPin) ?? declaredPin, System.StringComparison.Ordinal)
      ? declaredPin
      : null;

    return new DeclaredStrongId(typeName, TargetNamespace(attribute, symbol), raw, pinLocation, conflict);
  }

  /// <summary>
  /// The ids a type declares, for an analyzer that visits named types: the type itself, and every property and
  /// parameter of its members that carries <c>[WhizbangId]</c>.
  /// </summary>
  internal static IEnumerable<DeclaredStrongId> ReadAll(INamedTypeSymbol type, System.Threading.CancellationToken cancellationToken) {
    if (Read(type, cancellationToken) is { } own) {
      yield return own;
    }
    foreach (var member in type.GetMembers()) {
      if (member is IPropertySymbol property && Read(property, cancellationToken) is { } fromProperty) {
        yield return fromProperty;
      } else if (member is IMethodSymbol method) {
        foreach (var parameter in method.Parameters) {
          if (Read(parameter, cancellationToken) is { } fromParameter) {
            yield return fromParameter;
          }
        }
      }
    }
  }

  /// <summary>
  /// The namespace a declaration's id is generated into: <c>Namespace</c> or the constructor argument of
  /// <c>[WhizbangId]</c>, else the declaring symbol's namespace, or none for the global namespace (#1174).
  /// </summary>
  internal static string TargetNamespace(AttributeData attribute, ISymbol declaring) {
    var targetNamespace = declaring.ContainingNamespace.IsGlobalNamespace ? "" : TypeNameUtilities.Display(declaring.ContainingNamespace);
    if (_namedString(attribute, "Namespace") is { } customNamespace) {
      targetNamespace = customNamespace;
    }
    if (attribute.ConstructorArguments.Length > 0 &&
        attribute.ConstructorArguments[0].Value is string constructorNamespace &&
        !string.IsNullOrWhiteSpace(constructorNamespace)) {
      targetNamespace = constructorNamespace;
    }
    return targetNamespace;
  }

  /// <summary>The pin in its lowercase hyphenated form, or null when there is none or it is not a GUID.</summary>
  internal static string? NormalizePin(string? raw) =>
    raw is not null && System.Guid.TryParse(raw, out var pin) ? pin.ToString("D") : null;

  private static string? _namedString(AttributeData attribute, string name) =>
    attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == name).Value.Value is string text && !string.IsNullOrWhiteSpace(text)
      ? text
      : null;
}
