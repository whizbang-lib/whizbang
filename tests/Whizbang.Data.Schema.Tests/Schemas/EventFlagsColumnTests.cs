#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Locks the <c>flags</c> column (Slice 2') across the three tables it
/// lives on. The column stores <c>Whizbang.Core.Messaging.EventFlags</c>
/// as an INTEGER bitmask; default 0; never null. Replacing two booleans
/// (is_collective + is_composite) with one INTEGER means future event
/// categories ship by adding a flag value — no migration.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class EventFlagsColumnTests {

  [Test]
  public async Task EventStore_FlagsColumn_IsIntegerNotNullDefaultZeroAsync() {
    var flags = EventStoreSchema.Table.Columns.Single(c => c.Name == EventStoreSchema.Columns.FLAGS);

    await Assert.That(flags.DataType).IsEqualTo(WhizbangDataType.INTEGER)
      .Because("Bitmask backing — INTEGER lets the framework do `WHERE (flags & 1) = 1` at index speed.");
    await Assert.That(flags.Nullable).IsFalse()
      .Because("Default 0 means 'no flags'; a NULL would force every read site to coalesce, contradicting the purpose.");
  }

  [Test]
  public async Task Outbox_FlagsColumn_IsIntegerNotNullDefaultZeroAsync() {
    var flags = OutboxSchema.Table.Columns.Single(c => c.Name == OutboxSchema.Columns.FLAGS);
    await Assert.That(flags.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(flags.Nullable).IsFalse();
  }

  [Test]
  public async Task Inbox_FlagsColumn_IsIntegerNotNullDefaultZeroAsync() {
    var flags = InboxSchema.Table.Columns.Single(c => c.Name == InboxSchema.Columns.FLAGS);
    await Assert.That(flags.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(flags.Nullable).IsFalse();
  }

  [Test]
  public async Task AllThreeTables_FlagsColumn_SharesTheSameNameAsync() {
    // The flag value is preserved through transport: producer stamps it
    // on the outbox, consumer copies it onto the inbox, the event store
    // carries it on the row. All three columns are 'flags' — anything
    // else would force the transport-preserve path to rename.
    var evtStoreFlags = EventStoreSchema.Columns.FLAGS;
    var outboxFlags = OutboxSchema.Columns.FLAGS;
    var inboxFlags = InboxSchema.Columns.FLAGS;
    await Assert.That(evtStoreFlags).IsEqualTo("flags");
    await Assert.That(outboxFlags).IsEqualTo("flags");
    await Assert.That(inboxFlags).IsEqualTo("flags");
  }
}
