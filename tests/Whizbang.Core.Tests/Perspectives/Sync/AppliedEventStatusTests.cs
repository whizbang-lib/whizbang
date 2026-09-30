using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// The ledger's answer settles a wait only when nothing is left to wait for (#959).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/AppliedEventStatus.cs</code-under-test>
public class AppliedEventStatusTests {
  [Test]
  [Arguments(AppliedEventState.NotArrived, false)]
  [Arguments(AppliedEventState.Pending, false)]
  [Arguments(AppliedEventState.Applied, true)]
  [Arguments(AppliedEventState.NotApplicable, true)]
  public async Task IsSettled_IsTrueOnlyWhenNothingIsLeftToWaitForAsync(AppliedEventState state, bool settled) {
    var status = new AppliedEventStatus(state, Guid.CreateVersion7());

    await Assert.That(status.IsSettled).IsEqualTo(settled);
  }

  [Test]
  public async Task Inquiry_ByEventId_LeavesTheStreamUnsetAsync() {
    var eventId = Guid.CreateVersion7();

    var inquiry = new AppliedEventInquiry("Orders.OrderPerspective", eventId);

    await Assert.That(inquiry.EventId).IsEqualTo(eventId);
    await Assert.That(inquiry.StreamId).IsNull();
    await Assert.That(inquiry.StreamPosition).IsNull();
  }
}
