// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
    public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) =>
      CapabilityActivationResolver.Resolve(requirements, configuration);
  }

  private static CapabilitiesStartupStep _step(
      ICapabilityRegistry registry, bool probe, ILogger<CapabilitiesStartupStep>? logger = null,
      IConfiguration? configuration = null, params CapabilityRequirement[] requirements) =>
    new(registry, Options.Create(new CapabilityOptions { ProbeOnStartup = probe }),
      [new FixedRequirements(requirements)], [], configuration,
      logger ?? NullLogger<CapabilitiesStartupStep>.Instance);

  private static readonly CapabilityRequirement _delayed =
    new(new CapabilityId("broker", "x"), "Delayed delivery", "W:Delay:Mode", "Delayed", "Off");

  private static IConfiguration _set(string key, string value) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

  private static CapabilityRegistry _registry(FakeCapabilityProvider provider) =>
    new([provider], NullLogger<CapabilityRegistry>.Instance);

  /// <summary>A fake clock that says when a wait is scheduled and how long it is, so a test advances it on that signal.</summary>
  private sealed class SchedulingClock : FakeTimeProvider {
    private readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();

    public List<TimeSpan> Waits { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      lock (Waits) {
        Waits.Add(dueTime);
      }
      _scheduled.Writer.TryWrite(dueTime);
      return timer;
    }

    /// <summary>
    /// Waits for the next scheduled wait, then advances past it; returns false, advancing nothing, when the step
    /// finished first, so a step that schedules fewer waits than expected fails its assertions instead of hanging.
    /// </summary>
    public async Task<bool> ElapseNextWaitAsync(TimeSpan expected, Task running) {
      var next = _scheduled.Reader.ReadAsync().AsTask();
      if (await Task.WhenAny(next, running) != next) {
        return false;
      }
      Advance(expected);
      return true;
    }
  }

  private static CapabilitiesStartupStep _retryingStep(
      ICapabilityRegistry registry, TimeProvider clock, ILogger<CapabilitiesStartupStep> logger,
      IConfiguration? configuration, params CapabilityRequirement[] requirements) =>
    new(registry, Options.Create(new CapabilityOptions()), [new FixedRequirements(requirements)], [], configuration, logger, clock);

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
      .Contains("capability broker/x on 'vhost-a': Ready, already present 1.0. Required by: Delayed delivery (W:Delay:Mode = Delayed, explicit)");
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

  // ---- #1513, decided in #1672: Unknown on an explicit requirement is probed again a bounded number of times,
  // then fails start ----

  [Test]
  public async Task ExplicitRequirement_StillUnknownAfterTheBoundedRetries_FailsStartAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x")) {
      Failure = new InvalidOperationException("broker unreachable"),
    };
    var registry = _registry(provider);
    var clock = new SchedulingClock();
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    var running = _retryingStep(registry, clock, logger, _set("W:Delay:Mode", "Delayed"), _delayed).ExecuteAsync(CancellationToken.None).AsTask();
    var retry = 1;
    while (retry <= 3 && await clock.ElapseNextWaitAsync(StartupAssessmentSource.RetryDelay(retry), running)) {
      retry++;
    }
    await running;

    await Assert.That(provider.Probes).IsEqualTo(4).Because("the first probe and three bounded retries");
    await Assert.That(clock.Waits).IsEquivalentTo([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);
    var failure = registry.Verdict.Failures.Single();
    await Assert.That(failure.Status.State).IsEqualTo(CapabilityState.Unknown);
    await Assert.That(failure.Line).StartsWith(
      "capability broker/x on 'vhost-a': Unknown: InvalidOperationException: broker unreachable.");
    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("probing again in", StringComparison.Ordinal)))
      .IsEqualTo(3);
    await Assert.That(records.Single(r => r.Level == LogLevel.Error).Message).IsEqualTo(failure.Line);
  }

  [Test]
  public async Task ExplicitRequirement_UnknownThenReadyOnARetry_ProceedsAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));
    provider.States["x"] = CapabilityState.Unknown;
    var registry = _registry(provider);
    var clock = new SchedulingClock();

    var running = _retryingStep(registry, clock, new FakeLogger<CapabilitiesStartupStep>(), _set("W:Delay:Mode", "Delayed"), _delayed)
      .ExecuteAsync(CancellationToken.None).AsTask();
    provider.States.Remove("x");
    await clock.ElapseNextWaitAsync(StartupAssessmentSource.RetryDelay(1), running);
    await running;

    await Assert.That(provider.Probes).IsEqualTo(2).Because("the retry stops as soon as the probe can tell");
    await Assert.That(registry.Verdict.Entries.Single().Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
  }

  [Test]
  public async Task ImplicitRequirement_Unknown_IsNotProbedAgain_AndDegradesAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"));
    provider.States["x"] = CapabilityState.Unknown;
    var registry = _registry(provider);
    var clock = new SchedulingClock();

    await _retryingStep(registry, clock, new FakeLogger<CapabilitiesStartupStep>(), null, _delayed with { Default = "Delayed" })
      .ExecuteAsync(CancellationToken.None);

    await Assert.That(provider.Probes).IsEqualTo(1);
    await Assert.That(clock.Waits).IsEmpty().Because("only an explicit requirement is worth holding the verdict for");
    await Assert.That(registry.Verdict.Entries.Single().Outcome).IsEqualTo(CapabilityVerdictOutcome.Degrade);
  }

  [Test]
  public async Task ExplicitRequirement_NoProviderDescribes_IsNotProbedAgain_AndWarnsAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "y"));
    var registry = _registry(provider);
    var clock = new SchedulingClock();

    await _retryingStep(registry, clock, new FakeLogger<CapabilitiesStartupStep>(), _set("W:Delay:Mode", "Delayed"), _delayed)
      .ExecuteAsync(CancellationToken.None);

    await Assert.That(provider.Probes).IsEqualTo(1);
    await Assert.That(clock.Waits).IsEmpty().Because("probing again cannot describe a capability no provider knows");
    await Assert.That(registry.Verdict.Entries.Single().Outcome).IsEqualTo(CapabilityVerdictOutcome.Warn);
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    var registry = _registry(new FakeCapabilityProvider("broker", []));
    var options = Options.Create(new CapabilityOptions());
    var logger = NullLogger<CapabilitiesStartupStep>.Instance;

    await Assert.That(() => new CapabilitiesStartupStep(null!, options, [], [], null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, null!, [], [], null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, null!, [], null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, [], null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, [], [], null, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilitiesStartupStep(registry, options, [], [], null, logger, null!)).Throws<ArgumentNullException>();
  }
}
