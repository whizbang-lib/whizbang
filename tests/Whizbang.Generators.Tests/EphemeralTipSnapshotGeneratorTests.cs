// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2463: a perspective that applies both ephemeral and durable events keeps a tip snapshot beside its reap-boundary
/// floor. The tip is filled by the live and rewind cadences and is never a reap floor; the floor is moved only by the
/// reap-boundary bootstrap. The generator decides which perspectives keep one, from what they apply, and only those.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
public partial class EphemeralRewindFloorGeneratorTests {
  private const string KEEPS_TIP = "KeepsTipSnapshot = true,";

  [Test]
  [RequiresAssemblyFiles]
  [Arguments(MIXED, "applies a durable event beside the ephemeral one")]
  [Arguments(DURABLE_THROUGH_GLOBAL, "applies a durable event through the global interface beside the ephemeral one")]
  public async Task Runner_OfAPerspectiveMixingEphemeralAndDurableEvents_KeepsATipSnapshotAsync(string source, string why) {
    var runner = _runner(source);

    await Assert.That(runner).Contains(KEEPS_TIP).Because($"the perspective {why}");
    await Assert.That(runner).Contains("IsEphemeral = true,").Because("the control: it is still an ephemeral perspective");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments(EPHEMERAL_ONLY, "applies only ephemeral events and keeps no floor")]
  [Arguments(OPTED_IN, "applies only ephemeral events: its one snapshot is its floor")]
  [Arguments(DURABLE, "applies only durable events: none of its history is reaped")]
  public async Task Runner_OfAPerspectiveThatDoesNotMix_KeepsNoTipSnapshotAsync(string source, string why) {
    var runner = _runner(source);

    await Assert.That(runner).DoesNotContain(KEEPS_TIP).Because($"the perspective {why}");
    await Assert.That(runner).Contains("PresencePerspectiveRunner").Because("the control: the runner was generated");
  }
}
