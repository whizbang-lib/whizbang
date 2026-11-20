// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// The concrete subtypes of each polymorphic message type a receptor handles: every non-abstract class in the
/// compilation and its references that derives from the type or implements it, in the order the compilation lists
/// them. The receptor generator widens a receptor declared on an interface or base type to these concrete types.
/// </summary>
/// <remarks>
/// Public rather than internal because InternalsVisibleTo on this assembly collides with the test project's polyfills.
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/ConcreteSubtypeIndexTests.cs</tests>
public sealed class ConcreteSubtypeIndex {
  private readonly Dictionary<string, List<string>> _subtypes;

  private ConcreteSubtypeIndex(Dictionary<string, List<string>> subtypes, int typesExamined) {
    _subtypes = subtypes;
    TypesExamined = typesExamined;
  }

  /// <summary>
  /// How many types the index examined to answer every base type it was built for: the cost of building it, which
  /// the build-cost tests pin (#1690).
  /// </summary>
  public int TypesExamined { get; }

  /// <summary>
  /// The concrete subtypes of <paramref name="baseType"/> (fully qualified), in discovery order; empty for a base type
  /// the index was not built for or the compilation cannot resolve.
  /// </summary>
  public IReadOnlyList<string> SubtypesOf(string baseType) =>
      _subtypes.TryGetValue(baseType, out var subtypes) ? subtypes : [];

  /// <summary>
  /// Finds the concrete subtypes of each of <paramref name="baseTypes"/> (fully qualified) in <paramref name="compilation"/>,
  /// in one walk of its types: each concrete class's base classes and interfaces are looked up among the base types, so
  /// the cost is the number of types, whatever the number of base types (#1690).
  /// </summary>
  public static ConcreteSubtypeIndex Build(Compilation compilation, IEnumerable<string> baseTypes) {
    var subtypes = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
    var targets = new Dictionary<ISymbol, List<string>>(SymbolEqualityComparer.Default);
    foreach (var baseType in baseTypes.Distinct(System.StringComparer.Ordinal)) {
      var result = new List<string>();
      subtypes[baseType] = result;
      // Two names that resolve to one symbol share its list, as two walks for the same symbol found the same types.
      if (compilation.GetTypeByMetadataName(baseType.Replace("global::", "")) is { } baseSymbol) {
        if (targets.TryGetValue(baseSymbol, out var shared)) {
          subtypes[baseType] = shared;
        } else {
          targets[baseSymbol] = result;
        }
      }
    }
    var examined = 0;
    if (targets.Count > 0) {
      _walkNamespace(compilation.GlobalNamespace, targets, ref examined);
    }
    return new ConcreteSubtypeIndex(subtypes, examined);
  }

  private static void _walkNamespace(INamespaceSymbol ns, Dictionary<ISymbol, List<string>> targets, ref int examined) {
    foreach (var type in ns.GetTypeMembers()) {
      _checkTypeAndNestedTypes(type, targets, ref examined);
    }
    foreach (var childNs in ns.GetNamespaceMembers()) {
      _walkNamespace(childNs, targets, ref examined);
    }
  }

  private static void _checkTypeAndNestedTypes(INamedTypeSymbol type, Dictionary<ISymbol, List<string>> targets, ref int examined) {
    examined++;
    if (!type.IsAbstract && type.TypeKind == TypeKind.Class) {
      _addToEachBaseType(type, targets);
    }
    // Recurse into nested types (e.g., events nested inside static contract classes)
    foreach (var nested in type.GetTypeMembers()) {
      _checkTypeAndNestedTypes(nested, targets, ref examined);
    }
  }

  // Adds the concrete type to the list of every base type it implements or inherits from. Each interface and each base
  // class is met once, so the type is added to a list at most once.
  private static void _addToEachBaseType(INamedTypeSymbol type, Dictionary<ISymbol, List<string>> targets) {
    string? name = null;
    foreach (var implemented in type.AllInterfaces) {
      if (targets.TryGetValue(implemented, out var list)) {
        list.Add(name ??= TypeNameHelper.GetFullyQualifiedName(type));
      }
    }
    for (var current = type.BaseType; current is not null; current = current.BaseType) {
      if (targets.TryGetValue(current, out var list)) {
        list.Add(name ??= TypeNameHelper.GetFullyQualifiedName(type));
      }
    }
  }
}
