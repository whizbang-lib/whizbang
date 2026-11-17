// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Whether a perspective keeps a rewind floor, decided by what it consumes (#2295): the one classification the runner
/// generator (which turns snapshots off) and the EF Core association generator (which registers the reaper's marker)
/// both read, so the two can never disagree.
/// </summary>
/// <remarks>
/// A perspective keeps no rewind floor when it applies at least one event, every event it applies is ephemeral
/// (<see cref="EphemeralResolver.IsEphemeral"/>), and it is not marked <c>[KeepEphemeralRewindFloor]</c>. A perspective
/// that applies any durable event keeps its floor whatever it is marked: its snapshots are either an optimization or the
/// only record of what its reaped ephemeral events contributed.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
/// <tests>tests/Whizbang.Generators.Tests/EphemeralRewindFloorGeneratorTests.cs</tests>
public static class EphemeralConsumption {
  /// <summary>The opt-in that keeps a floor for a perspective that applies only ephemeral events.</summary>
  public const string KEEP_REWIND_FLOOR_ATTRIBUTE = "global::Whizbang.Core.Attributes.KeepEphemeralRewindFloorAttribute";

  /// <summary>True when the perspective keeps no rewind floor.</summary>
  /// <param name="perspective">The perspective class.</param>
  /// <param name="appliedEvents">Every event type the perspective applies.</param>
  public static bool KeepsNoRewindFloor(INamedTypeSymbol perspective, IReadOnlyCollection<ITypeSymbol> appliedEvents) =>
    appliedEvents.Count > 0
    && appliedEvents.All(EphemeralResolver.IsEphemeral)
    && !perspective.GetAttributes().Any(a => TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, KEEP_REWIND_FLOOR_ATTRIBUTE));
}
