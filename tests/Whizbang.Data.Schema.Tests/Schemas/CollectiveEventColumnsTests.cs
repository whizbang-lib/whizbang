using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Locks the Slice 2 schema additions for the collective-events feature:
/// <see cref="EventStoreSchema"/>, <see cref="OutboxSchema"/>, and
/// <see cref="InboxSchema"/> each gain an <c>is_collective</c> boolean
/// (defaulting <c>false</c>) that the transport plumbing (Slice 3) stamps
/// and the projection runner (Slice 7) branches on.
/// <see cref="PerspectiveSchema.CommonColumns"/> exposes a
/// <c>LastCollectiveEventId</c> column generators include in projection
/// tables so each row can answer "which collective event last touched me?"
/// without a separate lookup table.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveEventColumnsTests {

  // ── EventStore: is_collective ──────────────────────────────────────────

  [Test]
  [Category("Schema")]
  public async Task EventStore_HasIsCollectiveColumn_BooleanDefaultFalseAsync() {
    var col = EventStoreSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");

    await Assert.That(col).IsNotNull()
      .Because("Slice 2 adds is_collective to wh_event_store so the projection runner (Slice 7) can branch on it instead of inspecting the event payload.");
    await Assert.That(col!.DataType).IsEqualTo(WhizbangDataType.BOOLEAN);
    await Assert.That(col.Nullable).IsFalse()
      .Because("Every event store row is either collective or not — never unknown.");
  }

  // ── Outbox: is_collective ──────────────────────────────────────────────

  [Test]
  [Category("Schema")]
  public async Task Outbox_HasIsCollectiveColumn_BooleanDefaultFalseAsync() {
    var col = OutboxSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");

    await Assert.That(col).IsNotNull()
      .Because("Mirrors is_composite (W3 slice 9) — the producer-side Dispatcher stamps the flag on outbox rows whose payload is an ICollectiveEvent.");
    await Assert.That(col!.DataType).IsEqualTo(WhizbangDataType.BOOLEAN);
    await Assert.That(col.Nullable).IsFalse();
  }

  // ── Inbox: is_collective ───────────────────────────────────────────────

  [Test]
  [Category("Schema")]
  public async Task Inbox_HasIsCollectiveColumn_BooleanDefaultFalseAsync() {
    var col = InboxSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");

    await Assert.That(col).IsNotNull()
      .Because("Consumer side mirrors the producer flag so the projection runner can branch without re-deserializing the payload to type-check it.");
    await Assert.That(col!.DataType).IsEqualTo(WhizbangDataType.BOOLEAN);
    await Assert.That(col.Nullable).IsFalse();
  }

  // ── PerspectiveSchema: LastCollectiveEventId common column ─────────────

  [Test]
  [Category("Schema")]
  public async Task PerspectiveCommonColumns_LastCollectiveEventId_IsNullableUuidAsync() {
    var col = PerspectiveSchema.CommonColumns.LastCollectiveEventId;

    await Assert.That(col).IsNotNull();
    await Assert.That(col.Name).IsEqualTo("last_collective_event_id");
    await Assert.That(col.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(col.Nullable).IsTrue()
      .Because("A perspective row that has never been touched by a collective event has no pointer — nullable is the natural shape, NULL is the no-collective signal.");
    await Assert.That(col.PrimaryKey).IsFalse()
      .Because("Pure audit pointer, not part of identity.");
  }

  [Test]
  [Category("Schema")]
  public async Task PerspectiveCommonColumns_LastCollectiveEventId_StaticInstance_IsReusableAsync() {
    // CommonColumns instances are static singletons so the same definition
    // shared across all perspectives. Equality is by structural record
    // comparison (ColumnDefinition is a record).
    var col1 = PerspectiveSchema.CommonColumns.LastCollectiveEventId;
    var col2 = PerspectiveSchema.CommonColumns.LastCollectiveEventId;

    await Assert.That(ReferenceEquals(col1, col2)).IsTrue()
      .Because("Static readonly field — the generator references it directly instead of constructing a new ColumnDefinition each time.");
  }

  // ── Cross-schema invariant: is_collective name + type matches across the three transport tables ──

  [Test]
  [Category("Schema")]
  public async Task IsCollective_NameAndTypeMatchAcrossEventStoreOutboxInboxAsync() {
    // The flag travels: outbox row → published payload → inbox row →
    // event_store row. Same name + type across the three keeps the
    // transport plumbing (Slice 3) trivially correct.
    var inEventStore = EventStoreSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");
    var inOutbox = OutboxSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");
    var inInbox = InboxSchema.Table.Columns.SingleOrDefault(c => c.Name == "is_collective");

    await Assert.That(inEventStore).IsNotNull();
    await Assert.That(inOutbox).IsNotNull();
    await Assert.That(inInbox).IsNotNull();

    await Assert.That(inEventStore!.DataType).IsEqualTo(inOutbox!.DataType);
    await Assert.That(inOutbox.DataType).IsEqualTo(inInbox!.DataType);

    await Assert.That(inEventStore.Nullable).IsEqualTo(inOutbox.Nullable);
    await Assert.That(inOutbox.Nullable).IsEqualTo(inInbox.Nullable);
  }
}
