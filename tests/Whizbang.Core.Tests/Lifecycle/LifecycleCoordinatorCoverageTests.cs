using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Covers the "already complete" idempotency branch of the private
/// <c>PerspectiveWhenAllState.TrySignalAndCheck</c>, reached through
/// <see cref="LifecycleCoordinator.SignalPerspectiveComplete"/>. The sibling
/// <c>LifecycleCoordinatorTests.SignalPerspectiveComplete_DuplicateSignal_IdempotentAsync</c> signals
/// the SAME perspective twice before the set is fully complete, which exercises a different early
/// return (the "not everyone has signaled yet" branch). This covers signaling again AFTER the set
/// is already fully complete — the state is never removed from the coordinator's dictionary on
/// perspective completion (unlike the WhenAll/segment-completion path), so a late or duplicate
/// signal must be a deterministic, idempotent no-op rather than re-firing PostAllPerspectives.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleCoordinatorCoverageTests {

  [Test]
  public async Task SignalPerspectiveComplete_AfterAllAlreadyComplete_ReturnsFalseAsync() {
    // If this regressed to "return true" again, a late-arriving duplicate signal (a redelivered
    // completion notification, say) would fire PostAllPerspectives / PostLifecycle a second time
    // for the same event — a duplicate downstream side effect.
    var coordinator = new LifecycleCoordinator(logger: NullLogger<LifecycleCoordinator>.Instance);
    var eventId = Guid.NewGuid();
    coordinator.ExpectPerspectiveCompletions(eventId, ["OnlyPerspective"]);

    var first = coordinator.SignalPerspectiveComplete(eventId, "OnlyPerspective");
    var second = coordinator.SignalPerspectiveComplete(eventId, "OnlyPerspective");

    await Assert.That(first).IsTrue()
      .Because("the single expected perspective just signaled — this is the real completion.");
    await Assert.That(second).IsFalse()
      .Because("the set is already complete; a further signal (even for the same perspective) must not report completion again.");
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue();
  }

  // ExpectCompletionsFrom is called once per event by whichever path opens the fan-in. Two paths
  // racing to open the same event's latch (a cascade and its originating dispatch both deciding
  // they own the post-lifecycle) must not replace a latch that already has signals recorded
  // against it: the replacement would forget them and the event's PostLifecycle would never fire.
  [Test]
  public async Task ExpectCompletionsFrom_CalledTwiceForOneEvent_KeepsTheLatchThatIsAlreadyCountingAsync() {
    var coordinator = new LifecycleCoordinator(logger: NullLogger<LifecycleCoordinator>.Instance);
    var eventId = Guid.NewGuid();
    var provider = new ServiceCollection().BuildServiceProvider();

    coordinator.ExpectCompletionsFrom(
      eventId, PostLifecycleCompletionSource.Local, PostLifecycleCompletionSource.Outbox);

    // One of the two expected segments reports in against the first latch.
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, provider, CancellationToken.None);

    // A second registration for the same event must be ignored rather than install a fresh latch.
    coordinator.ExpectCompletionsFrom(
      eventId, PostLifecycleCompletionSource.Local, PostLifecycleCompletionSource.Outbox);

    // If the latch had been replaced, this second segment would leave the Dispatcher signal
    // missing and the fan-in would still be waiting; with the original latch it completes.
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Outbox, provider, CancellationToken.None);

    // Firing removes the latch, so a third signal finds nothing to fan in — the observable proof
    // that the second segment really did complete the set.
    await Assert.That(coordinator.SignalSegmentCompleteAsync(
        eventId, PostLifecycleCompletionSource.Outbox, provider, CancellationToken.None).AsTask())
      .ThrowsNothing()
      .Because("once the fan-in has fired its state is gone, so a late duplicate signal is a no-op "
        + "rather than an error");
  }

  // The latch fires exactly once. Two segments finishing at the same instant both find the state
  // still in the dictionary — the removal happens after the fire — so both call in; the flag is
  // what stops the second one reporting completion as well. Without it PostLifecycle runs twice
  // for one event, which means duplicate detached receptors and duplicate downstream effects.
  [Test]
  public async Task WhenAllState_SignaledAgainAfterFiring_ReportsNoSecondCompletionAsync() {
    PostLifecycleCompletionSource[] expected = [
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Outbox,
    ];
    var state = new LifecycleCoordinator.WhenAllState(expected);

    var partial = state.TrySignalAndComplete(PostLifecycleCompletionSource.Local);
    var completing = state.TrySignalAndComplete(PostLifecycleCompletionSource.Outbox);
    var afterFiring = state.TrySignalAndComplete(PostLifecycleCompletionSource.Local);

    await Assert.That(partial).IsFalse()
      .Because("one of two expected segments is not the whole fan-in");
    await Assert.That(completing).IsTrue()
      .Because("the second expected segment completes the set — this is the one true answer");
    await Assert.That(afterFiring).IsFalse()
      .Because("a signal arriving after the latch has fired must not report completion again, or "
        + "PostLifecycle fires twice for one event");
  }
}
