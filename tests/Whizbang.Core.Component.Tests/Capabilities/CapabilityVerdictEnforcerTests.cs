// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Component.Tests.Capabilities;

/// <summary>
/// The hosted service that stops the host when the verdict finds an explicitly configured feature whose
/// capability is not ready. It waits for the verdict without holding host start: the probe runs after the
/// schema is migrated, which a hosted service started before it may be the one doing. It stops the host with a
/// non-zero exit code, so a stop for a missing capability never looks like an orderly shutdown.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityVerdictEnforcer.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#validation</docs>
public class CapabilityVerdictEnforcerTests {
  private sealed class RecordingHostLifetime : IHostApplicationLifetime {
    public int StopCalls { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => StopCalls++;
  }

  private static readonly CapabilityRequirement _requirement =
    new(new CapabilityId("broker", "x"), "Delayed delivery", "W:Delay:Mode", "Delayed", "Off");

  private static CapabilityVerdict _verdict(CapabilityState state) {
    var status = new CapabilityStatus(
      new CapabilityDescriptor(new CapabilityId("broker", "x"), "a purpose", [], false, [], new Dictionary<CapabilityState, string>()),
      "vhost-a", state, null, null, null);
    return CapabilityVerdict.Evaluate([status],
      [new ActiveCapabilityRequirement(_requirement, _requirement.Key, "Delayed", CapabilityActivation.Explicit)]);
  }

  private static (CapabilityVerdictEnforcer Enforcer, CapabilityRegistry Registry, RecordingHostLifetime Lifetime, List<int> ExitCodes, FakeLogger<CapabilityVerdictEnforcer> Logger) _enforcer() {
    var registry = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);
    var lifetime = new RecordingHostLifetime();
    var exitCodes = new List<int>();
    var logger = new FakeLogger<CapabilityVerdictEnforcer>();
    return (new CapabilityVerdictEnforcer(registry, lifetime.StopApplication, logger, exitCodes.Add), registry, lifetime, exitCodes, logger);
  }

  [Test]
  public async Task AFailingVerdict_StopsTheHostWithExitCodeOne_NamingTheLineAsync() {
    var (enforcer, registry, lifetime, exitCodes, logger) = _enforcer();

    await enforcer.StartAsync(CancellationToken.None);
    registry.PublishVerdict(_verdict(CapabilityState.Missing));
    await enforcer.Watching;

    await Assert.That(lifetime.StopCalls).IsEqualTo(1);
    await Assert.That(exitCodes).IsEquivalentTo([1]);
    var critical = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Critical);
    await Assert.That(critical.Message).Contains(
      "capability broker/x on 'vhost-a': Missing. Fix: install broker/x on the server. Required by: Delayed delivery (W:Delay:Mode = Delayed, explicit)");
  }

  [Test]
  public async Task AVerdictWithoutFailures_LeavesTheHostRunningAsync() {
    var (enforcer, registry, lifetime, exitCodes, _) = _enforcer();
    registry.PublishVerdict(_verdict(CapabilityState.Unknown));

    await enforcer.StartAsync(CancellationToken.None);
    await enforcer.Watching;

    await Assert.That(lifetime.StopCalls).IsEqualTo(0).Because("an explicit requirement nothing could check warns; it does not stop start");
    await Assert.That(exitCodes).IsEmpty();
  }

  [Test]
  public async Task StoppedBeforeAnyVerdict_EndsWithoutStoppingTheHostAsync() {
    var (enforcer, _, lifetime, exitCodes, _) = _enforcer();

    await enforcer.StartAsync(CancellationToken.None);
    await enforcer.StopAsync(CancellationToken.None);

    await Assert.That(enforcer.Watching.IsCompletedSuccessfully).IsTrue();
    await Assert.That(lifetime.StopCalls).IsEqualTo(0);
    await Assert.That(exitCodes).IsEmpty();
    enforcer.Dispose();
    await Assert.That(() => enforcer.StartAsync(CancellationToken.None)).Throws<ObjectDisposedException>()
      .Because("disposing releases the stop signal, so a disposed enforcer cannot watch again");
  }

  [Test]
  public async Task StopBeforeStart_IsANoOpAsync() {
    var (enforcer, _, lifetime, _, _) = _enforcer();

    await enforcer.StopAsync(CancellationToken.None);

    await Assert.That(lifetime.StopCalls).IsEqualTo(0);
  }

  [Test]
  public async Task SetProcessExitCode_SetsTheProcessExitCodeAsync() {
    var before = Environment.ExitCode;
    try {
      CapabilityVerdictEnforcer.SetProcessExitCode(3);
      await Assert.That(Environment.ExitCode).IsEqualTo(3);
    } finally {
      Environment.ExitCode = before;
    }
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    var registry = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);
    var lifetime = new RecordingHostLifetime();
    var logger = NullLogger<CapabilityVerdictEnforcer>.Instance;
    static void exit(int _) { }

    await Assert.That(() => new CapabilityVerdictEnforcer(null!, lifetime.StopApplication, logger, exit)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilityVerdictEnforcer(registry, null!, logger, exit)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilityVerdictEnforcer(registry, lifetime.StopApplication, null!, exit)).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilityVerdictEnforcer(registry, lifetime.StopApplication, logger, null!)).Throws<ArgumentNullException>();
  }
}
