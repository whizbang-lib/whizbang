using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangLifecycleState"/>: advancing the phase broadcasts it to every participant
/// and updates <see cref="IWhizbangLifecycleState.Phase"/>, and a failed transition drives the fault
/// path — <see cref="LifecyclePhase.Faulted"/> (a bounded record window) then terminal
/// <see cref="LifecyclePhase.Halted"/>. FakeTimeProvider based — no real delays.
/// </summary>
public class WhizbangLifecycleStateTests {

  private sealed class FakeParticipant(string component, Func<LifecyclePhase, ValueTask>? onPhase = null)
      : IWhizbangRunControl {
    public string Component { get; } = component;
    public List<LifecyclePhase> Seen { get; } = [];
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Seen.Add(phase);
      return onPhase?.Invoke(phase) ?? default;
    }
  }

  private static (WhizbangLifecycleState state, FakeParticipant participant) _build(
      Func<LifecyclePhase, ValueTask>? onPhase = null, FakeTimeProvider? time = null) {
    var participant = new FakeParticipant("workers", onPhase);
    var options = new WhizbangLifecycleOptions();
    var coordinator = new WhizbangLifecycleCoordinator([participant], options, time);
    return (new WhizbangLifecycleState(coordinator, options, time), participant);
  }

  [Test]
  public async Task StartsAtStartingAsync() {
    var (state, _) = _build();
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Starting);
  }

  [Test]
  public async Task AdvanceTo_BroadcastsAndUpdatesPhaseAsync() {
    var (state, participant) = _build();

    await state.AdvanceToAsync(LifecyclePhase.Migrating, CancellationToken.None);
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Migrating);

    await state.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None);
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Running);

    await Assert.That(participant.Seen).IsEquivalentTo([LifecyclePhase.Migrating, LifecyclePhase.Running]);
  }

  [Test]
  public async Task AdvanceTo_ParticipantThrows_FaultsThenHaltsAfterRecordWindowAsync() {
    var time = new FakeTimeProvider();
    var options = new WhizbangLifecycleOptions { FaultRecordWindow = TimeSpan.FromSeconds(5) };
    // Participant throws only on the operational transition; it acks the Faulted/Halted record phases.
    var participant = new FakeParticipant("workers", phase =>
      phase == LifecyclePhase.Running ? throw new InvalidOperationException("boom") : default);
    var coordinator = new WhizbangLifecycleCoordinator([participant], options, time);
    var state = new WhizbangLifecycleState(coordinator, options, time);

    // AdvanceTo does not rethrow — the throw drives Faulted, then it parks in the record window.
    var advance = state.AdvanceToAsync(LifecyclePhase.Running, CancellationToken.None).AsTask();

    await _until(() => state.Phase == LifecyclePhase.Faulted);
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Faulted);
    await Assert.That(advance.IsCompleted).IsFalse(); // still inside the record window
    await Assert.That(participant.Seen).Contains(LifecyclePhase.Faulted);
    await Assert.That(participant.Seen).DoesNotContain(LifecyclePhase.Halted);

    time.Advance(TimeSpan.FromSeconds(6)); // record window elapses → Halted
    await advance;
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Halted);
    await Assert.That(participant.Seen).Contains(LifecyclePhase.Halted);
  }

  private static async Task _until(Func<bool> condition) {
    for (var i = 0; i < 200 && !condition(); i++) {
      await Task.Yield();
    }
  }
}
