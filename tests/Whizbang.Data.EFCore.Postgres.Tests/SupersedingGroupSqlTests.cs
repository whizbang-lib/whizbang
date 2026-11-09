// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>The first event of a document's state, in a superseding group with <see cref="SupersedingGroupProbeRevised"/>.</summary>
[Superseding(Group = "superseding-group-probe")]
public sealed record SupersedingGroupProbeCreated : IEvent {
  /// <summary>The document, which is the stream.</summary>
  [StreamId]
  public Guid DocumentId { get; init; }

  /// <summary>The key.</summary>
  [SupersedingKey]
  public string Model { get; init; } = "";
}

/// <summary>A later event of a document's state, carrying the whole state as the first one does.</summary>
[Superseding(Group = "superseding-group-probe")]
public sealed record SupersedingGroupProbeRevised : IEvent {
  /// <summary>The document, which is the stream.</summary>
  [StreamId]
  public Guid DocumentId { get; init; }

  /// <summary>The key.</summary>
  [SupersedingKey]
  public string Model { get; init; } = "";
}

/// <summary>
/// Superseding groups (#2124, migration 352): the types of one group supersede each other, per stream, group and key.
/// Every write path stamps the group beside the key, the queue and the reaper match on the group (the event type for an
/// ungrouped row, so ungrouped types behave exactly as before), and the history reconcile re-stamps the stored events of a
/// type that joins a group later.
/// </summary>
/// <docs>fundamentals/events/superseding-events#superseding-groups</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/352_SupersedingGroups.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/365_TheTypeKeyedSupersedingIndexIsDropped.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class SupersedingGroupSqlTests : EFCoreTestBase {
  private const string CREATED = "Probe.Contracts.DocumentCreated, Probe.Contracts";
  private const string REVISED = "Probe.Contracts.DocumentRevised, Probe.Contracts";
  private const string OTHER = "Probe.Contracts.DocumentTagged, Probe.Contracts";
  private const string GROUP = "document-state";
  private const int SUPERSEDING = 128;

  private static readonly DateTimeOffset _t0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly string _createdType = TypeNameFormatter.Format(typeof(SupersedingGroupProbeCreated));
  private static readonly string _revisedType = TypeNameFormatter.Format(typeof(SupersedingGroupProbeRevised));

  // ===== The write paths stamp the group, and the queue matches on it =====

  [Test]
  public async Task Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync() {
    await using var conn = await _openAsync();
    var createdFirst = (Guid)TrackedGuid.New();
    var revisedFirst = (Guid)TrackedGuid.New();

    var created = await _emitAsync(conn, _t0, (createdFirst, CREATED, "k", GROUP));
    await _emitAsync(conn, _t0.AddMinutes(1), (createdFirst, REVISED, "k", GROUP));
    var revised = await _emitAsync(conn, _t0.AddMinutes(2), (revisedFirst, REVISED, "k", GROUP));
    await _emitAsync(conn, _t0.AddMinutes(3), (revisedFirst, CREATED, "k", GROUP));

    await Assert.That(await _groupAsync(conn, created[0])).IsEqualTo(GROUP).Because("the emit chain stamps the group from 'sg'");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([created[0], revised[0]])
      .Because("the newest event of any type in the group supersedes the older events of every type in it");
  }

  [Test]
  public async Task Emit_UngroupedTypes_StillSupersedeOnlyTheirOwnTypeAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    var created = await _emitAsync(conn, _t0, (stream, CREATED, "k", null));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, REVISED, "k", null));
    await Assert.That(await _queuedAsync(conn)).IsEmpty()
      .Because("without a group a type is its own group: two types never supersede each other");
    await Assert.That(await _groupAsync(conn, created[0])).IsNull().Because("an ungrouped row stores no group; nothing is backfilled");

    await _emitAsync(conn, _t0.AddMinutes(2), (stream, CREATED, "k", null));
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([created[0]]).Because("the control: the same type still supersedes");
  }

  [Test]
  public async Task Emit_AGroupedAndAnUngroupedType_NeverSupersedeEachOtherAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    await _emitAsync(conn, _t0, (stream, CREATED, "k", GROUP));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, OTHER, "k", null));
    await _emitAsync(conn, _t0.AddMinutes(2), (stream, OTHER, "k", "another-group"));

    await Assert.That(await _queuedAsync(conn)).IsEmpty()
      .Because("a group matches only its own name, never an ungrouped type nor another group");
  }

  [Test]
  public async Task Emit_KeysStillPartitionAGroupAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    var a = await _emitAsync(conn, _t0, (stream, CREATED, "model-a", GROUP));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, REVISED, "model-b", GROUP));
    await Assert.That(await _queuedAsync(conn)).IsEmpty().Because("another key of the group is another state");

    await _emitAsync(conn, _t0.AddMinutes(2), (stream, REVISED, "model-a", GROUP));
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a[0]]);
  }

  [Test]
  public async Task Emit_AMissingKeyInAGroup_IsNeverSupersededAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    var first = await _emitAsync(conn, _t0, (stream, CREATED, null, GROUP));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, REVISED, null, GROUP));
    await _emitAsync(conn, _t0.AddMinutes(2), (stream, REVISED, "k", GROUP));

    await Assert.That(await _groupAsync(conn, first[0])).IsEqualTo(GROUP);
    await Assert.That(await _queuedAsync(conn)).IsEmpty()
      .Because("#2123: an event whose key is unknown is kept, in a group as anywhere");
  }

  [Test]
  public async Task Emit_Inbox_StampsTheGroup_AndQueuesAcrossTypesAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);

    var conn = await _receiveAsync(dbContext, _received(stream, ids[0], CREATED), _received(stream, ids[1], REVISED));

    await Assert.That(await _groupAsync(conn, ids[0])).IsEqualTo(GROUP);
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([ids[0]])
      .Because("the receiver's copies supersede each other by the group the producer stamped");
  }

  [Test]
  public async Task DirectAppend_StampsTheGroup_AndQueuesAcrossTypesAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var created = _envelope(new SupersedingGroupProbeCreated { DocumentId = stream, Model = "m" });
    var revised = _envelope(new SupersedingGroupProbeRevised { DocumentId = stream, Model = "m" });

    await store.AppendAsync(stream, created);
    await store.AppendAsync(stream, revised);

    var conn = await _openAsync(context);
    await Assert.That(await _groupAsync(conn, created.MessageId.Value)).IsEqualTo("superseding-group-probe")
      .Because("the EF Core store stamps the group in the metadata it appends, and wh_append_event copies it");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([created.MessageId.Value]);
  }

  // ===== The reaper =====

  [Test]
  public async Task Reap_AnEventSupersededByAnotherTypeOfItsGroup_IsDeleted_TheNewestIsKeptAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var created = await _emitAsync(conn, _t0, (stream, CREATED, "k", GROUP));
    var revised = await _emitAsync(conn, _t0.AddMinutes(1), (stream, REVISED, "k", GROUP));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([revised[0]]);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", created[0]))).IsEqualTo(0L);
  }

  [Test]
  public async Task Reap_AQueuedEventWhoseOnlyNewerEventIsOutsideItsGroup_IsKeptAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var created = await _emitAsync(conn, _t0, (stream, CREATED, "k", GROUP));
    await _emitAsync(conn, _t0.AddMinutes(1), (stream, CREATED, "k", "another-group"));
    await _emitAsync(conn, _t0.AddMinutes(2), (stream, OTHER, "k", null));
    await _stampAsync(conn);
    await _executeAsync(conn, "INSERT INTO wh_superseded_events (event_id, stream_id, queued_at) VALUES (@e, @s, NOW())",
      ("e", created[0]), ("s", stream));

    await Assert.That(await _reapAsync(conn)).IsEqualTo(0L)
      .Because("the reaper rechecks that a newer event of the same group and key exists, whatever put the event in the queue");

    await _emitAsync(conn, _t0.AddMinutes(3), (stream, REVISED, "k", GROUP));
    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("the control: a newer event of its group lets it go");
  }

  // ===== The history reconcile =====

  /// <summary>
  /// A type that joins a group later: its events and its group partners' events were stamped as their own groups, so
  /// none supersedes the other. The reconcile walks the types again under the group, re-stamps them, and the stream's old
  /// Created event is queued and reaped behind the newer Revised one.
  /// </summary>
  [Test]
  public async Task Reconcile_TypesThatJoinAGroupLater_AreReStamped_AndTheOlderEventOfTheGroupIsReapedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    // As the write paths stored them before the group: flagged, keyed, no group.
    var created = await _appendAsync(conn, stream, _createdType, SUPERSEDING, "m", new SupersedingGroupProbeCreated { DocumentId = stream, Model = "m" });
    var revised = await _appendAsync(conn, stream, _revisedType, SUPERSEDING, "m", new SupersedingGroupProbeRevised { DocumentId = stream, Model = "m" });
    // And walked to the end then, under no group.
    foreach (var type in new[] { _createdType, _revisedType }) {
      await _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @d)", ("t", type), ("d", type));
      await _executeAsync(conn,
        "UPDATE wh_superseding_history SET completed_at = NOW(), last_event_id = 'ffffffff-ffff-ffff-ffff-ffffffffffff' WHERE event_type = @t",
        ("t", type));
    }
    await Assert.That(await _queuedAsync(conn)).IsEmpty();

    var result = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsStamped).IsEqualTo(2L).Because("a stored group that is not the type's group is stamped again");
    await Assert.That(await _groupAsync(conn, created)).IsEqualTo("superseding-group-probe");
    await Assert.That(await _groupAsync(conn, revised)).IsEqualTo("superseding-group-probe");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([created]);
    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([revised]);

    var again = await _step().ReconcileAsync(_services(), CancellationToken.None);
    await Assert.That(again.EventsStamped).IsEqualTo(0L).Because("once its group is stamped, nothing of the type is read again");
  }

  [Test]
  public async Task Reconcile_HistoryStoredBeforeAGroupedTypeWasSuperseding_IsStampedWithItsGroupAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var created = await _appendAsync(conn, stream, _createdType, 0, null, new SupersedingGroupProbeCreated { DocumentId = stream, Model = "m" });
    var revised = await _appendAsync(conn, stream, _revisedType, 0, null, new SupersedingGroupProbeRevised { DocumentId = stream, Model = "m" });

    await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _flagsAsync(conn, created)).IsEqualTo((long)SUPERSEDING);
    await Assert.That(await _groupAsync(conn, revised)).IsEqualTo("superseding-group-probe");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([created]);
  }

  [Test]
  public async Task Ensure_AChangedGroup_RestartsTheWalk_TheSameGroupDoesNotAsync() {
    await using var conn = await _openAsync();
    const string TYPE = "Probe.Contracts.EnsureProbe, Probe.Contracts";
    await _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @t, @g)", ("t", TYPE), ("g", GROUP));
    await _executeAsync(conn,
      "UPDATE wh_superseding_history SET completed_at = NOW(), last_event_id = 'ffffffff-ffff-ffff-ffff-ffffffffffff', events_matched = 3 WHERE event_type = @t",
      ("t", TYPE));

    await _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @t, @g)", ("t", TYPE), ("g", GROUP));
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_superseding_history WHERE event_type = @t AND completed_at IS NOT NULL",
      ("t", TYPE))).IsEqualTo(1L).Because("the same group leaves a finished walk finished");

    await _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @t, @g)", ("t", TYPE), ("g", "renamed"));
    await Assert.That(await _scalarAsync(conn,
      "SELECT count(*) FROM wh_superseding_history WHERE event_type = @t AND completed_at IS NULL AND last_event_id IS NULL AND events_matched = 0 AND supersede_group = 'renamed'",
      ("t", TYPE))).IsEqualTo(1L).Because("a new group walks the type again from its first event");

    await _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @t)", ("t", TYPE));
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_superseding_history WHERE event_type = @t AND supersede_group IS NULL",
      ("t", TYPE))).IsEqualTo(1L).Because("leaving a group is a change of group too");
  }

  // ===== The index =====

  [Test]
  public async Task TheGroupProbe_IsServedByTheGroupIndexAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    for (var i = 0; i < 20; i++) {
      await _emitAsync(conn, _t0.AddMinutes(i), (stream, i % 2 == 0 ? CREATED : REVISED, "k" + (i % 5), GROUP));
    }
    await using var transaction = await conn.BeginTransactionAsync();
    await using var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conn, transaction);
    _ = await off.ExecuteNonQueryAsync();
    // One probe of _wh_queue_superseded_events and of the reaper, for one stored event: the events of its stream, group
    // and key on the other side of its version.
    await using var explain = new NpgsqlCommand("""
      EXPLAIN SELECT older.event_id
      FROM wh_event_store older
      WHERE older.stream_id = @s
        AND COALESCE(older.supersede_group, older.event_type) = @g
        AND older.supersede_key = 'k1'
        AND older.version < 20
      """, conn, transaction);
    explain.Parameters.AddWithValue("g", GROUP);
    explain.Parameters.AddWithValue("s", stream);
    var plan = new List<string>();
    await using (var reader = await explain.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        plan.Add(reader.GetString(0));
      }
    }
    var text = string.Join('\n', plan);

    await Assert.That(text).Contains("idx_event_store_superseding_group")
      .Because("matching on COALESCE(group, type) needs the expression index; the type-keyed index narrows by stream only");
    await Assert.That(text).Contains("Index Cond: ((stream_id = ").And.Contains("(COALESCE(supersede_group, (event_type)::text) = ")
      .Because("the group match is an index condition, not a filter over every keyed event of the stream");
  }

  /// <summary>
  /// 295's index on the event type is gone once the group index serves every probe (#2166): nothing reads it, and every
  /// keyed write into the store would still maintain it.
  /// </summary>
  [Test]
  public async Task TheTypeKeyedIndex_IsDroppedAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_event_store_superseding'"))
      .IsEqualTo(0L)
      .Because("every probe matches on COALESCE(group, type), which idx_event_store_superseding_group serves, ungrouped rows included");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_event_store_superseding_group'"))
      .IsEqualTo(1L)
      .Because("positive control: the catalog read sees the index that replaced it");
  }

  // ===== Helpers =====

  private static SupersedingHistoryReconcileStep _step() =>
    new(NullLogger.Instance, 100, 10, () => [typeof(SupersedingGroupProbeCreated), typeof(SupersedingGroupProbeRevised)]);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services() => new ServiceCollection().AddSingleton(_connection()).BuildServiceProvider();

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

  // Queues each event in the outbox, flagged superseding with its key and group in the metadata as the dispatcher writes
  // them, and runs the outbox emit chain over it.
  private static async Task<Guid[]> _emitAsync(
      NpgsqlConnection conn, DateTimeOffset now, params (Guid Stream, string Type, string? Key, string? Group)[] events) {
    var ids = _orderedIds(events.Length);
    var instanceId = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'superseding-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           jsonb_strip_nulls(jsonb_build_object('sk', @key::text, 'sg', @group::text)), NULL,
           1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("key", (object?)events[i].Key ?? DBNull.Value),
        ("group", (object?)events[i].Group ?? DBNull.Value), ("inst", instanceId), ("lease", now.AddMinutes(5)),
        ("sid", events[i].Stream), ("flags", SUPERSEDING), ("now", now));
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

  // An event stored through wh_append_event as a write path before #2124 stored it: the key in the metadata, no group.
  private static async Task<Guid> _appendAsync<TEvent>(NpgsqlConnection conn, Guid stream, string type, int flags, string? key, TEvent payload) {
    var id = (Guid)TrackedGuid.New();
    var metadata = key is null
      ? $$"""{"MessageId":"{{id}}","Hops":[]}"""
      : $$"""{"MessageId":"{{id}}","Hops":[],"sk":"{{key}}"}""";
    await _executeAsync(conn,
      "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, @p::jsonb, @m::jsonb, NULL, NOW(), @f)",
      ("id", id), ("s", stream), ("t", type), ("p", JsonSerializer.Serialize(payload, _json.GetTypeInfo(typeof(TEvent)))),
      ("m", metadata), ("f", flags));
    return id;
  }

  private static InboxMessage _received(Guid streamId, Guid messageId, string type) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { model = "k" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = type,
      StreamId = streamId,
      IsEvent = true,
      Flags = EventFlags.Superseding,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [], SupersedingKey = "k", SupersedingGroup = GROUP },
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

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static Task<long> _reapAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT rows_affected FROM wh_reap_superseded_events(5000)");

  private static Task<long> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, "SELECT COALESCE(flags, 0) FROM wh_event_store WHERE event_id = @id", ("id", eventId));

  private static async Task<string?> _groupAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT supersede_group FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (string?)value;
  }

  private static async Task<List<Guid>> _queuedAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_superseded_events ORDER BY event_id";
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
