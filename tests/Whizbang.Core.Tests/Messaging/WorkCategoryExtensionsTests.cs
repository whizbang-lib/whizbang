using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the wire-form mapping in <see cref="WorkCategoryExtensions.ToSqlCategory"/>.
/// The SQL functions (claim_work, complete_*, etc.) consume these snake_case
/// strings directly — any rename here is a silent breaking change to every
/// SQL call site, so the mapping must stay frozen.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class WorkCategoryExtensionsTests {

  [Test]
  public async Task ToSqlCategory_Outbox_ReturnsOutboxAsync() {
    await Assert.That(WorkCategory.Outbox.ToSqlCategory()).IsEqualTo("outbox");
  }

  [Test]
  public async Task ToSqlCategory_Inbox_ReturnsInboxAsync() {
    await Assert.That(WorkCategory.Inbox.ToSqlCategory()).IsEqualTo("inbox");
  }

  [Test]
  public async Task ToSqlCategory_PerspectiveEvent_ReturnsSnakeCaseAsync() {
    // Snake_case wire form — the PG functions accept "perspective_event" exactly,
    // not "perspectiveEvent" or "PerspectiveEvent".
    await Assert.That(WorkCategory.PerspectiveEvent.ToSqlCategory())
      .IsEqualTo("perspective_event");
  }

  [Test]
  public async Task ToSqlCategory_UnknownValue_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Cast an unmapped int into the enum — exercises the switch fallthrough that
    // ensures a stale call site can't pass garbage through to the database.
    var unknown = (WorkCategory)999;

    await Assert.That(() => unknown.ToSqlCategory())
      .Throws<ArgumentOutOfRangeException>();
  }
}
