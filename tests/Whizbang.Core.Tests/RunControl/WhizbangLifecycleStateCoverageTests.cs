using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers two swallowed-exception edges of <see cref="WhizbangLifecycleState"/>'s fault path that
/// the happy-path tests in <c>WhizbangLifecycleStateTests</c> never reach: shutdown landing DURING
/// the fault record window (rather than the window elapsing normally), and a participant that
/// itself throws while the fault path tries to broadcast Faulted/Halted.
/// </summary>
public class WhizbangLifecycleStateCoverageTests {

  private sealed class FakeParticipant(string component, Func<LifecyclePhase, ValueTask>? onPhase = null)
      : IWhizbangRunControl {
    public string Component { get; } = component;
    public List<LifecyclePhase> Seen { get; } = [];
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Seen.Add(phase);
      return onPhase?.Invoke(phase) ?? default;
    }
  }

  private static async Task _until(Func<bool> condition) {
    for (var i = 0; i < 200 && !condition(); i++) {
      await Task.Yield();
    }
  }

  /// <summary>
  /// If this catch were dropped, a shutdown racing the fault record window would let the
  /// OperationCanceledException from the delay escape <c>_faultAndHaltAsync</c> uncaught — the
  /// instance would never reach the terminal Halted phase, and whatever awaits FaultAsync/
  /// AdvanceToAsync during shutdown would observe a faulted task instead of a clean halt.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task FaultAsync_CanceledDuringRecordWindow_StillReachesHaltedAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider();
    var options = new WhizbangLifecycleOptions { FaultRecordWindow = TimeSpan.FromSeconds(30) };
    var participant = new FakeParticipant("workers");
    var coordinator = new WhizbangLifecycleCoordinator([participant], options, time);
    var state = new WhizbangLifecycleState(coordinator, options, time);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    var fault = state.FaultAsync(cts.Token).AsTask();

    await _until(() => state.Phase == LifecyclePhase.Faulted);
    await Assert.That(fault.IsCompleted).IsFalse(); // still inside the record window

    // Shutdown lands mid-window instead of the window elapsing on its own — the delay observes
    // cancellation rather than FakeTimeProvider advancing past FaultRecordWindow.
    await cts.CancelAsync();
    await fault;

    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Halted)
      .Because("a cancellation observed DURING the record window must still drive Halted — the "
             + "catch exists precisely so 'shutting down' short-circuits the wait, not the halt");
    await Assert.That(participant.Seen).Contains(LifecyclePhase.Halted);
  }

  /// <summary>
  /// If this bare catch were removed, a resource that fails to record the fault/halt broadcast
  /// (a dead connection, a already-torn-down scope) would throw OUT of the best-effort broadcast
  /// and mask the fault path itself — the instance would never progress past Faulted, so the one
  /// piece of state an operator most needs (the system is dying) would silently vanish behind an
  /// unrelated broadcast failure.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task FaultAsync_ParticipantThrowsOnBroadcast_StillReachesHaltedAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider();
    var options = new WhizbangLifecycleOptions { FaultRecordWindow = TimeSpan.FromSeconds(5) };
    // Throws only when the FAULTED broadcast itself is attempted — the record window's own
    // best-effort re-broadcast (not the earlier successful transition into some other phase).
    var participant = new FakeParticipant("workers", phase =>
      phase == LifecyclePhase.Faulted ? throw new InvalidOperationException("recorder down") : default);
    var coordinator = new WhizbangLifecycleCoordinator([participant], options, time);
    var state = new WhizbangLifecycleState(coordinator, options, time);

    var fault = state.FaultAsync(CancellationToken.None).AsTask();

    // The broadcast throw is swallowed internally, so Phase still reaches Faulted synchronously
    // relative to the caller even though the participant never actually acknowledged it.
    await _until(() => state.Phase == LifecyclePhase.Faulted);
    await Assert.That(fault.IsCompleted).IsFalse(); // still inside the record window

    time.Advance(TimeSpan.FromSeconds(6));
    await fault;

    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Halted)
      .Because("a broadcast failure for one phase must not prevent the fault path from completing "
             + "and reaching the terminal Halted phase");
  }
}
