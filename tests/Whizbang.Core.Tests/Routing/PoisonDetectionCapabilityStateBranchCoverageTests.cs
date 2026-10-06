// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="PoisonDetectionCapabilityState.ReportAgeCapability"/>: a surface
/// that is already degraded does not count as newly degraded again, and recovering clears it.
/// </summary>
public class PoisonDetectionCapabilityStateBranchCoverageTests {

  [Test]
  public async Task ReportAgeCapability_AlreadyDegraded_IsNotNewlyDegradedAgainAsync() {
    var state = new PoisonDetectionCapabilityState();

    var first = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: false);
    var second = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: false);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("the transition into degraded is reported once per surface, so the caller logs once rather than per message");
    await Assert.That(state.HasDegradedSurface).IsTrue();
  }

  [Test]
  public async Task ReportAgeCapability_DegradedThenRecovered_IsNotNewlyDegradedAndClearsTheSurfaceAsync() {
    var state = new PoisonDetectionCapabilityState();
    _ = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: false);

    var recovered = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: true);

    await Assert.That(recovered).IsFalse();
    await Assert.That(state.HasDegradedSurface).IsFalse()
      .Because("a surface that can supply a trustworthy age again is no longer degraded");
  }

  [Test]
  public async Task ReportAgeCapability_HealthyThenDegrades_IsNewlyDegradedAsync() {
    var state = new PoisonDetectionCapabilityState();
    var healthy = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: true);

    var degraded = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: false);

    await Assert.That(healthy).IsFalse();
    await Assert.That(degraded).IsTrue()
      .Because("a surface that was healthy and then loses its trustworthy age has just transitioned into degraded");
    await Assert.That(state.DegradedSurfaces.Count).IsEqualTo(1);
    await Assert.That(state.DegradedSurfaces[0].Entity).IsEqualTo("inbox.orders");
  }

  [Test]
  public async Task ReportAgeCapability_HealthyStaysHealthy_IsNotNewlyDegradedAsync() {
    var state = new PoisonDetectionCapabilityState();
    _ = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: true);

    var again = state.ReportAgeCapability("transport-a", "inbox.orders", canSupplyTrustworthyAge: true);

    await Assert.That(again).IsFalse()
      .Because("a surface that stays healthy never transitions into degraded");
    await Assert.That(state.HasDegradedSurface).IsFalse();
  }
}
