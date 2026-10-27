// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The <c>whizbang_capabilities</c> health entry: unhealthy while an explicitly configured feature lacks its
/// capability, degraded while one warns or degrades, healthy otherwise, with the verdict lines as its data.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityHealthCheck.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#health</docs>
public class CapabilityHealthCheckTests {
  private static readonly CapabilityRequirement _requirement =
    new(new CapabilityId("broker", "x"), "Delayed delivery", "W:Delay:Mode", "Delayed", "Off");

  private static async Task<HealthCheckResult> _checkAsync(CapabilityState? state, CapabilityActivation activation = CapabilityActivation.Explicit) {
    var registry = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);
    if (state is { } known) {
      var status = new CapabilityStatus(FakeCapabilityProvider.Describe("broker", "x"), "vhost-a", known, null, null, null);
      registry.PublishVerdict(CapabilityVerdict.Evaluate([status],
        [new ActiveCapabilityRequirement(_requirement, _requirement.Key, "Delayed", activation)]));
    }
    return await new CapabilityHealthCheck(registry).CheckHealthAsync(new HealthCheckContext());
  }

  [Test]
  public async Task BeforeTheVerdict_IsHealthyAsync() {
    var result = await _checkAsync(state: null);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).IsEqualTo("no capability is required");
  }

  [Test]
  public async Task EveryRequirementSatisfied_IsHealthyAsync() {
    var result = await _checkAsync(CapabilityState.Ready);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).IsEqualTo("1 required capability, every one ready");
  }

  [Test]
  public async Task AnExplicitRequirementFailing_IsUnhealthy_WithItsLineAsync() {
    var result = await _checkAsync(CapabilityState.Missing);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Description).IsEqualTo("1 explicitly configured feature lacks its capability");
    await Assert.That((IReadOnlyList<string>)result.Data["capabilities"]).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AnImplicitRequirementDegrading_IsDegradedAsync() {
    var result = await _checkAsync(CapabilityState.Missing, CapabilityActivation.Implicit);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    await Assert.That(result.Description).IsEqualTo("1 required capability is not ready");
  }

  [Test]
  public async Task AnExplicitRequirementUnknown_IsDegradedAsync() {
    var result = await _checkAsync(CapabilityState.Unknown);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
  }

  [Test]
  public async Task Registry_IsValidatedAsync() {
    await Assert.That(() => new CapabilityHealthCheck(null!)).Throws<ArgumentNullException>();
  }
}
