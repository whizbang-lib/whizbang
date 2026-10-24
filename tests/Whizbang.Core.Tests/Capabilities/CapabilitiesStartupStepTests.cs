// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
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
  private sealed class FixedRequirements(params CapabilityRequirement[] requirements) : ICapabilityRequirementSource {
    public IReadOnlyList<CapabilityRequirement> Requirements { get; } = requirements;
  }

  private static CapabilitiesStartupStep _step(
      ICapabilityRegistry registry, bool probe, ILogger<CapabilitiesStartupStep>? logger = null,
      IConfiguration? configuration = null, params CapabilityRequirement[] requirements) =>
    new(registry, Options.Create(new CapabilityOptions { ProbeOnStartup = probe }),
      [new FixedRequirements(requirements)], configuration,
      logger ?? NullLogger<CapabilitiesStartupStep>.Instance);

  private static readonly CapabilityRequirement _delayed =
    new(new CapabilityId("broker", "x"), "Delayed delivery", "W:Delay:Mode", "Delayed", "Off");

  private static IConfiguration _set(string key, string value) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

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

  [Test]
  public async Task ExplicitRequirement_OnACapabilityNotReady_IsAnErrorLine_AndTheVerdictIsPublishedAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"],
      FakeCapabilityProvider.Describe("broker", "x"), FakeCapabilityProvider.Describe("broker", "y"));
    provider.States["x"] = CapabilityState.Missing;
    var registry = _registry(provider);
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    await _step(registry, probe: true, logger, _set("W:Delay:Mode", "Delayed"), _delayed).ExecuteAsync(CancellationToken.None);

    var records = logger.Collector.GetSnapshot();
    await Assert.That(registry.Verdict.Failures.Single().Status.Descriptor.Id.Name).IsEqualTo("x");
    await Assert.That(records.Single(r => r.Level == LogLevel.Error).Message).IsEqualTo(
      "capability broker/x on 'vhost-a': Missing, 1.0. Fix: install broker/x on the server. Required by: Delayed delivery (W:Delay:Mode = Delayed, explicit)");
    await Assert.That(records.Where(r => r.Level == LogLevel.Information).Select(r => r.Message).Single())
      .DoesNotContain("broker/x").Because("a required capability is reported by its verdict line, not again in the block");
  }

  [Test]
  public async Task ImplicitRequirement_OnACapabilityNotReady_IsAWarningAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));
    provider.States["x"] = CapabilityState.NotPermitted;
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    await _step(_registry(provider), probe: true, logger, null, _delayed with { Default = "Delayed" }).ExecuteAsync(CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning).Message)
      .EndsWith("Required by: Delayed delivery (W:Delay:Mode = Delayed, default)");
  }

  [Test]
  public async Task RequirementOnAReadyCapability_IsAnInformationLineAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    await _step(_registry(provider), probe: true, logger, _set("W:Delay:Mode", "Delayed"), _delayed).ExecuteAsync(CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Information).Select(r => r.Message))
      .Contains("capability broker/x on 'vhost-a': Ready, 1.0. Required by: Delayed delivery (W:Delay:Mode = Delayed, explicit)");
  }

  [Test]
  public async Task ProbeTurnedOff_StillPublishesTheVerdict_WithEveryCapabilityUnknownAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));
    var registry = _registry(provider);
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    await _step(registry, probe: false, logger, _set("W:Delay:Mode", "Delayed"), _delayed).ExecuteAsync(CancellationToken.None);

    await Assert.That(registry.Verdict.Entries.Single().Outcome).IsEqualTo(CapabilityVerdictOutcome.Warn)
      .Because("an explicit requirement nothing could check warns rather than failing start");
    await Assert.That(logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning).Message)
      .StartsWith("capability broker/x on 'vhost-a': Unknown: not probed yet.");
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    var registry = _registry(new FakeCapabilityProvider("broker", []));
    var options = Options.Create(new CapabilityOptions());
    var logger = NullLogger<CapabilitiesStartupStep>.Instance;

    await Assert.That(() => new CapabilitiesStartupStep(null!, options, [], null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, null!, [], null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, [], null, null!)).Throws<ArgumentNullException>();
  }
}
