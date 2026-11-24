// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every write that commits a result is fenced by the partition epoch it was made under (#2556, phase 4 of #2540). A
/// claim learns the epoch of each partition it claims in (<c>wh_held_epoch</c>); a completion, a failure, a cursor
/// advance, a publish mark and a handler commit name it, and the write affects nothing when the partition's ownership
/// has moved on since. A fenced write is reported (a <c>whizbang.fenced</c> notice naming the kind and how many), never
/// silent, and the work stays for the current owner. With the switch off, or with no epoch named, every write is
/// exactly as before.
/// </summary>
/// <remarks>
/// Instance <c>A</c> owns every partition (the map [A]) at epoch 1. "The ownership moves on" is the flip the handover
/// protocol ends with: the partition's owner becomes <c>B</c> and its epoch rises by one.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/570_WritesAreFencedByThePartitionEpoch.sql</code-under-test>
[Category("Shard5")]
public class OwnershipFencingSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Fencing.Perspective";
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int PARTITION = 7;
  private const long FIRST_EPOCH = 1;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  // --- the epoch a claim learns -----------------------------------------------------------------

  [Test]
  public async Task HeldEpoch_IsTheOwnersEpochInEitherState_AndMinusOneForAnyoneElseAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);

    var active = await _heldEpochAsync(conn, PARTITION, _a);
    await _execAsync(conn, "UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = @b, handover_deadline = NOW() + INTERVAL '30 seconds' WHERE partition_number = @p",
      ("b", _b), ("p", PARTITION));
    var handingOver = await _heldEpochAsync(conn, PARTITION, _a);
    var other = await _heldEpochAsync(conn, PARTITION, _b);

    await Assert.That(active).IsEqualTo(FIRST_EPOCH);
    await Assert.That(handingOver).IsEqualTo(FIRST_EPOCH)
      .Because("the old owner finishes its batch while handing over, and its writes carry the epoch it still holds");
    await Assert.That(other).IsEqualTo(-1L).Because("an instance that does not own the partition holds no epoch of it");
  }

  [Test]
  public async Task HeldEpoch_WithoutAPartition_IsZero_AndAPartitionPastTheTable_IsReadThroughItsRemainderAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);

    var none = await _scalarAsync<long>(conn, "SELECT wh_held_epoch(NULL, @a)", ("a", _a));
    var past = await _heldEpochAsync(conn, 10_000 + PARTITION, _a);

    await Assert.That(none).IsEqualTo(0L).Because("work with no partition is anyone's, at epoch 0, as wh_i_own answers");
    await Assert.That(past).IsEqualTo(FIRST_EPOCH);
  }

  [Test]
  public async Task HeldEpoch_WithTheSwitchOff_IsNullAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    var epoch = await _voteAsync(conn);
    await _publishAsync(conn, epoch, [_a]);

    var held = await _scalarAsync<object>(conn, "SELECT wh_held_epoch(@p, @a)", ("p", PARTITION), ("a", _a));

    await Assert.That(held).IsEqualTo(DBNull.Value).Because("off, nothing is fenced, so a claim carries no epoch");
  }

  [Test]
  public async Task EpochIsCurrent_OnlyForThePartitionsCurrentEpoch_AndAlwaysForAWriteThatNamesNoneAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    await _moveOnAsync(conn);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_epoch_is_current(@p, 2)", ("p", PARTITION))).IsTrue();
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_epoch_is_current(@p, 1)", ("p", PARTITION))).IsFalse();
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_epoch_is_current(@p, -1)", ("p", PARTITION))).IsFalse()
      .Because("a claim made while not the owner holds no tenure, so nothing it writes lands");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_epoch_is_current(@p, NULL)", ("p", PARTITION))).IsTrue()
      .Because("a write that names no epoch (an inline path, an older caller) is not fenced");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_epoch_is_current(NULL, 0)")).IsTrue();
  }

  // --- completions, failures and cursors through the flush ------------------------------------

  [Test]
  public async Task Flush_AnOutboxCompletionUnderAStaleEpoch_AffectsNoRow_AndIsReportedFencedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, outbox: [message], outboxEpochs: [FIRST_EPOCH]));

    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(1L)
      .Because("the old owner's completion lands nowhere; the row stays for the current owner");
    await Assert.That(fenced).IsEquivalentTo(["outbox_completion=1"]);
  }

  [Test]
  public async Task Flush_AnOutboxCompletionUnderTheCurrentEpoch_CompletesAsBeforeAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, outbox: [message], outboxEpochs: [FIRST_EPOCH]));

    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(0L);
    await Assert.That(fenced).IsEmpty();
  }

  [Test]
  public async Task Flush_APerspectiveEventCompletionUnderAStaleEpoch_AffectsNoRowAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var work = await _perspectiveEventAsync(conn);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, perspectiveEvents: [work], perspectiveEpochs: [FIRST_EPOCH]));

    await Assert.That(await _countAsync(conn, "wh_perspective_events", "event_work_id", work)).IsEqualTo(1L);
    await Assert.That(fenced).IsEquivalentTo(["perspective_event_completion=1"]);
  }

  [Test]
  public async Task Flush_ACursorAdvanceUnderAStaleEpoch_LeavesTheCursorAsItWasAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var lastEvent = await _storedEventAsync(conn, stream);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, cursors: _cursorJson(stream, lastEvent, FIRST_EPOCH)));

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", "stream_id", stream)).IsEqualTo(0L)
      .Because("a stale owner's cursor never moves the stream's position");
    await Assert.That(fenced).IsEquivalentTo(["perspective_cursor=1"]);
  }

  [Test]
  public async Task Flush_ACursorAdvanceUnderTheCurrentEpoch_AdvancesAsBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var lastEvent = await _storedEventAsync(conn, stream);

    await _flushAsync(conn, cursors: _cursorJson(stream, lastEvent, FIRST_EPOCH));

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", "stream_id", stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task Flush_AFailureUnderAStaleEpoch_IsNotRecordedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, failures: _outboxFailureJson(message, FIRST_EPOCH)));

    await Assert.That(await _scalarAsync<object>(conn, "SELECT error FROM wh_outbox WHERE message_id = @m", ("m", message)))
      .IsEqualTo(DBNull.Value).Because("the stale owner's failure is not the current owner's to record");
    await Assert.That(fenced).IsEquivalentTo(["outbox_failure=1"]);
  }

  // --- the direct paths ---------------------------------------------------------------------------

  [Test]
  public async Task PublishMark_UnderAStaleEpoch_AffectsNoRowAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);

    long affected = 0;
    var fenced = await _withNoticesAsync(conn, async () => affected = await _scalarAsync<int>(conn,
      "SELECT complete_outbox_published(ARRAY[@m]::uuid[], FALSE, ARRAY[1]::bigint[])", ("m", message)));

    await Assert.That(affected).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(1L);
    await Assert.That(fenced).IsEquivalentTo(["outbox_completion=1"]);
  }

  [Test]
  public async Task CompletePerspective_UnderAStaleEpoch_AffectsNoRowAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var work = await _perspectiveEventAsync(conn);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _execAsync(conn,
      "SELECT complete_perspective('[]'::jsonb, ARRAY[@w]::uuid[], FALSE, ARRAY[1]::bigint[])", ("w", work)));

    await Assert.That(await _countAsync(conn, "wh_perspective_events", "event_work_id", work)).IsEqualTo(1L);
    await Assert.That(fenced).IsEquivalentTo(["perspective_event_completion=1"]);
  }

  [Test]
  public async Task HandlerCommit_UnderAStaleEpoch_CommitsNothing_NeitherTheCompletionNorWhatTheHandlerEmittedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var inbox = await _inboxRowAsync(conn);
    var emitted = (Guid)TrackedGuid.New();
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _execAsync(conn, "SELECT commit_handler_result(@r::jsonb)",
      ("r", _handlerCommitJson(inbox, emitted, FIRST_EPOCH))));

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT processed_at IS NULL FROM wh_inbox_state WHERE message_id = @m", ("m", inbox)))
      .IsTrue();
    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", emitted)).IsEqualTo(0L)
      .Because("a stale handler's result is one write: what it emitted does not commit either");
    await Assert.That(fenced).IsEquivalentTo(["handler_commit=1"]);
  }

  [Test]
  public async Task HandlerBatch_AStaleItemIsReturnedFenced_AndTheRestCommitAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var stale = await _inboxRowAsync(conn);
    var current = await _inboxRowAsync(conn, partition: PARTITION + 1);
    var staleHandler = (Guid)TrackedGuid.New();
    var currentHandler = (Guid)TrackedGuid.New();
    await _moveOnAsync(conn);
    var batch = "[" + _handlerCommitJson(stale, (Guid)TrackedGuid.New(), FIRST_EPOCH, staleHandler) + ","
      + _handlerCommitJson(current, (Guid)TrackedGuid.New(), FIRST_EPOCH, currentHandler) + "]";

    var results = new Dictionary<Guid, (bool Success, int Tier)>();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT handler_id, success, tier FROM commit_handler_batch(@r::jsonb)";
      cmd.Parameters.AddWithValue("r", batch);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        results[reader.GetGuid(0)] = (reader.GetBoolean(1), reader.GetInt32(2));
      }
    }

    await Assert.That(results[staleHandler]).IsEqualTo((false, 0)).Because("tier 0 is the fenced outcome, distinct from a failure");
    await Assert.That(results[currentHandler].Success).IsTrue();
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT processed_at IS NULL FROM wh_inbox_state WHERE message_id = @m", ("m", stale)))
      .IsTrue();
  }

  // --- the switch off, and a write that names no epoch, are exactly as before ------------------

  [Test]
  public async Task SwitchOff_AnEpochIsIgnored_AndTheWriteLandsAsBeforeAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    var epoch = await _voteAsync(conn);
    await _publishAsync(conn, epoch, [_a]);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);

    var fenced = await _withNoticesAsync(conn, () => _flushAsync(conn, outbox: [message], outboxEpochs: [FIRST_EPOCH]));

    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(0L);
    await Assert.That(fenced).IsEmpty();
  }

  [Test]
  public async Task MapMode_AWriteThatNamesNoEpoch_LandsAsBeforeAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);

    await _execAsync(conn, "SELECT flush_completions(ARRAY[@m]::uuid[], '[]'::jsonb, ARRAY[]::uuid[], '[]'::jsonb)", ("m", message));

    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(0L);
  }

  // --- helpers --------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn);
    await _publishAsync(conn, epoch, [_a]);
    var answer = await _scalarAsync<string>(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch));
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
    return epoch;
  }

  /// <summary>The flip the handover ends with: the partition's owner becomes B and its epoch rises by one.</summary>
  private static Task _moveOnAsync(NpgsqlConnection conn) =>
    _execAsync(conn, """
      UPDATE wh_partition_ownership
      SET owner_instance_id = @b, epoch = epoch + 1, state = 'active', next_owner_instance_id = NULL, handover_deadline = NULL
      WHERE partition_number = @p
      """, ("b", _b), ("p", PARTITION));

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) =>
    await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
      ("inst", instance), ("metadata", SUPPORTS_MAP));

  private static async Task<long> _voteAsync(NpgsqlConnection conn) =>
    Convert.ToInt64(await _scalarAsync<object>(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), System.Globalization.CultureInfo.InvariantCulture);

  private static async Task _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<long> _heldEpochAsync(NpgsqlConnection conn, int partition, Guid instance) =>
    _scalarAsync<long>(conn, "SELECT wh_held_epoch(@p, @i)", ("p", partition), ("i", instance));

  private static async Task<Guid> _outboxRowAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number,
         instance_id, lease_expiry)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, clock_timestamp(), @stream, @p, @a, NOW() + INTERVAL '5 minutes')
      """, ("id", id), ("stream", (Guid)TrackedGuid.New()), ("p", PARTITION), ("a", _a));
    return id;
  }

  private static async Task<Guid> _perspectiveEventAsync(NpgsqlConnection conn) {
    var work = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry)
      VALUES (@w, @stream, @perspective, @event, @p, 0, 0, NOW(), @a, NOW() + INTERVAL '5 minutes')
      """, ("w", work), ("stream", (Guid)TrackedGuid.New()), ("perspective", PERSPECTIVE), ("event", (Guid)TrackedGuid.New()),
      ("p", PARTITION), ("a", _a));
    return work;
  }

  private static async Task<Guid> _inboxRowAsync(NpgsqlConnection conn, int partition = PARTITION) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 minute', @stream, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 0, @p, @a, NOW() + INTERVAL '5 minutes'
      FROM m
      """, ("id", id), ("stream", (Guid)TrackedGuid.New()), ("p", partition), ("a", _a));
    return id;
  }

  /// <summary>One event on the stream in the event store, so a cursor naming it can be written.</summary>
  private static async Task<Guid> _storedEventAsync(NpgsqlConnection conn, Guid stream) {
    var eventId = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@event, @stream, @stream, 'TestAggregate', 'TestEvent', 1, NOW() - INTERVAL '1 minute')
      """, ("event", eventId), ("stream", stream));
    return eventId;
  }

  private static string _cursorJson(Guid stream, Guid lastEvent, long epoch) => $$"""
    [{"StreamId":"{{stream}}","PerspectiveName":"{{PERSPECTIVE}}","LastEventId":"{{lastEvent}}","Status":2,
      "PartitionNumber":{{PARTITION}},"PartitionEpoch":{{epoch}}}]
    """;

  private static string _outboxFailureJson(Guid message, long epoch) => $$"""
    [{"Category":"outbox","Items":[{"MessageId":"{{message}}","CompletedStatus":8,"Error":"stale owner's failure",
      "Reason":2,"PartitionNumber":{{PARTITION}},"PartitionEpoch":{{epoch}}}]}]
    """;

  private static string _handlerCommitJson(Guid inbox, Guid emitted, long epoch, Guid? handler = null) => $$"""
    {"handler_id":"{{handler ?? (Guid)TrackedGuid.New()}}","instance_id":"{{_a}}","service_name":"test","host_name":"test-host",
     "process_id":1,"partition_count":10000,"debug_mode":false,"partition_epoch":{{epoch}},
     "inbox_completion":{"MessageId":"{{inbox}}","Status":4},
     "new_outbox_messages":[{"MessageId":"{{emitted}}","Destination":"out-topic","MessageType":"EmittedEvent",
       "EnvelopeType":null,"Envelope":{},"Metadata":{},"Scope":null,"StreamId":"{{(Guid)TrackedGuid.New()}}","IsEvent":false}],
     "new_inbox_messages":[]}
    """;

  private static async Task _flushAsync(NpgsqlConnection conn, Guid[]? outbox = null, long[]? outboxEpochs = null,
      Guid[]? perspectiveEvents = null, long[]? perspectiveEpochs = null, string cursors = "[]", string failures = "[]") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flush_completions(@o, @c::jsonb, @pe, @f::jsonb, @oe, @pee)";
    cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = outbox ?? [] });
    cmd.Parameters.AddWithValue("c", cursors);
    cmd.Parameters.Add(new NpgsqlParameter("pe", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = perspectiveEvents ?? [] });
    cmd.Parameters.AddWithValue("f", failures);
    cmd.Parameters.Add(new NpgsqlParameter("oe", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = (object?)outboxEpochs ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("pee", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = (object?)perspectiveEpochs ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Runs <paramref name="action"/> and returns the fenced notices it raised, as <c>kind=count</c>.</summary>
  private static async Task<List<string>> _withNoticesAsync(NpgsqlConnection conn, Func<Task> action) {
    var fenced = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) {
      if (e.Notice.MessageText == "whizbang.fenced") {
        fenced.Add(e.Notice.Detail ?? "");
      }
    }
    conn.Notice += OnNotice;
    try {
      await action();
    } finally {
      conn.Notice -= OnNotice;
    }
    return fenced;
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, string table, string column, Guid id) =>
    _scalarAsync<long>(conn, $"SELECT count(*) FROM {table} WHERE {column} = @id", ("id", id));

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
