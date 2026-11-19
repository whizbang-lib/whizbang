// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.RunControl;
using Whizbang.Testing.Lifecycle;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Component.Tests.Capabilities;

/// <summary>
/// The hosted service that stops the host when the verdict finds an explicitly configured feature whose
/// capability is not ready. It waits for the verdict without holding host start: the probe runs after the
/// schema is migrated, which a hosted service started before it may be the one doing. It exits through the one exit
/// path (#2126) as a startup failure, exit code 4, so a stop for a missing capability never looks like an orderly
/// shutdown.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityVerdictEnforcer.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#validation</docs>
public class CapabilityVerdictEnforcerTests {
  private static readonly CapabilityRequirement _requirement =
    new(new CapabilityId("broker", "x"), "Delayed delivery", "W:Delay:Mode", "Delayed", "Off");

  private static CapabilityVerdict _verdict(CapabilityState state) {
    var status = new CapabilityStatus(
      new CapabilityDescriptor(new CapabilityId("broker", "x"), "a purpose", [], false, [], new Dictionary<CapabilityState, string>()),
      "vhost-a", state, null, null, null);
    return CapabilityVerdict.Evaluate([status],
      [new ActiveCapabilityRequirement(_requirement, _requirement.Key, "Delayed", CapabilityActivation.Explicit)]);
  }

  private static (CapabilityVerdictEnforcer Enforcer, CapabilityRegistry Registry, RecordingInstanceExit Exit) _enforcer() {
    var registry = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);
    var exit = new RecordingInstanceExit();
    return (new CapabilityVerdictEnforcer(registry, exit), registry, exit);
  }

  [Test]
  public async Task AFailingVerdict_ExitsAsAStartupFailure_NamingTheLineAsync() {
    var (enforcer, registry, exit) = _enforcer();

    await enforcer.StartAsync(CancellationToken.None);
    registry.PublishVerdict(_verdict(CapabilityState.Missing));
    await enforcer.Watching;

    var request = exit.Requests.Single();
    await Assert.That(request.Cause).IsEqualTo(ExitCause.StartupFailure);
    await Assert.That(ExitCauses.ExitCodeOf(request.Cause)).IsEqualTo(4);
    await Assert.That(request.Reason).Contains(
      "capability broker/x on 'vhost-a': Missing. Fix: install broker/x on the server. Required by: Delayed delivery (W:Delay:Mode = Delayed, explicit)");
    await Assert.That(request.Reason).StartsWith("1 explicitly configured feature(s) need a capability that is not ready");
  }

  [Test]
  public async Task AVerdictWithoutFailures_LeavesTheHostRunningAsync() {
    var (enforcer, registry, exit) = _enforcer();
    registry.PublishVerdict(_verdict(CapabilityState.Unknown));

    await enforcer.StartAsync(CancellationToken.None);
    await enforcer.Watching;

    await Assert.That(exit.Requests).IsEmpty().Because("an explicit requirement nothing could check warns; it does not stop start");
  }

  [Test]
  public async Task StoppedBeforeAnyVerdict_EndsWithoutExitingAsync() {
    var (enforcer, _, exit) = _enforcer();

    await enforcer.StartAsync(CancellationToken.None);
    await enforcer.StopAsync(CancellationToken.None);

    await Assert.That(enforcer.Watching.IsCompletedSuccessfully).IsTrue();
    await Assert.That(exit.Requests).IsEmpty();
    enforcer.Dispose();
    await Assert.That(() => enforcer.StartAsync(CancellationToken.None)).Throws<ObjectDisposedException>()
      .Because("disposing releases the stop signal, so a disposed enforcer cannot watch again");
  }

  [Test]
  public async Task StopBeforeStart_IsANoOpAsync() {
    var (enforcer, _, exit) = _enforcer();

    await enforcer.StopAsync(CancellationToken.None);

    await Assert.That(exit.Requests).IsEmpty();
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    var registry = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);

    await Assert.That(() => new CapabilityVerdictEnforcer(null!, new RecordingInstanceExit())).Throws<ArgumentNullException>();
    await Assert.That(() => new CapabilityVerdictEnforcer(registry, null!)).Throws<ArgumentNullException>();
  }
}
