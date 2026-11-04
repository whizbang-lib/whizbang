// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// The one run-control path (#2045): stand-down, pause and stop all take it. It closes the run permit, reports the
/// transitional phase, waits for every hold to finish, releases the work still held, releases the roles, and confirms by
/// writing the settled phase. Each test records the steps in one log, so the order itself is what is asserted.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangRunController.cs</code-under-test>
[Category("RunControl")]
public class WhizbangRunControllerTests {

  private sealed class StepLog {
    public List<string> Steps { get; } = [];
    public void Add(string step) {
      lock (Steps) {
        Steps.Add(step);
      }
    }
  }

  private sealed class RecordingLifecycle(StepLog log, WhizbangRunPermit permit) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      log.Add($"phase:{phase}:{permit.State}");
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class RecordingWorkReleaser(StepLog log, string name, Exception? fail = null) : IReleasesHeldWork {
    public Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) {
      log.Add("work:" + name);
      return fail is null ? Task.CompletedTask : Task.FromException(fail);
    }
  }

  private sealed class RecordingRoleReleaser(StepLog log, string name, Exception? fail = null) : IReleasesHeldRoles {
    public Task ReleaseAllAsync(CancellationToken cancellationToken) {
      log.Add("roles:" + name);
      return fail is null ? Task.CompletedTask : Task.FromException(fail);
    }
  }

  private sealed record Harness(
    WhizbangRunController Controller, WhizbangRunPermit Permit, StepLog Log, RecordingLifecycle Lifecycle, FakeTimeProvider Time);

  private static Harness _build(
      IEnumerable<IReleasesHeldWork>? work = null, IEnumerable<IReleasesHeldRoles>? roles = null,
      WhizbangLifecycleOptions? options = null, StepLog? log = null) {
    log ??= new StepLog();
    var permit = new WhizbangRunPermit();
    var lifecycle = new RecordingLifecycle(log, permit);
    var time = new FakeTimeProvider();
    var controller = new WhizbangRunController(
      permit, lifecycle, work ?? [], roles ?? [], options ?? new WhizbangLifecycleOptions(), time,
      NullLogger<WhizbangRunController>.Instance);
    return new Harness(controller, permit, log, lifecycle, time);
  }

  [Test]
  [Arguments(RunHaltReason.Pause, "Pausing", "Paused", RunState.Paused)]
  [Arguments(RunHaltReason.StandDown, "Pausing", "StandingBy", RunState.Paused)]
  [Arguments(RunHaltReason.Stop, "Stopping", "Stopped", RunState.Stopped)]
  public async Task Halt_ClosesThePermit_ReleasesWorkThenRoles_ThenWritesTheSettledPhaseAsync(
      RunHaltReason reason, string transitional, string settled, RunState closed) {
    var log = new StepLog();
    var h = _build(
      [new RecordingWorkReleaser(log, "claims")], [new RecordingRoleReleaser(log, "elector")], log: log);

    await h.Controller.HaltAsync(reason, CancellationToken.None);

    // The permit is closed before the transitional phase is written, so the row never says "pausing" while work is
    // still being taken; the settled phase is written last, as the confirmation.
    await Assert.That(h.Log.Steps).IsEquivalentTo(
      [$"phase:{transitional}:{closed}", "work:claims", "roles:elector", $"phase:{settled}:{closed}"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(h.Permit.State).IsEqualTo(closed);
    await Assert.That(h.Controller.HaltedFor).IsEqualTo(reason);
  }

  [Test]
  public async Task Halt_WaitsForEveryHoldBeforeReleasingAsync() {
    var log = new StepLog();
    var h = _build([new RecordingWorkReleaser(log, "claims")], log: log);
    var hold = await h.Permit.EnterAsync(CancellationToken.None);

    var halting = h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);
    // The transitional phase is written at once, but nothing is released while a claim is still in hand.
    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Pausing:Paused"]);
    await Assert.That(halting.IsCompleted).IsFalse();

    hold.Dispose();
    await halting;
    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Pausing:Paused", "work:claims", "phase:Paused:Paused"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Halt_AHoldThatOutlastsTheBound_IsLeftAndTheHaltGoesOnAsync() {
    var log = new StepLog();
    var options = new WhizbangLifecycleOptions { HeldWorkTimeout = TimeSpan.FromSeconds(20) };
    var h = _build([new RecordingWorkReleaser(log, "claims")], options: options, log: log);
    using var stuck = await h.Permit.EnterAsync(CancellationToken.None);

    var halting = h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);
    await Assert.That(halting.IsCompleted).IsFalse();
    h.Time.Advance(TimeSpan.FromSeconds(20));
    await halting;

    await Assert.That(h.Log.Steps).Contains("phase:Paused:Paused")
      .Because("a hold that never ends must not leave the instance reporting Pausing forever: its leases lapse instead");
  }

  [Test]
  public async Task Halt_WithNothingHeldAndNoReleasers_StillWritesBothPhasesAsync() {
    // The fast path signals the lifecycle too: a halt with nothing to finish still reports where the instance stands.
    var h = _build();

    await h.Controller.HaltAsync(RunHaltReason.StandDown, CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Pausing:Paused", "phase:StandingBy:Paused"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Halt_Repeated_ReportsAgainAsync() {
    var h = _build();
    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);
    h.Log.Steps.Clear();

    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Pausing:Paused", "phase:Paused:Paused"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Halt_AfterAStop_IsNotAPauseAsync() {
    // Design choice (#2045): a stopped instance stays stopped; a later pause or stand-down changes nothing.
    var h = _build();
    await h.Controller.HaltAsync(RunHaltReason.Stop, CancellationToken.None);
    h.Log.Steps.Clear();

    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEmpty();
    await Assert.That(h.Controller.HaltedFor).IsEqualTo(RunHaltReason.Stop);
  }

  [Test]
  public async Task Halt_AStopAfterAPause_StopsAsync() {
    var h = _build();
    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await h.Controller.HaltAsync(RunHaltReason.Stop, CancellationToken.None);

    await Assert.That(h.Permit.State).IsEqualTo(RunState.Stopped);
    await Assert.That(h.Lifecycle.Phase).IsEqualTo(LifecyclePhase.Stopped);
  }

  [Test]
  public async Task Halt_AReleaserThatFails_IsLoggedAndTheRestStillRunAsync() {
    var log = new StepLog();
    var h = _build(
      [new RecordingWorkReleaser(log, "bad", new InvalidOperationException("boom")), new RecordingWorkReleaser(log, "good")],
      [new RecordingRoleReleaser(log, "bad", new InvalidOperationException("boom")), new RecordingRoleReleaser(log, "good")],
      log: log);

    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEquivalentTo(
      ["phase:Pausing:Paused", "work:bad", "work:good", "roles:bad", "roles:good", "phase:Paused:Paused"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Halt_AnObeyerThatFails_DoesNotStopTheHaltAsync() {
    var h = _build();
    using var subscription = h.Permit.Obey((_, _) => Task.FromException(new InvalidOperationException("pause failed")));

    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await Assert.That(h.Log.Steps).Contains("phase:Paused:Paused");
  }

  [Test]
  public async Task Halt_Canceled_ThrowsAndReleasesTheSerializationAsync() {
    var h = _build();
    using var hold = await h.Permit.EnterAsync(CancellationToken.None);
    using var cts = new CancellationTokenSource();

    var halting = h.Controller.HaltAsync(RunHaltReason.Pause, cts.Token);
    await cts.CancelAsync();
    await Assert.That(async () => await halting).Throws<OperationCanceledException>();

    hold.Dispose();
    await h.Controller.ResumeAsync(CancellationToken.None);
    await Assert.That(h.Permit.State).IsEqualTo(RunState.Running);
  }

  [Test]
  public async Task Halt_CanceledWhileAnObeyerActs_ThrowsAsync() {
    // A cancellation is the caller's, not an obeyer's failure: it ends the path rather than being logged and passed.
    var h = _build();
    using var cts = new CancellationTokenSource();
    using var subscription = h.Permit.Obey(async (_, token) => {
      await cts.CancelAsync();
      token.ThrowIfCancellationRequested();
    });

    await Assert.That(async () => await h.Controller.HaltAsync(RunHaltReason.Pause, cts.Token)).Throws<OperationCanceledException>();
    await Assert.That(h.Log.Steps).IsEmpty();
  }

  [Test]
  public async Task Halt_AnOperationCanceledThatIsNotTheCallers_IsAFailedStep_AndTheHaltGoesOnAsync() {
    // An obeyer or a releaser whose own operation was canceled has failed that step; only the caller's token ends the
    // path. Each is logged, and the halt is still confirmed.
    var log = new StepLog();
    var h = _build([new ThrowingWorkReleaser(new OperationCanceledException("the release statement was canceled"))],
      [new RecordingRoleReleaser(log, "elector")], log: log);
    using var subscription = h.Permit.Obey((_, _) => Task.FromException(new OperationCanceledException("the pause was canceled")));

    await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Pausing:Paused", "roles:elector", "phase:Paused:Paused"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  private sealed class ThrowingWorkReleaser(Exception exception) : IReleasesHeldWork {
    public Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) => Task.FromException(exception);
  }

  [Test]
  public async Task Halt_CanceledWhileReleasing_ThrowsAsync() {
    var log = new StepLog();
    using var cts = new CancellationTokenSource();
    var h = _build([new CancelingWorkReleaser(cts)], log: log);

    await Assert.That(async () => await h.Controller.HaltAsync(RunHaltReason.Pause, cts.Token)).Throws<OperationCanceledException>();
    await Assert.That(h.Log.Steps).DoesNotContain("phase:Paused:Paused");
  }

  private sealed class CancelingWorkReleaser(CancellationTokenSource cts) : IReleasesHeldWork {
    public async Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) {
      await cts.CancelAsync();
      cancellationToken.ThrowIfCancellationRequested();
    }
  }

  [Test]
  public async Task Dispose_ThenHalt_IsRefusedAsync() {
    var h = _build();
    h.Controller.Dispose();

    await Assert.That(async () => await h.Controller.HaltAsync(RunHaltReason.Pause, CancellationToken.None))
      .Throws<ObjectDisposedException>();
  }

  [Test]
  [Arguments(RunHaltReason.Pause)]
  [Arguments(RunHaltReason.StandDown)]
  public async Task Resume_AfterAPauseOrAStandDown_ReopensThePermitAsync(RunHaltReason reason) {
    var h = _build();
    await h.Controller.HaltAsync(reason, CancellationToken.None);
    h.Log.Steps.Clear();

    await h.Controller.ResumeAsync(CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEquivalentTo(["phase:Resuming:Paused", "phase:Running:Running"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(h.Permit.IsRunning).IsTrue();
    await Assert.That(h.Controller.HaltedFor).IsNull();
  }

  [Test]
  public async Task Resume_AfterAStop_IsRefusedAsync() {
    // Design choice (#2045): resume reopens the permit after a pause or a stand-down, never after a stop.
    var h = _build();
    await h.Controller.HaltAsync(RunHaltReason.Stop, CancellationToken.None);

    await Assert.That(async () => await h.Controller.ResumeAsync(CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(h.Permit.State).IsEqualTo(RunState.Stopped);
  }

  [Test]
  public async Task Resume_WhenRunning_ChangesNothingAsync() {
    var h = _build();

    await h.Controller.ResumeAsync(CancellationToken.None);

    await Assert.That(h.Log.Steps).IsEmpty();
  }

  [Test]
  public async Task Constructor_RequiresEveryCollaboratorAsync() {
    var permit = new WhizbangRunPermit();
    var lifecycle = new RecordingLifecycle(new StepLog(), permit);
    var options = new WhizbangLifecycleOptions();
    var logger = NullLogger<WhizbangRunController>.Instance;

    await Assert.That(() => new WhizbangRunController(null!, lifecycle, [], [], options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangRunController(permit, null!, [], [], options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangRunController(permit, lifecycle, null!, [], options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangRunController(permit, lifecycle, [], null!, options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangRunController(permit, lifecycle, [], [], null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangRunController(permit, lifecycle, [], [], options, null, null!)).Throws<ArgumentNullException>();
    await Assert.That(new WhizbangRunController(permit, lifecycle, [], [], options, null, logger).Permit).IsSameReferenceAs(permit);
  }
}
