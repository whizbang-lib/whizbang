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

  /// <summary>Finds the concrete subtypes of each of <paramref name="baseTypes"/> (fully qualified) in <paramref name="compilation"/>.</summary>
  public static ConcreteSubtypeIndex Build(Compilation compilation, IEnumerable<string> baseTypes) {
    var subtypes = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
    var examined = 0;
    foreach (var baseType in baseTypes.Distinct(System.StringComparer.Ordinal)) {
      var result = new List<string>();
      subtypes[baseType] = result;
      var baseSymbol = compilation.GetTypeByMetadataName(baseType.Replace("global::", ""));
      if (baseSymbol is not null) {
        _walkNamespace(compilation.GlobalNamespace, baseSymbol, result, ref examined);
      }
    }
    return new ConcreteSubtypeIndex(subtypes, examined);
  }

  private static void _walkNamespace(INamespaceSymbol ns, INamedTypeSymbol target, List<string> result, ref int examined) {
    foreach (var type in ns.GetTypeMembers()) {
      _checkTypeAndNestedTypes(type, target, result, ref examined);
    }
    foreach (var childNs in ns.GetNamespaceMembers()) {
      _walkNamespace(childNs, target, result, ref examined);
    }
  }

  private static void _checkTypeAndNestedTypes(INamedTypeSymbol type, INamedTypeSymbol target, List<string> result, ref int examined) {
    examined++;
    if (!type.IsAbstract && type.TypeKind == TypeKind.Class && _isSubtypeOf(type, target)) {
      result.Add(TypeNameHelper.GetFullyQualifiedName(type));
    }
    // Recurse into nested types (e.g., events nested inside static contract classes)
    foreach (var nested in type.GetTypeMembers()) {
      _checkTypeAndNestedTypes(nested, target, result, ref examined);
    }
  }

  // Whether type implements or inherits from target.
  private static bool _isSubtypeOf(INamedTypeSymbol type, INamedTypeSymbol target) {
    if (target.TypeKind == TypeKind.Interface) {
      return type.AllInterfaces.Contains(target, SymbolEqualityComparer.Default);
    }

    var current = type.BaseType;
    while (current is not null) {
      if (SymbolEqualityComparer.Default.Equals(current, target)) {
        return true;
      }
      current = current.BaseType;
    }
    return false;
  }
}
