using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangLifecycleCoordinator"/>: it broadcasts a phase to every participant and
/// coordinates it — the <b>barrier</b> (awaits all acks before returning), the <b>queue</b> (serializes
/// transitions), and the <b>timeout</b> (a participant that doesn't ack in time raises
/// <see cref="LifecycleAckTimeoutException"/>). Completion-signal + FakeTimeProvider based — no delays.
/// </summary>
public class WhizbangLifecycleCoordinatorTests {

  private sealed class FakeParticipant(string component, Func<LifecyclePhase, ValueTask>? onPhase = null)
      : IWhizbangRunControl {
    public string Component { get; } = component;
    public List<LifecyclePhase> Seen { get; } = [];
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Seen.Add(phase);
      return onPhase?.Invoke(phase) ?? default;
    }
  }

  [Test]
  public async Task Transition_BroadcastsToEveryParticipantAsync() {
    var a = new FakeParticipant("a");
    var b = new FakeParticipant("b");
    var coordinator = new WhizbangLifecycleCoordinator([a, b], new WhizbangLifecycleOptions());

    await coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None);

    await Assert.That(a.Seen).IsEquivalentTo([LifecyclePhase.Running]);
    await Assert.That(b.Seen).IsEquivalentTo([LifecyclePhase.Running]);
  }

  [Test]
  public async Task Transition_Barrier_WaitsForAllAcksBeforeReturningAsync() {
    var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var a = new FakeParticipant("a");
    var b = new FakeParticipant("b", _ => new ValueTask(releaseB.Task));
    var coordinator = new WhizbangLifecycleCoordinator([a, b], new WhizbangLifecycleOptions());

    var transition = coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None).AsTask();

    await Assert.That(transition.IsCompleted).IsFalse(); // barrier: still waiting on B's ack
    await Assert.That(a.Seen).IsEquivalentTo([LifecyclePhase.Running]); // A was invoked

    releaseB.SetResult();
    await transition; // now all acked
    await Assert.That(transition.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task Transition_Queue_SerializesTransitionsAsync() {
    var gateMigrating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var migratingInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runningInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var participant = new FakeParticipant("workers", phase => phase switch {
      LifecyclePhase.Migrating => Invoke(migratingInvoked, gateMigrating.Task),
      LifecyclePhase.Running => Invoke(runningInvoked, Task.CompletedTask),
      _ => default
    });
    var coordinator = new WhizbangLifecycleCoordinator([participant], new WhizbangLifecycleOptions());

    var first = coordinator.TransitionAsync(LifecyclePhase.Migrating, CancellationToken.None).AsTask();
    var second = coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None).AsTask();

    await migratingInvoked.Task; // first transition is in flight
    await Assert.That(runningInvoked.Task.IsCompleted).IsFalse(); // second is queued behind the first

    gateMigrating.SetResult();
    await first;
    await second;
    await Assert.That(runningInvoked.Task.IsCompleted).IsTrue();
    await Assert.That(participant.Seen).IsEquivalentTo([LifecyclePhase.Migrating, LifecyclePhase.Running]);

    static ValueTask Invoke(TaskCompletionSource invoked, Task gate) {
      invoked.TrySetResult();
      return new ValueTask(gate);
    }
  }

  [Test]
  public async Task Transition_Timeout_RaisesAckTimeoutAsync() {
    var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stuck = new FakeParticipant("transport", _ => new ValueTask(never.Task));
    var options = new WhizbangLifecycleOptions { TransitionAckTimeout = TimeSpan.FromSeconds(30) };
    var time = new FakeTimeProvider();
    var coordinator = new WhizbangLifecycleCoordinator([stuck], options, time);

    var transition = coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None).AsTask();
    await Assert.That(transition.IsCompleted).IsFalse();

    time.Advance(TimeSpan.FromSeconds(31)); // blow the ack budget

    var ex = await Assert.That(async () => await transition).ThrowsExactly<LifecycleAckTimeoutException>();
    await Assert.That(ex!.Component).IsEqualTo("transport");
    await Assert.That(ex.Phase).IsEqualTo(LifecyclePhase.Running);
  }

  [Test]
  public async Task Transition_NoParticipants_IsNoOpAsync() {
    var coordinator = new WhizbangLifecycleCoordinator([], new WhizbangLifecycleOptions());
    await coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None); // does not throw
  }
}
