using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The bounded wait of a collective for its predecessor (#1003), counted from the first sink run that found the
/// predecessor missing, across every run after it.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
[Category("CollectiveEvents")]
public class CollectivePredecessorHoldsTests {
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  [Test]
  public async Task HoldFor_CountsFromTheFirstHold_AndEndsOnceTheWaitRunsOutAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var holds = new CollectivePredecessorHolds(clock);
    var eventId = Guid.CreateVersion7();

    var first = holds.HoldFor(eventId, _wait);
    clock.Advance(TimeSpan.FromSeconds(10));
    var second = holds.HoldFor(eventId, _wait);
    clock.Advance(TimeSpan.FromSeconds(20));
    var expired = holds.HoldFor(eventId, _wait);

    await Assert.That(first).IsEqualTo(_wait);
    await Assert.That(second).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(expired).IsEqualTo(TimeSpan.Zero);
    await Assert.That(holds.Count).IsEqualTo(0)
      .Because("a collective whose wait ran out applies, so it is forgotten");
  }

  [Test]
  public async Task Forget_EndsTheHoldAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var holds = new CollectivePredecessorHolds(clock);
    var eventId = Guid.CreateVersion7();
    holds.HoldFor(eventId, _wait);
    clock.Advance(TimeSpan.FromSeconds(10));

    holds.Forget(eventId);

    await Assert.That(holds.Count).IsEqualTo(0);
    await Assert.That(holds.HoldFor(eventId, _wait)).IsEqualTo(_wait)
      .Because("a forgotten collective that waits again starts a new wait");
  }

  /// <summary>
  /// A collective applied by another instance is never forgotten here; it is dropped once it has been held for twice
  /// the wait, so the map holds only collectives waiting now. The collective being asked about is never dropped.
  /// </summary>
  [Test]
  public async Task HoldFor_DropsHoldsLongPastTheirWaitAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var holds = new CollectivePredecessorHolds(clock);
    var appliedElsewhere = Guid.CreateVersion7();
    var stillWaiting = Guid.CreateVersion7();
    holds.HoldFor(appliedElsewhere, _wait);
    clock.Advance(TimeSpan.FromSeconds(45));
    holds.HoldFor(stillWaiting, _wait);
    clock.Advance(TimeSpan.FromSeconds(15));

    holds.HoldFor(stillWaiting, _wait);

    await Assert.That(holds.Count).IsEqualTo(1)
      .Because("the hold held for twice the wait is dropped; the one held for 15s stays");
  }
}
