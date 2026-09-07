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
    var coordinator = new LifecycleCoordinator();
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
}
