// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for three <see cref="BackupTickCoordinator"/> branches
/// <see cref="BackupTickCoordinatorTests"/> and <see cref="BackupTickCoordinatorStateMachineTests"/>
/// don't reach: a schema-gate wait canceled during startup, the ASLEEP state's real sleep-then-loop
/// cycle, and a registered tick whose <see cref="OperationCanceledException"/> races the
/// coordinator's own shutdown check. This coordinator is the zero-idle-polling backstop for every
/// registered periodic concern — a startup cancellation that faults instead of returning cleanly
/// would report a routine shutdown as a crash, and an ASLEEP loop that never actually re-checks
/// idle time would mean the backstop never wakes up.
/// </summary>
public class BackupTickCoordinatorCoverageTests {

  /// <summary>Always reports a small, constant idle time so the ASLEEP branch never transitions
  /// to POLLING; signals once its getter has been read a second time, proving the sleep-then-loop
  /// cycle actually ran instead of the coordinator exiting or hanging on the first pass.</summary>
  private sealed class AlwaysIdleTracker : IIdleActivityTracker {
    private int _reads;
    public TaskCompletionSource SecondReadSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Touch(string source) { }
    public TimeSpan TimeSinceLastActivity {
      get {
        if (Interlocked.Increment(ref _reads) == 2) {
          SecondReadSignal.TrySetResult();
        }
        return TimeSpan.Zero;
      }
    }
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => string.Empty;
  }

  /// <summary>What breaks: the ASLEEP branch's whole purpose is zero DB calls while idle — if it
  /// never actually slept and re-checked, the backstop would either busy-loop or never wake up to
  /// transition to POLLING once real idle time elapses.</summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_Asleep_SleepsThenReChecksIdleTimeAsync(CancellationToken testToken) {
    var tracker = new AlwaysIdleTracker();
    var coordinator = new BackupTickCoordinator(
      tracker: tracker,
      registry: new BackupTickRegistry(),
      options: Options.Create(new BackupTickCoordinatorOptions { IdleThreshold = TimeSpan.FromMilliseconds(5) }),
      logger: NullLogger<BackupTickCoordinator>.Instance,
      gate: NullNotifySignalingGate.Instance);

    using var cts = new CancellationTokenSource();
    await coordinator.StartAsync(cts.Token);
    await tracker.SecondReadSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await coordinator.StopAsync(CancellationToken.None);

    await Assert.That(tracker.SecondReadSignal.Task.IsCompletedSuccessfully).IsTrue()
      .Because("a second read of idle time proves the ASLEEP branch actually slept and looped back to re-check, instead of exiting or hanging on the first pass");
  }

  /// <summary>What breaks: a registered tick canceling in lockstep with the coordinator's own
  /// shutdown token must stop the cycle without being counted as a delegate failure — counting it
  /// would page an operator for an ordinary shutdown race.</summary>
  [Test]
  public async Task FireOneCycleAsync_TickCancelsDuringShutdown_BreaksWithoutCountingAFailureAsync() {
    var registry = new BackupTickRegistry();
    using var cts = new CancellationTokenSource();
    var ranSecond = false;
    registry.Register("shutdown-racer", _ => {
      cts.Cancel();
      throw new OperationCanceledException("racing the coordinator's own shutdown");
    }, () => true);
    registry.Register("second", _ => { ranSecond = true; return Task.CompletedTask; }, () => true);
    var coordinator = new BackupTickCoordinator(
      tracker: new IdleActivityTracker(TimeProvider.System),
      registry: registry,
      options: Options.Create(new BackupTickCoordinatorOptions()),
      logger: NullLogger<BackupTickCoordinator>.Instance,
      gate: NullNotifySignalingGate.Instance);

    var invoked = await coordinator.FireOneCycleAsync(cts.Token);

    await Assert.That(ranSecond).IsFalse()
      .Because("a cancellation racing the coordinator's own token must halt the cycle immediately");
    await Assert.That(invoked).IsEqualTo(1)
      .Because("the racing tick was still invoked before it canceled");
    await Assert.That(coordinator.TotalDelegateFailures).IsEqualTo(0L)
      .Because("shutdown propagation is not a delegate failure — counting it would alert on an ordinary stop");
  }
}
