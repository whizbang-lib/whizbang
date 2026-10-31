// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// What a perspective class's interfaces promise (#1936). The perspective interfaces are the strongly-typed contract the
/// compiler checks and the runner routes by: for each model, the events listed as type arguments are the events the
/// runner delivers. Nothing is ever routed by a method's name alone, so the analyzers hold every <c>Apply</c> on the
/// class to this contract.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveUnlistedApplyTests.cs</tests>
internal sealed class PerspectiveContract {
  internal const string APPLY = "Apply";
  private const string EVENT_INTERFACE = "Whizbang.Core.IEvent";

  private readonly Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> _routed;

  private PerspectiveContract(Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> routed) {
    _routed = routed;
  }

  /// <summary>The contract of <paramref name="perspective"/>: its models and the events each one is routed.</summary>
  internal static PerspectiveContract Of(INamedTypeSymbol perspective) {
    var routed = new Dictionary<ITypeSymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default);
    foreach (var single in PerspectiveDiscoveryHelper.FindSingleStreamInterfaces(perspective)) {
      _route(routed, single.TypeArguments[0], single.TypeArguments.Skip(1));
    }
    foreach (var global in PerspectiveDiscoveryHelper.FindGlobalInterfaces(perspective)) {
      // TypeArguments: [0] the model, [1] the partition key, [2..] the events.
      _route(routed, global.TypeArguments[0], global.TypeArguments.Skip(2));
    }
    return new PerspectiveContract(routed);
  }

  private static void _route(Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> routed, ITypeSymbol model, IEnumerable<ITypeSymbol> events) {
    if (!routed.TryGetValue(model, out var listed)) {
      listed = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
      routed[model] = listed;
    }
    listed.UnionWith(events);
  }

  /// <summary>Whether <paramref name="type"/> is one of the perspective's models.</summary>
  internal bool HasModel(ITypeSymbol type) => _routed.ContainsKey(type);

  /// <summary>Whether an interface on the perspective lists <paramref name="eventType"/> for <paramref name="model"/>.</summary>
  internal bool Routes(ITypeSymbol model, ITypeSymbol eventType) =>
    _routed.TryGetValue(model, out var listed) && listed.Contains(eventType);

  /// <summary>Whether <paramref name="type"/> is an event: it implements <c>IEvent</c>.</summary>
  internal static bool IsEvent(ITypeSymbol type) =>
    type.AllInterfaces.Any(static i => TypeNameUtilities.IsNamed(i, EVENT_INTERFACE));

  /// <summary>
  /// The perspective's ordinary instance <c>Apply</c> methods with two parameters, its own and inherited, of any
  /// accessibility. A base method the class overrides or hides is the class's method, so it is returned once, from the
  /// most derived type that declares it. <c>Inherited</c> is true for a method a base type declares.
  /// </summary>
  internal static IEnumerable<(IMethodSymbol Method, bool Inherited)> OrdinaryApplies(INamedTypeSymbol perspective) {
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    for (var current = perspective; current is not null; current = current.BaseType) {
      foreach (var method in current.GetMembers(APPLY).OfType<IMethodSymbol>()) {
        if (method is { MethodKind: MethodKind.Ordinary, IsStatic: false, Parameters.Length: 2 }
            && seen.Add(TypeNameUtilities.FullyQualified(method.Parameters[0].Type) + "|" + TypeNameUtilities.FullyQualified(method.Parameters[1].Type))) {
          yield return (method, !SymbolEqualityComparer.Default.Equals(current, perspective));
        }
      }
    }
  }
}
