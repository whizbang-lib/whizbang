// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The <c>Capabilities</c> startup step: after the migration step, on every instance, never blocking
/// readiness. It probes every provider and writes the verdict, or is skipped when the probe is off.
/// </summary>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
public class CapabilitiesStartupStepTests {
  private static CapabilitiesStartupStep _step(ICapabilityRegistry registry, bool probe, ILogger<CapabilitiesStartupStep>? logger = null) =>
    new(registry, Options.Create(new CapabilityOptions { ProbeOnStartup = probe }),
      logger ?? NullLogger<CapabilitiesStartupStep>.Instance);

  private static CapabilityRegistry _registry(FakeCapabilityProvider provider) =>
    new([provider], NullLogger<CapabilityRegistry>.Instance);

  [Test]
  public async Task Descriptor_RunsAfterMigrateOnEveryInstanceWithoutBlockingReadinessAsync() {
    var descriptor = _step(_registry(new FakeCapabilityProvider("broker", [])), probe: true).Descriptor;

    await Assert.That(descriptor.Name).IsEqualTo(CapabilitiesStartupStep.STEP_NAME);
    await Assert.That(descriptor.DependsOn.Single()).IsEqualTo(FrameworkStartupSteps.MIGRATE);
    await Assert.That(descriptor.RequiredCapability).IsEqualTo(StartupCapabilities.EVERY_INSTANCE);
    await Assert.That(descriptor.Blocking).IsFalse()
      .Because("what a server offers informs; it never holds a service back from ready");
  }

  [Test]
  public async Task ProbeTurnedOff_IsSkippedWithoutProbingAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));

    var report = await _step(_registry(provider), probe: false).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).Contains("Whizbang:Capabilities:ProbeOnStartup");
    await Assert.That(provider.Probes).IsEqualTo(0);
  }

  [Test]
  public async Task ProbeOn_ProbesOnceAndReportsAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a", "vhost-b"],
      FakeCapabilityProvider.Describe("broker", "x"), FakeCapabilityProvider.Describe("broker", "y"));
    provider.States["y"] = CapabilityState.Missing;
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    var report = await _step(_registry(provider), probe: true, logger).ExecuteAsync(CancellationToken.None);

    await Assert.That(provider.Probes).IsEqualTo(2);
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).IsEqualTo("2 of 4 usable");
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Information)).IsEqualTo(2);
  }
}
