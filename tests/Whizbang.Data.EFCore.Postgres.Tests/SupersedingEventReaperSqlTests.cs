// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A superseding event appended directly, keyed by its model.</summary>
[Superseding]
public sealed record SupersedingProbeEmbedded : IEvent {
  /// <summary>The document, which is the stream.</summary>
  [StreamId]
  public Guid DocumentId { get; init; }

  /// <summary>The embedding model, which is the key.</summary>
  [SupersedingKey]
  public string Model { get; init; } = "";
}

/// <summary>
/// Superseding (latest-wins) event types in SQL (#1729, migration 295): every write path stamps the key on a flagged row
/// and queues the older events of its key, and the reaper (perform_maintenance Task 16) deletes a queued event with its
/// body and its digest contribution only past every gate: one test per gate, each with the control that shows the
/// harness sees the other outcome.
/// </summary>
/// <docs>fundamentals/events/superseding-events</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/295_SupersedingEvents.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class SupersedingEventReaperSqlTests : EFCoreTestBase {
  private const string TYPE = "Probe.Contracts.EmbeddingUpdated, Probe.Contracts";
  private const string OTHER_TYPE = "Probe.Contracts.ProfileRenamed, Probe.Contracts";
  private const int SUPERSEDING = 128;
  private const int AT_MOST_ONCE = 32;
  private const int EPHEMERAL = 8;

  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

  // ===== The key and the queue =====

  [Test]
  public async Task Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    var first = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "model-a"));
    var second = await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "model-a"));
    var unflagged = await _emitAsync(conn, _t0.AddMinutes(2), (stream, OTHER_TYPE, 0, "model-a"));
    var keyless = await _emitAsync(conn, _t0.AddMinutes(3), (stream, TYPE, SUPERSEDING, null));

    await Assert.That(await _keyAsync(conn, first[0])).IsEqualTo("model-a");
    await Assert.That(await _keyAsync(conn, second[0])).IsEqualTo("model-a");
    await Assert.That(await _keyAsync(conn, unflagged[0])).IsNull()
      .Because("a key is read only from a row flagged superseding, so a stray 'sk' never makes an event reapable");
    await Assert.That(await _keyAsync(conn, keyless[0])).IsNull()
      .Because("a superseding event whose key is null is never superseded");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([first[0]])
      .Because("storing the second event of a key queues the first; nothing else has an older event of its key");
  }

  [Test]
  public async Task Emit_TwoEventsOfOneKeyInOneBatch_QueueTheOlderAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);

    await _emitAsync(conn, _t0, ids, (stream, TYPE, SUPERSEDING, "k"), (stream, TYPE, SUPERSEDING, "k"));

    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([ids[0]])
      .Because("the queue is written after the batch is stored, so it sees an older event stored in the same batch");
  }

  [Test]
  public async Task Emit_FlagsWrittenAsNames_StampTheKeyOfASupersedingEventAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();

    await _executeAsync(conn, "SELECT commit_handler_result(@req::jsonb)", ("req", $$"""
      {
        "instance_id": "{{(Guid)TrackedGuid.New()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{id}}", "Destination": "out", "MessageType": "{{TYPE}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"model": "m"}, "MessageId": "{{id}}", "Hops": []},
          "Metadata": {"sk": "named-key"}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": "Superseding"
        }]
      }
      """));

    await Assert.That(await _scalarAsync(conn, "SELECT flags FROM wh_event_store WHERE event_id = @id", ("id", id))).IsEqualTo(128L)
      .Because("the flag name parser knows Superseding, so a name list stores its bit instead of refusing the event");
    await Assert.That(await _keyAsync(conn, id)).IsEqualTo("named-key");
  }

  [Test]
  public async Task Emit_Inbox_StampsTheKey_AndQueuesTheOlderOneAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);

    var conn = await _receiveAsync(dbContext, _received(stream, ids[0], "model-a"), _received(stream, ids[1], "model-a"));

    await Assert.That(await _keyAsync(conn, ids[0])).IsEqualTo("model-a")
      .Because("the receiver's copy is keyed from the metadata the receive path stamped, so it is reaped on the receiver too");
    await Assert.That(await _keyAsync(conn, ids[1])).IsEqualTo("model-a");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([ids[0]]);
  }

  [Test]
  public async Task DirectAppend_StampsTheKey_AndQueuesTheOlderOneAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var first = _envelope(new SupersedingProbeEmbedded { DocumentId = stream, Model = "model-a" });
    var other = _envelope(new SupersedingProbeEmbedded { DocumentId = stream, Model = "model-b" });
    var second = _envelope(new SupersedingProbeEmbedded { DocumentId = stream, Model = "model-a" });

    await store.AppendAsync(stream, first);
    await store.AppendAsync(stream, other);
    await store.AppendAsync(stream, second);

    var conn = await _openAsync(context);
    await Assert.That(await _keyAsync(conn, first.MessageId.Value)).IsEqualTo("model-a")
      .Because("the EF Core store stamps the key in the metadata it appends, and wh_append_event copies it on a flagged row");
    await Assert.That(await _keyAsync(conn, other.MessageId.Value)).IsEqualTo("model-b");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([first.MessageId.Value])
      .Because("only the older event of the same key is superseded; a different key is not");
  }

  // ===== The reaper and its gates =====

  [Test]
  public async Task Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    var newer = await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);

    var reaped = await _maintainAsync(conn);

    await Assert.That(reaped).IsEqualTo(1L).Because("perform_maintenance reports Task 16 by its name");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([newer[0]]);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", older[0]))).IsEqualTo(0L);
    await Assert.That(await _queuedAsync(conn)).IsEmpty();
    await Assert.That(await _digestAsync(conn, stream, TYPE)).IsEqualTo(await _freshFoldAsync(conn, stream, TYPE))
      .Because("the digest loses exactly the reaped event's contribution, so it equals a fold of what is left");
    await Assert.That(await _digestCountAsync(conn, stream, TYPE)).IsEqualTo(1L);
    // #2106: the reaped event's id halves leave the id lanes too.
    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  [Test]
  public async Task Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _associateAsync(conn, TYPE, "EmbeddingView");
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L).Because("a perspective that consumes it has not processed it yet");

    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id", ("id", older[0]));
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("the control: once processed, the same event is reaped");
  }

  [Test]
  public async Task Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    var newer = await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, 'P', @e, 0, NOW())",
      ("s", stream), ("e", older[0]));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L).Because("a cursor rests on it, and the EF Core schema's foreign key is RESTRICT");

    await _executeAsync(conn, "UPDATE wh_perspective_cursors SET last_event_id = @e WHERE stream_id = @s", ("s", stream), ("e", newer[0]));
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("the control: the cursor moved on, so the event goes");
  }

  [Test]
  public async Task Reap_ASnapshotOnIt_KeepsIt_UntilTheSnapshotIsGoneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
      "VALUES (@s, 'P', @e, '{}'::jsonb, 1, 1)", ("s", stream), ("e", older[0]));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L)
      .Because("a rewind to that snapshot would move a cursor onto the event, so the event stays while the snapshot does");

    await _executeAsync(conn, "DELETE FROM wh_perspective_snapshots WHERE stream_id = @s", ("s", stream));
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
  }

  [Test]
  public async Task Reap_AnActiveDestructionHold_KeepsIt_ALapsedOneDoesNotAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);
    await _executeAsync(conn, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@id, NOW() + INTERVAL '1 day')", ("id", older[0]));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L).Because("a PreDestruction hook's Cancel or Defer is honored");

    await _executeAsync(conn, "UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE event_id = @id", ("id", older[0]));
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_destruction_hold WHERE event_id = @id", ("id", older[0])))
      .IsEqualTo(0L).Because("the lapsed hold goes with its event");
  }

  [Test]
  public async Task Reap_AnEventTheStamperHasNotReached_IsKeptAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L).Because("its commit sequence is not stamped yet");

    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
  }

  [Test]
  public async Task Reap_ADifferentTypeOrKeyInTheSameStream_IsUntouchedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var keyA = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "a"));
    var keyB = await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "b"));
    var otherType = await _emitAsync(conn, _t0.AddMinutes(2), (stream, OTHER_TYPE, SUPERSEDING, "a"));
    var ordinary = await _emitAsync(conn, _t0.AddMinutes(3), (stream, OTHER_TYPE, 0, null));
    var newerA = await _emitAsync(conn, _t0.AddMinutes(4), (stream, TYPE, SUPERSEDING, "a"));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([keyB[0], otherType[0], ordinary[0], newerA[0]])
      .Because("only an older event of the same stream, type and key is superseded: key a of the type goes, and nothing else");
    await Assert.That(await _storedAsync(conn, stream)).DoesNotContain(keyA[0]);
  }

  [Test]
  public async Task Reap_TheNewestOfItsKey_IsKept_EvenWhenQueuedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var only = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);
    await _executeAsync(conn, "INSERT INTO wh_superseded_events (event_id, stream_id, queued_at) VALUES (@id, @s, NOW())", ("id", only[0]), ("s", stream));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L)
      .Because("the reaper checks again, under the stream's lock, that a newer event of the key exists");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([only[0]]);
  }

  [Test]
  public async Task Reap_AnEphemeralRowCarryingTheFlag_IsLeftToTheEphemeralReaperAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING | EPHEMERAL, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING | EPHEMERAL, "k"));
    await _stampAsync(conn);

    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([older[0]]);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L)
      .Because("an ephemeral event is not part of the durable log; WHIZ135 refuses the combination at build time, and the reaper refuses it here");
  }

  [Test]
  public async Task Reap_DebugMode_KeepsEverythingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);
    await _setDebugModeAsync(conn, true);

    await Assert.That(await _reapStatusAsync(conn)).IsEqualTo("0|skipped (debug_mode=true)");
    await Assert.That(await _storedAsync(conn, stream)).Count().IsEqualTo(2);

    await _setDebugModeAsync(conn, false);
    await Assert.That(await _reapStatusAsync(conn)).IsEqualTo("1|ok");
  }

  [Test]
  public async Task Reap_AQueuedEventAlreadyGone_IsForgottenAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var older = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _executeAsync(conn, "DELETE FROM wh_event_body WHERE event_id = @id", ("id", older[0]));
    await _executeAsync(conn, "DELETE FROM wh_event_store WHERE event_id = @id", ("id", older[0]));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L);
    await Assert.That(await _queuedAsync(conn)).IsEmpty().Because("a truncate, an archive or a purge removed it; the queue lets it go");
  }

  [Test]
  public async Task Reap_RemovingFoldedHistory_RefoldsAndBumpsTheGenerationOnce_AnAtMostOnceEventDoesNotAsync() {
    await using var conn = await _openAsync();
    var folded = (Guid)TrackedGuid.New();
    var unfolded = (Guid)TrackedGuid.New();
    await _emitAsync(conn, _t0, (unfolded, TYPE, SUPERSEDING | AT_MOST_ONCE, "k"));
    await _emitAsync(conn, _t0.AddMinutes(1), (unfolded, TYPE, SUPERSEDING | AT_MOST_ONCE, "k"));
    await _stampAsync(conn);
    var before = await _generationAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("an at-most-once event was never folded, so removing it changes no folded history");

    await _emitAsync(conn, _t0.AddMinutes(2), (folded, TYPE, SUPERSEDING, "a"), (folded, TYPE, SUPERSEDING, "b"));
    await _emitAsync(conn, _t0.AddMinutes(3), (folded, TYPE, SUPERSEDING, "a"), (folded, TYPE, SUPERSEDING, "b"));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(2L);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(before + 1)
      .Because("removing folded history bumps the origin generation once per pass, however many events it removed");
  }

  [Test]
  public async Task Reap_TakesAtMostTheBatchSize_OldestQueuedFirstAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var first = await _emitAsync(conn, _t0, (stream, TYPE, SUPERSEDING, "k"));
    var second = await _emitAsync(conn, _t0.AddMinutes(1), (stream, TYPE, SUPERSEDING, "k"));
    await _emitAsync(conn, _t0.AddMinutes(2), (stream, TYPE, SUPERSEDING, "k"));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn, batchSize: 1)).IsEqualTo(1L);
    await Assert.That(await _storedAsync(conn, stream)).DoesNotContain(first[0]).Because("the oldest queued goes first");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([second[0]]);
    await Assert.That(await _reapAsync(conn, batchSize: 1)).IsEqualTo(1L);
    await Assert.That(await _queuedAsync(conn)).IsEmpty();
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];

  private static MessageEnvelope<TMessage> _envelope<TMessage>(TMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static Task<Guid[]> _emitAsync(NpgsqlConnection conn, DateTimeOffset now, params (Guid Stream, string Type, int Flags, string? Key)[] events) =>
    _emitAsync(conn, now, _orderedIds(events.Length), events);

  // Queues the events in the outbox, with the key in the metadata as the dispatcher writes it, and runs the outbox emit
  // chain over them as one batch.
  private static async Task<Guid[]> _emitAsync(
      NpgsqlConnection conn, DateTimeOffset now, Guid[] ids, params (Guid Stream, string Type, int Flags, string? Key)[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'superseding-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           CASE WHEN @key::text IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('sk', @key::text) END, NULL,
           1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("key", (object?)events[i].Key ?? DBNull.Value), ("inst", instanceId),
        ("lease", now.AddMinutes(5)), ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static InboxMessage _received(Guid streamId, Guid messageId, string key) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { model = key }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = TYPE,
      StreamId = streamId,
      IsEvent = true,
      Flags = EventFlags.Superseding,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [], SupersedingKey = key },
    };
  }

  // Stores the rows in the inbox, leases them to one instance and runs the claim's emit chain over them.
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = await _openAsync(dbContext);
    var instanceId = (Guid)TrackedGuid.New();
    Guid[] ids = [.. rows.Select(r => r.MessageId)];
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("inst", instanceId));
    return conn;
  }

  private static async Task _associateAsync(NpgsqlConnection conn, string eventType, string perspective) =>
    await _executeAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("t", eventType), ("p", perspective));

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static Task _setDebugModeAsync(NpgsqlConnection conn, bool on) => _executeAsync(conn, """
    INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
    VALUES ('debug_mode', @v, 'boolean', 'test')
    ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
    """, ("v", on ? "true" : "false"));

  private static Task<long> _reapAsync(NpgsqlConnection conn, int batchSize = 5000) =>
    _scalarAsync(conn, "SELECT rows_affected FROM wh_reap_superseded_events(@n)", ("n", batchSize));

  private static async Task<string> _reapStatusAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected || '|' || status FROM wh_reap_superseded_events()";
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  // Runs the whole maintenance pass and returns what Task 16 reported.
  private static async Task<long> _maintainAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT task_name, rows_affected FROM perform_maintenance()";
    long? reaped = null;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      if (reader.GetString(0) == "reap_superseded_events") {
        reaped = reader.GetInt64(1);
      }
    }
    return reaped ?? throw new InvalidOperationException("perform_maintenance reported no reap_superseded_events task");
  }

  private static async Task<string?> _keyAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT supersede_key FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (string?)value;
  }

  private static async Task<List<Guid>> _queuedAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_superseded_events ORDER BY queued_at, event_id";
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task<List<Guid>> _storedAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task<string> _digestAsync(NpgsqlConnection conn, Guid stream, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT digest_lo || '|' || digest_hi FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", type);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _digestCountAsync(NpgsqlConnection conn, Guid stream, string type) =>
    _scalarAsync(conn, "SELECT event_count FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t", ("s", stream), ("t", type));

  // What a fold over the stream's surviving events of the type computes, the way the emit chain folds.
  private static async Task<string> _freshFoldAsync(NpgsqlConnection conn, Guid stream, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT bit_xor(hashtextextended(event_id::text, 0)) || '|' || bit_xor(hashtextextended(event_id::text, 1)) " +
      "FROM wh_event_store WHERE stream_id = @s AND event_type = @t";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", type);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _generationAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'");

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
