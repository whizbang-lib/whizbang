using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Resolves a type's effective ephemeral mode from <c>[Ephemeral]</c> at compile time — the single
/// source of truth shared by the catalog generator (which stamps the mode) and the analyzer (which
/// enforces the virality rules), so the two can never disagree.
/// </summary>
/// <remarks>
/// The CLR only honours <c>AttributeUsage.Inherited</c> for base classes via reflection and never for
/// interfaces, so the walk is explicit and generator-driven: own type → base records → implemented
/// interfaces, <strong>most-specific wins</strong>. Interfaces are name-ordered so results are
/// deterministic; genuine sibling-profile conflicts are reported separately (WHIZ134).
/// </remarks>
internal static class EphemeralResolver {
  private const string EPHEMERAL_ATTR = "global::Whizbang.Core.Attributes.EphemeralAttribute";

  /// <summary>True when the type resolves to ephemeral (directly or by composition).</summary>
  public static bool IsEphemeral(ITypeSymbol? type) =>
    type is INamedTypeSymbol named && _find(named) is not null;

  /// <summary>
  /// The resolved (Destruction, Storage) enum-member names, or null when the type is Sourced.
  /// </summary>
  public static (string Destruction, string Storage)? Resolve(INamedTypeSymbol type) {
    var attr = _find(type);
    return attr is null
      ? null
      : (_enumArgName(attr, "Destruction", "WhenConsumed"), _enumArgName(attr, "Storage", "InMemory"));
  }

  /// <summary>
  /// The type's <c>[Ephemeral(RewindGraceSeconds = …)]</c> override in seconds, or <c>-1</c> when unset
  /// (inherit the global default) or when the type is not ephemeral. Resolved from the same own → base →
  /// interface walk as <see cref="Resolve"/>.
  /// </summary>
  public static int ResolveRewindGraceSeconds(INamedTypeSymbol type) {
    var attr = _find(type);
    return attr is null ? -1 : _intArgName(attr, "RewindGraceSeconds", -1);
  }

  /// <summary>
  /// True when a type's ephemeral mode is <em>ambiguously composed</em> — it has no own or base
  /// <c>[Ephemeral]</c> to settle the tie, yet implements two or more sibling profile interfaces that
  /// carry <c>[Ephemeral]</c> with <em>different</em> configs. A refined profile that extends another
  /// (more-specific) is NOT ambiguous; only unrelated siblings that disagree are. The developer resolves
  /// it by declaring an explicit <c>[Ephemeral]</c> on the type. (WHIZ134.)
  /// </summary>
  public static bool IsAmbiguousComposition(INamedTypeSymbol type) {
    if (_on(type) is not null) {
      return false;
    }
    for (var b = type.BaseType; b is not null; b = b.BaseType) {
      if (_on(b) is not null) {
        return false;
      }
    }

    var carriers = new List<(INamedTypeSymbol Iface, string Destruction, string Storage)>();
    foreach (var iface in type.AllInterfaces) {
      var a = _on(iface);
      if (a is not null) {
        carriers.Add((iface, _enumArgName(a, "Destruction", "WhenConsumed"), _enumArgName(a, "Storage", "InMemory")));
      }
    }
    if (carriers.Count < 2) {
      return false;
    }

    // Keep only the most-derived carriers: drop any interface that another carrier extends.
    var maximal = carriers.Where(c => !carriers.Any(o =>
      !SymbolEqualityComparer.Default.Equals(o.Iface, c.Iface)
      && o.Iface.AllInterfaces.Contains(c.Iface, SymbolEqualityComparer.Default))).ToList();

    return maximal.Select(c => (c.Destruction, c.Storage)).Distinct().Count() > 1;
  }

  private static AttributeData? _find(INamedTypeSymbol type) {
    var attr = _on(type);
    for (var b = type.BaseType; b is not null && attr is null; b = b.BaseType) {
      attr = _on(b);
    }
    if (attr is null) {
      foreach (var iface in type.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal)) {
        attr = _on(iface);
        if (attr is not null) {
          break;
        }
      }
    }
    return attr;
  }

  private static AttributeData? _on(INamedTypeSymbol type) =>
    type.GetAttributes().FirstOrDefault(a =>
      a.AttributeClass is not null &&
      TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == EPHEMERAL_ATTR);

  private static int _intArgName(AttributeData attr, string propertyName, int defaultValue) {
    foreach (var na in attr.NamedArguments) {
      if (na.Key == propertyName && na.Value.Value is int v) {
        return v;
      }
    }
    return defaultValue;
  }

  private static string _enumArgName(AttributeData attr, string propertyName, string defaultMember) {
    foreach (var na in attr.NamedArguments) {
      if (na.Key == propertyName
          && na.Value.Type is INamedTypeSymbol enumType
          && na.Value.Value is not null) {
        var member = enumType.GetMembers()
          .OfType<IFieldSymbol>()
          .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, na.Value.Value));
        if (member is not null) {
          return member.Name;
        }
      }
    }
    return defaultMember;
  }
}
