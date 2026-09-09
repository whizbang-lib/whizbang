#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Locks the <c>priority</c> column (priority step 1) across the three work tables it lives on. The column
/// holds the row's effective priority as an INTEGER (lower is more urgent), never null, defaulting to the
/// standard band so a row nothing classified is scheduled as ordinary work rather than urgent work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#storage</docs>
[Category("Unit")]
public class PriorityColumnTests {
  private const int STANDARD = 150;

  [Test]
  public async Task Inbox_PriorityColumn_IsIntegerNotNullDefaultStandardAsync() {
    var priority = InboxSchema.Table.Columns.Single(c => c.Name == InboxSchema.Columns.PRIORITY);
    await Assert.That(priority.DataType).IsEqualTo(WhizbangDataType.INTEGER)
      .Because("the claim orders streams by the number and buckets it with arithmetic; an integer is what both need");
    await Assert.That(priority.Nullable).IsFalse()
      .Because("a NULL would force every read site to coalesce; the standard band is the miss");
    await Assert.That(priority.DefaultValue).IsEqualTo(DefaultValue.Integer(STANDARD));
  }

  [Test]
  public async Task Outbox_PriorityColumn_IsIntegerNotNullDefaultStandardAsync() {
    var priority = OutboxSchema.Table.Columns.Single(c => c.Name == OutboxSchema.Columns.PRIORITY);
    await Assert.That(priority.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(priority.Nullable).IsFalse();
    await Assert.That(priority.DefaultValue).IsEqualTo(DefaultValue.Integer(STANDARD));
  }

  [Test]
  public async Task PerspectiveEvents_PriorityColumn_IsIntegerNotNullDefaultStandardAsync() {
    var priority = PerspectiveEventsSchema.Table.Columns.Single(c => c.Name == PerspectiveEventsSchema.Columns.PRIORITY);
    await Assert.That(priority.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(priority.Nullable).IsFalse();
    await Assert.That(priority.DefaultValue).IsEqualTo(DefaultValue.Integer(STANDARD));
  }

  [Test]
  public async Task AllThreeTables_PriorityColumn_SharesTheSameNameAsync() {
    // The number is read by the same claim code on every table; one name keeps the SQL uniform.
    var inbox = InboxSchema.Columns.PRIORITY;
    var outbox = OutboxSchema.Columns.PRIORITY;
    var perspectiveEvents = PerspectiveEventsSchema.Columns.PRIORITY;
    await Assert.That(inbox).IsEqualTo("priority");
    await Assert.That(outbox).IsEqualTo("priority");
    await Assert.That(perspectiveEvents).IsEqualTo("priority");
  }
}
