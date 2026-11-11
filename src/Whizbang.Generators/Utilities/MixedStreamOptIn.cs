// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// The one reading of <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents]</c> and of the (ephemeral, Sourced) pairs a
/// read model applies, shared by the ephemeral analyzer (WHIZ130, WHIZ139) and the perspective registry generator, so
/// the build's diagnostics and the runtime's allowed pairs agree on every read model (#2255).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <tests>tests/Whizbang.Generators.Tests/MixedStreamAllowanceGeneratorTests.cs</tests>
internal static class MixedStreamOptIn {
  private const string ATTRIBUTE = "DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute";
  private const string ATTRIBUTE_SHORT = "DangerouslyAllowMixedEphemeralAndSourcedEvents";
  private const string STREAMS = "Streams";

  // MixedStreams.Allow; the enum's underlying value, which is what a named attribute argument carries.
  private const int ALLOW = 1;

  /// <summary>
  /// True when the read model carries the attribute, matched by simple name so it holds when the attribute does not bind.
  /// </summary>
  public static bool HasOptIn(INamedTypeSymbol perspective) =>
    perspective.GetAttributes().Any(a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is ATTRIBUTE or ATTRIBUTE_SHORT);

  /// <summary>True when the read model carries the attribute with <c>Streams = MixedStreams.Allow</c>.</summary>
  public static bool AllowsStreams(INamedTypeSymbol perspective) =>
    perspective.GetAttributes()
      .Where(a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is ATTRIBUTE or ATTRIBUTE_SHORT)
      .SelectMany(a => a.NamedArguments)
      .Any(argument => argument.Key == STREAMS && argument.Value.Value is ALLOW);

  /// <summary>
  /// Every (ephemeral, Sourced) pair among the read model's applied events, each ephemeral event with each Sourced one,
  /// in the order the events are given; empty unless it applies both kinds.
  /// </summary>
  public static List<(ITypeSymbol Ephemeral, ITypeSymbol Durable)> Pairs(IEnumerable<ITypeSymbol> events) {
    var ephemeral = new List<ITypeSymbol>();
    var durable = new List<ITypeSymbol>();
    foreach (var evt in events) {
      (EphemeralResolver.IsEphemeral(evt) ? ephemeral : durable).Add(evt);
    }
    return [.. ephemeral.SelectMany(e => durable.Select(d => (e, d)))];
  }
}
