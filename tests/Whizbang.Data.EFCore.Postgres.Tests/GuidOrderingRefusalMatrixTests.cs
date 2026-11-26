// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The refusal matrix on PostgreSQL, EF Core driver (#2528): every outbox write, inbox write and event store append,
/// crossed with every non-v7 shape (versions 1, 3, 4, 5, 6, 8, the empty id, the max UUID and a raw hash), is refused
/// with <see cref="GuidOrderingException"/> before any SQL runs, and nothing reaches <c>wh_outbox</c>, <c>wh_inbox</c>,
/// <c>wh_event_store</c> or the stream's bookkeeping. A v7 id is accepted on each, the control that the scan for rows
/// can see a row. A batch holding one refused id writes none of its rows.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
[Category("Shard5")]
public class GuidOrderingRefusalMatrixTests : EFCoreTestBase {
  public static IEnumerable<Func<Guid>> NonV7Ids() {
    foreach (var id in new[] {
        Guid.Parse("c232ab00-9414-11ec-b3c8-9f6bdeced846"), // v1
        Guid.Parse("6fa459ea-ee8a-3ca4-894e-db77e160355e"), // v3
        Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8"), // v4
        Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d"), // v5
        Guid.Parse("1ec9414c-232a-6b00-b3c8-9f6bdeced846"), // v6
        Guid.Parse("320c3d4d-cc00-875b-8ec9-32d5f69181c0"), // v8
        Guid.Empty,
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), // max, version 15
        Guid.Parse("a3f1c2d4-5e6f-b123-9abc-def012345678"), // raw hash, nibble 11
      }) {
      yield return () => id;
    }
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext context) =>
    new(context, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task StoreOutbox_NonV7_IsRefusedAndStoresNothingAsync(Guid id) {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();

    var thrown = await Assert.That(async () =>
        await _coordinator(context).StoreOutboxMessagesAsync([_outbox(id, stream)], partitionCount: 100))
      .Throws<GuidOrderingException>();

    await Assert.That(thrown!.Id).IsEqualTo(id);
    await Assert.That(thrown.EntryPoint).IsEqualTo("EFCoreWorkCoordinator.OutboxWrite");
    await _assertNothingStoredAsync(context, stream, id);
  }

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task StoreInbox_NonV7_IsRefusedAndStoresNothingAsync(Guid id) {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();

    var thrown = await Assert.That(async () =>
        await _coordinator(context).StoreInboxMessagesAsync([_inbox(id, stream)], partitionCount: 100))
      .Throws<GuidOrderingException>();

    await Assert.That(thrown!.EntryPoint).IsEqualTo("EFCoreWorkCoordinator.InboxWrite");
    await _assertNothingStoredAsync(context, stream, id);
  }

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task StoreDeadLetters_NonV7_IsRefusedAndStoresNothingAsync(Guid id) {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var coordinator = _coordinator(context);

    await Assert.That(async () => await coordinator.StoreOutboxDeadLettersAsync([_outbox(id, stream)],
        new OutboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, "refused", 1, (Guid)TrackedGuid.New(), "gen-1")))
      .Throws<GuidOrderingException>();
    await Assert.That(async () => await coordinator.StoreInboxDeadLettersAsync([_inbox(id, stream)],
        new InboxDeadLetterRequest(MessageFailureReason.InboxStoreFailed, "refused", 1, (Guid)TrackedGuid.New(), "gen-1")))
      .Throws<GuidOrderingException>();

    await _assertNothingStoredAsync(context, stream, id);
  }

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task EventStoreAppend_NonV7_IsRefusedAndStoresNothingAsync(Guid id) {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var thrown = await Assert.That(async () => await store.AppendAsync(stream, _envelope(id, stream)))
      .Throws<GuidOrderingException>();

    await Assert.That(thrown!.EntryPoint).IsEqualTo("EFCoreEventStore.Append");
    await Assert.That(thrown.MessageType).IsEqualTo(nameof(MixedStreamProbeNoted));
    await _assertNothingStoredAsync(context, stream, id);
  }

  /// <summary>The control: a v7 id is stored on every entry point, so the scans above can see a row.</summary>
  [Test]
  public async Task EveryEntryPoint_AcceptsAV7_AndTheRowIsVisibleAsync() {
    await using var context = CreateDbContext();
    var coordinator = _coordinator(context);
    var outboxStream = (Guid)TrackedGuid.New();
    var outboxId = (Guid)TrackedGuid.New();
    var inboxStream = (Guid)TrackedGuid.New();
    var inboxId = (Guid)TrackedGuid.New();
    var appendStream = (Guid)TrackedGuid.New();
    var appendId = (Guid)TrackedGuid.New();

    await coordinator.StoreOutboxMessagesAsync([_outbox(outboxId, outboxStream)], partitionCount: 100);
    await coordinator.StoreInboxMessagesAsync([_inbox(inboxId, inboxStream)], partitionCount: 100);
    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(appendStream, _envelope(appendId, appendStream));

    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_outbox WHERE message_id = @id", outboxId)).IsEqualTo(1L);
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_inbox WHERE message_id = @id", inboxId)).IsEqualTo(1L);
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_event_store WHERE event_id = @id", appendId)).IsEqualTo(1L);
  }

  /// <summary>
  /// A non-v7 stream id is stored by default (reported only), and refused with nothing stored when stream ids are raised
  /// to Error.
  /// </summary>
  [Test]
  public async Task EventStoreAppend_NonV7StreamId_IsStoredByDefault_AndRefusedAtErrorAsync() {
    await using var context = CreateDbContext();
    var reported = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");
    var refused = Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d");
    var strict = new GuidOrderingValidator(
      new Whizbang.Core.Configuration.WhizbangOptions { StreamIdOrderingViolationSeverity = Whizbang.Core.Configuration.GuidOrderingSeverity.Error },
      Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(reported, _envelope(TrackedGuid.New(), reported));
    await Assert.That(async () => await new EFCoreEventStore<WorkCoordinationDbContext>(context, guidOrdering: strict)
        .AppendAsync(refused, _envelope(TrackedGuid.New(), refused)))
      .Throws<GuidOrderingException>();

    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", reported, reported)).IsEqualTo(1L);
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", refused, refused)).IsEqualTo(0L);
  }

  /// <summary>
  /// A type carrying [SuppressGuidOrderingValidation] opts out (#2528): its non-v7 event id is appended and its outbox
  /// and inbox rows written, while the empty id is refused for it with nothing stored, and the control above shows an
  /// unattributed type is refused.
  /// </summary>
  [Test]
  public async Task ASuppressedType_IsStoredUnderANonV7Id_ButNeverUnderTheEmptyIdAsync() {
    await using var context = CreateDbContext();
    var coordinator = _coordinator(context);
    var v4 = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");
    var v5 = Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d");
    var v8 = Guid.Parse("320c3d4d-cc00-875b-8ec9-32d5f69181c0");
    var appendStream = (Guid)TrackedGuid.New();
    var suppressedName = Whizbang.Core.TypeNameFormatter.Format(typeof(SuppressedOrderingProbeNoted));

    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(appendStream, _suppressedEnvelope(v4, appendStream));
    await coordinator.StoreOutboxMessagesAsync([_outbox(v5, (Guid)TrackedGuid.New()) with { MessageType = suppressedName }], partitionCount: 100);
    await coordinator.StoreInboxMessagesAsync([_inbox(v8, (Guid)TrackedGuid.New()) with { MessageType = suppressedName }], partitionCount: 100);

    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_event_store WHERE event_id = @id", v4)).IsEqualTo(1L);
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_outbox WHERE message_id = @id", v5)).IsEqualTo(1L);
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_inbox WHERE message_id = @id", v8)).IsEqualTo(1L);

    var emptyStream = (Guid)TrackedGuid.New();
    await Assert.That(async () => await new EFCoreEventStore<WorkCoordinationDbContext>(context)
        .AppendAsync(emptyStream, _suppressedEnvelope(Guid.Empty, emptyStream)))
      .Throws<GuidOrderingException>();
    await _assertNothingStoredAsync(context, emptyStream, Guid.Empty);
  }

  private static MessageEnvelope<SuppressedOrderingProbeNoted> _suppressedEnvelope(Guid id, Guid stream) => new() {
    MessageId = new MessageId(id),
    Payload = new SuppressedOrderingProbeNoted { StreamId = stream },
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>A batch holding one refused id writes none of its rows: the check runs before any SQL.</summary>
  [Test]
  public async Task ABatchWithOneRefusedId_StoresNoneOfItsRowsAsync() {
    await using var context = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var good = (Guid)TrackedGuid.New();
    var bad = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");

    await Assert.That(async () =>
        await _coordinator(context).StoreOutboxMessagesAsync([_outbox(good, stream), _outbox(bad, stream)], partitionCount: 100))
      .Throws<GuidOrderingException>();
    await Assert.That(async () =>
        await _coordinator(context).StoreInboxMessagesAsync([_inbox(good, stream), _inbox(bad, stream)], partitionCount: 100))
      .Throws<GuidOrderingException>();

    await _assertNothingStoredAsync(context, stream, good);
    await _assertNothingStoredAsync(context, stream, bad);
  }

  private static async Task _assertNothingStoredAsync(WorkCoordinationDbContext context, Guid stream, Guid id) {
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_outbox WHERE message_id = @id OR stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no outbox row");
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_inbox WHERE message_id = @id OR stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no inbox row");
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_event_store WHERE event_id = @id OR stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no event row");
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_active_streams WHERE stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no stream row");
    await Assert.That(await _countAsync(context, "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no cursor");
  }

  private static async Task<long> _countAsync(WorkCoordinationDbContext context, string sql, Guid id, Guid? stream = null) {
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(stream), stream ?? id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static OutboxMessage _outbox(Guid id, Guid stream) => new() {
    MessageId = id,
    Destination = "guid-ordering-topic",
    Envelope = _jsonEnvelope(id),
    Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
    MessageType = "TestMessage, TestAssembly",
    StreamId = stream,
    IsEvent = true,
  };

  private static InboxMessage _inbox(Guid id, Guid stream) => new() {
    MessageId = id,
    HandlerName = "ProbeHandler",
    Envelope = _jsonEnvelope(id),
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
    MessageType = "TestMessage, TestAssembly",
    StreamId = stream,
    IsEvent = true,
    Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
  };

  // The unchecked constructor: the strict From would refuse these before the entry point is reached.
  private static MessageEnvelope<JsonElement> _jsonEnvelope(Guid id) => new() {
    MessageId = new MessageId(id),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [],
    Payload = JsonElement.Parse("{}"),
  };

  private static MessageEnvelope<MixedStreamProbeNoted> _envelope(Guid id, Guid stream) => new() {
    MessageId = new MessageId(id),
    Payload = new MixedStreamProbeNoted { OrderId = stream },
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}

/// <summary>An event type that opts out of the event and message id time-ordering check (#2528).</summary>
[Whizbang.Core.SuppressGuidOrderingValidation]
public sealed record SuppressedOrderingProbeNoted : Whizbang.Core.IEvent {
  /// <summary>The stream.</summary>
  [Whizbang.Core.StreamId]
  public required Guid StreamId { get; init; }
}
