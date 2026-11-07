// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangKillswitch"/> + the coordinator's override map: coarse controls drive the
/// whole system through the lifecycle (pause/resume/stop everything), and a per-component override pins
/// one resource (drain a transport) independent of the system phase.
/// </summary>
public class WhizbangKillswitchTests {

  /// <summary>The test process keeps its own exit code.</summary>
  private sealed class DiscardedExitCode : IProcessExitCode {
    public void SetExitCode(int exitCode) { }
  }

  private sealed class FakeParticipant(string component) : IWhizbangRunControl {
    public string Component { get; } = component;
    public List<LifecyclePhase> Seen { get; } = [];
    public LifecyclePhase? Last => Seen.Count > 0 ? Seen[^1] : null;
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Seen.Add(phase);
      return default;
    }
  }

  private static (WhizbangKillswitch killswitch, IWhizbangLifecycleState lifecycle, FakeParticipant workers, FakeParticipant transport) _build() {
    var (killswitch, lifecycle, workers, transport, _) = _buildWithPermit();
    return (killswitch, lifecycle, workers, transport);
  }

  private static (WhizbangKillswitch killswitch, IWhizbangLifecycleState lifecycle, FakeParticipant workers, FakeParticipant transport,
      WhizbangRunPermit permit) _buildWithPermit() {
    var workers = new FakeParticipant("workers");
    var transport = new FakeParticipant("transport");
    var options = new WhizbangLifecycleOptions();
    var coordinator = new WhizbangLifecycleCoordinator([workers, transport], options);
    var lifecycle = new WhizbangLifecycleState(coordinator, options);
    var permit = new WhizbangRunPermit();
    var controller = new WhizbangRunController(
      permit, lifecycle, [], [], options, null, NullLogger<WhizbangRunController>.Instance);
    // #2126: the stop is the one exit path's, which halts through the same controller; outside a host it completes itself.
    var exit = new WhizbangInstanceExit(
      Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), controller, new DiscardedExitCode(), null,
      NullLogger<WhizbangInstanceExit>.Instance);
    return (new WhizbangKillswitch(controller, lifecycle, coordinator, exit), lifecycle, workers, transport, permit);
  }

  [Test]
  public async Task PauseAsync_TakesTheRunControlPath_ClosingThePermitUntilResumedAsync() {
    // #2045: the operator's pause is the same path as a stand-down and a stop, not a phase broadcast of its own.
    var (killswitch, lifecycle, _, _, permit) = _buildWithPermit();
    await lifecycle.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);

    await killswitch.PauseAsync();
    await Assert.That(permit.State).IsEqualTo(RunState.Paused);

    await killswitch.ResumeAsync();
    await Assert.That(permit.State).IsEqualTo(RunState.Running);
  }

  [Test]
  public async Task StopAsync_ClosesThePermitForGood_AndAResumeIsRefusedAsync() {
    var (killswitch, lifecycle, _, _, permit) = _buildWithPermit();
    await lifecycle.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);

    await killswitch.StopAsync();

    await Assert.That(permit.State).IsEqualTo(RunState.Stopped);
    await Assert.That(async () => await killswitch.ResumeAsync()).Throws<InvalidOperationException>();
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Stopped);
  }

  [Test]
  public async Task PauseAsync_DrivesPausingThenPausedAsync() {
    var (killswitch, lifecycle, workers, _) = _build();
    await lifecycle.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);

    await killswitch.PauseAsync();

    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Paused);
    await Assert.That(workers.Seen).Contains(LifecyclePhase.Pausing);
    await Assert.That(workers.Last).IsEqualTo(LifecyclePhase.Paused);
  }

  [Test]
  public async Task ResumeAsync_DrivesResumingThenRunningAsync() {
    var (killswitch, lifecycle, workers, _) = _build();
    await killswitch.PauseAsync();

    await killswitch.ResumeAsync();

    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Running);
    await Assert.That(workers.Seen).Contains(LifecyclePhase.Resuming);
    await Assert.That(workers.Last).IsEqualTo(LifecyclePhase.Running);
  }

  [Test]
  public async Task StopAsync_DrivesStoppingThenStoppedAsync() {
    var (killswitch, lifecycle, workers, _) = _build();
    await lifecycle.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);

    await killswitch.StopAsync();

    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Stopped);
    await Assert.That(workers.Seen).Contains(LifecyclePhase.Stopping);
    await Assert.That(workers.Last).IsEqualTo(LifecyclePhase.Stopped);
  }

  [Test]
  public async Task OverrideComponent_PinsOneResource_IndependentOfSystemPhaseAsync() {
    var (killswitch, lifecycle, workers, transport) = _build();
    await lifecycle.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);

    // Drain just the transport for maintenance; workers stay running.
    await killswitch.OverrideComponentAsync("transport", LifecyclePhase.Stopping);
    await Assert.That(transport.Last).IsEqualTo(LifecyclePhase.Stopping);
    await Assert.That(workers.Last).IsEqualTo(LifecyclePhase.Running);

    // A system transition still routes around the pinned component.
    await lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, CancellationToken.None);
    await Assert.That(workers.Last).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(transport.Last).IsEqualTo(LifecyclePhase.Stopping); // still pinned

    // Clearing returns it to the current system phase.
    await killswitch.ClearComponentOverrideAsync("transport");
    await Assert.That(transport.Last).IsEqualTo(LifecyclePhase.Migrating);
  }
}
