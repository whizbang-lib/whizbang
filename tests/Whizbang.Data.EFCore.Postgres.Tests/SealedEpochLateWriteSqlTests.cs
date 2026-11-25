// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2572: a sealed digest epoch stays equal to a fresh fold of the events it covers, whichever write path adds an event
/// to it or moves one in or out of it after it was sealed (migration 592), and epochs that drifted before the fix are
/// healed once, in bounded batches, by the maintenance closure pass (migration 593).
/// </summary>
/// <remarks>
/// Every test seals epochs with a width of 10 from settled events, then writes into a sealed epoch and compares the
/// stored fold with <c>_wh_epoch_buckets</c>, the canonical fold, read from the same store. A passing compare is never
/// vacuous: each test also asserts the stored count includes the event it wrote.
/// </remarks>
/// <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
[Category("Integration")]
[Category("Shard5")]
public class SealedEpochLateWriteSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.SealedEpochLateWriteEvent, Whizbang.Tests";
  private const string LANE = "l";
  private const string EPOCH = "e";
  private const long WIDTH = 10;
  private static readonly Guid _zero = Guid.Empty;

  // ── every write path that adds or moves an event ─────────────────────

  [Test]
  public async Task ABackfilledEvent_StoredByTheInboxChainInsideASealedEpoch_RefoldsItAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    var instance = TrackedGuid.New().Value;
    await _registerInstanceAsync(conn, instance);
    var late = TrackedGuid.New().Value;

    await _storeThroughTheInboxChainAsync(conn, instance, late, origin, originSequence: 3);

    await Assert.That(await _storedOriginSequenceAsync(conn, late)).IsEqualTo(3L)
      .Because("the inbox chain stored the backfilled event at its origin's position, inside sealed epoch 0");
    await Assert.That(await _storedCountAsync(conn, origin, 0)).IsEqualTo(3L)
      .Because("the sealed epoch counts the event that arrived after it was sealed");
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue()
      .Because("a sealed epoch must equal a fresh fold of the events it covers, or every audit against it mismatches");
  }

  [Test]
  public async Task ADirectInsert_IntoASealedEpochOfAReceivedLane_RefoldsItAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);

    await _insertAsync(conn, origin, originSequence: 14, commitSequence: null, settled: false);

    await Assert.That(await _storedCountAsync(conn, origin, 1)).IsEqualTo(2L);
    await Assert.That(await _foldIsFreshAsync(conn, origin, 1)).IsTrue()
      .Because("the refold rides the store's own write, so a path added later cannot forget it");
  }

  [Test]
  public async Task AReattribution_MovingAPositionedEventBetweenLanes_RefoldsTheSealedEpochsOfBothAsync() {
    await using var conn = await _openAsync();
    var from = TrackedGuid.New().Value;
    var to = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, from);
    await _sealReceivedLaneAsync(conn, to);
    var moved = await _firstEventAsync(conn, from, 0);

    await _execAsync(conn, $"UPDATE wh_event_store SET origin_service_id = '{to}' WHERE event_id = '{moved}'");

    await Assert.That(await _storedCountAsync(conn, from, 0)).IsEqualTo(1L).Because("the event left the old lane's epoch");
    await Assert.That(await _storedCountAsync(conn, to, 0)).IsEqualTo(3L).Because("and joined the new lane's epoch");
    await Assert.That(await _foldIsFreshAsync(conn, from, 0)).IsTrue();
    await Assert.That(await _foldIsFreshAsync(conn, to, 0)).IsTrue();
  }

  [Test]
  public async Task APositionWrittenByAnUpdate_IntoASealedEpoch_RefoldsItAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    var held = await _insertAsync(conn, origin, originSequence: null, commitSequence: null, settled: true);

    await _execAsync(conn, $"UPDATE wh_event_store SET origin_commit_sequence = 7 WHERE event_id = '{held}'");

    await Assert.That(await _storedCountAsync(conn, origin, 0)).IsEqualTo(3L);
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue();
  }

  [Test]
  public async Task AnEventMovedIntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync() {
    await using var conn = await _openAsync();
    var relay = TrackedGuid.New().Value;
    await _sealLocalLaneAsync(conn);
    var moved = await _insertAsync(conn, relay, originSequence: 40, commitSequence: 5, settled: true);
    var generation = await _generationAsync(conn);

    await _execAsync(conn, $"UPDATE wh_event_store SET origin_service_id = NULL, origin_commit_sequence = NULL WHERE event_id = '{moved}'");

    await Assert.That(await _storedCountAsync(conn, _zero, 0)).IsEqualTo(3L)
      .Because("the event joined the local lane at its own commit sequence, inside sealed epoch 0");
    await Assert.That(await _foldIsFreshAsync(conn, _zero, 0)).IsTrue();
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation + 1)
      .Because("a rewrite of this service's own sealed history moves its generation, so its consumers lower their seals");
  }

  [Test]
  public async Task AChangeThatLeavesTheFoldAsItWas_RewritesNothing_AndBumpsNothingAsync() {
    await using var conn = await _openAsync();
    await _sealLocalLaneAsync(conn);
    var sealedAt = await _closedAtAsync(conn, _zero, 0);
    var generation = await _generationAsync(conn);

    await using (var tx = await conn.BeginTransactionAsync()) {
      var transient = await _insertAsync(conn, null, originSequence: null, commitSequence: 6, settled: true);
      await _execAsync(conn, $"DELETE FROM wh_event_store WHERE event_id = '{transient}'");
      await tx.CommitAsync();
    }

    await Assert.That(await _closedAtAsync(conn, _zero, 0)).IsEqualTo(sealedAt)
      .Because("the epoch the transaction touched folds to what it already held, so its rows are not rewritten");
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation)
      .Because("nothing a consumer verified changed, so the generation stays");
  }

  [Test]
  public async Task AnIneligibleEvent_IntoASealedEpoch_IsNotRecordedAsATouchAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await using var tx = await conn.BeginTransactionAsync();

    await _insertAsync(conn, origin, originSequence: 4, commitSequence: null, settled: true, flags: 8);

    await Assert.That(await _touchesAsync(conn)).IsEqualTo(0L)
      .Because("an ephemeral event is in no fold, so storing it changes no seal");
    await tx.RollbackAsync();
  }

  [Test]
  public async Task AnExplicitRefoldAfterTheChange_LeavesNothingForTheCommitAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await using var tx = await conn.BeginTransactionAsync();

    await _insertAsync(conn, origin, originSequence: 4, commitSequence: null, settled: true);
    var touched = await _touchesAsync(conn);
    await _execAsync(conn, $"SELECT refold_digest_epochs('{origin}', 0, 0)");

    await Assert.That(touched).IsEqualTo(1L).Because("the insert recorded the sealed epoch it changed, for the commit");
    await Assert.That(await _touchesAsync(conn)).IsEqualTo(0L)
      .Because("a path that refolds the epoch itself leaves the commit nothing to fold again");
    await Assert.That(await _scalarAsync(conn, "SELECT _wh_refold_touched_epochs()")).IsEqualTo(0L);
    await tx.CommitAsync();
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue();
  }

  [Test]
  public async Task ATouchedEpochTheFrontierFellBelow_IsLeftForClosureAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    var sealedAt = await _closedAtAsync(conn, origin, 0);

    await using (var tx = await conn.BeginTransactionAsync()) {
      await _insertAsync(conn, origin, originSequence: 4, commitSequence: null, settled: true);
      await _execAsync(conn, $"UPDATE wh_digest_epoch_frontiers SET closed_through_epoch = -1 WHERE origin_service_id = '{origin}'");
      await tx.CommitAsync();
    }

    await Assert.That(await _closedAtAsync(conn, origin, 0)).IsEqualTo(sealedAt)
      .Because("an epoch no longer sealed is folded by closure when it is closed again, never by the commit");
  }

  [Test]
  public async Task TwoTransactions_StoringIntoOneSealedEpoch_LeaveItsFoldCountingBothAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await using var first = await _openAsync();
    await using var firstTx = await first.BeginTransactionAsync();
    await _insertAsync(first, origin, originSequence: 3, commitSequence: null, settled: false);

    // The second transaction stores into the same epoch and commits while the first is still open, so its refold
    // cannot see the first one's event.
    await using (var second = await _openAsync()) {
      await _insertAsync(second, origin, originSequence: 4, commitSequence: null, settled: false);
    }
    await firstTx.CommitAsync();

    await Assert.That(await _storedCountAsync(conn, origin, 0)).IsEqualTo(4L)
      .Because("the transaction that commits last refolds after the first committed, so the fold counts both");
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue();
  }

  [Test]
  public async Task ARefoldAtCommit_HoldsTheEpochsLockUntilItsTransactionEndsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await using var holder = await _openAsync();
    await using var tx = await holder.BeginTransactionAsync();

    var refolded = await _scalarAsync(holder, $"SELECT _wh_refold_epoch_if_drifted('{origin}', 0, {WIDTH}, NOW())::int");
    var whileHeld = await _scalarAsync(conn, $"SELECT pg_try_advisory_xact_lock(_wh_digest_epoch_lock_key('{origin}', 0))::int");
    await tx.CommitAsync();
    var afterCommit = await _scalarAsync(conn, $"SELECT pg_try_advisory_xact_lock(_wh_digest_epoch_lock_key('{origin}', 0))::int");

    await Assert.That(refolded).IsEqualTo(0L).Because("the epoch had not drifted, so nothing was rewritten");
    await Assert.That(whileHeld).IsEqualTo(0L)
      .Because("two refolds of one epoch are serialized, so the later one folds after the earlier one committed");
    await Assert.That(afterCommit).IsEqualTo(1L);
  }

  // ── healing what drifted before the fix ──────────────────────────────

  [Test]
  public async Task TheUpgradeHeal_QueuesEverySealedReceivedEpochOnce_AndNoLocalOneAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await _sealLocalLaneAsync(conn);
    await _execAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'integrity_epoch_heal_queued'");

    var queued = await _scalarAsync(conn, "SELECT _wh_queue_received_epoch_heal()");
    var again = await _scalarAsync(conn, "SELECT _wh_queue_received_epoch_heal()");

    await Assert.That(queued).IsEqualTo(2L).Because("epochs 0 and 1 of the received lane are sealed, empty or not");
    await Assert.That(again).IsEqualTo(0L).Because("the heal is queued once, however often the migration runs again");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_digest_epoch_heal_queue WHERE origin_service_id = '{_zero}'"))
      .IsEqualTo(0L).Because("the local lane's rewrite paths refold and bump on their own; the fault this heals is in received lanes");
  }

  [Test]
  public async Task TheHeal_RefoldsADriftedEpoch_LeavesAnUndriftedOne_AndDrainsTheQueueAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    var untouchedAt = await _closedAtAsync(conn, origin, 1);
    await _execAsync(conn, $"UPDATE wh_digest_epochs SET digest_lo = 666, event_count = 1 WHERE origin_service_id = '{origin}' AND epoch_id = 0");
    await _queueHealAsync(conn);

    var result = await _healAsync(conn, 10);

    await Assert.That(result).IsEqualTo((2L, 1L));
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue().Because("the drifted seal is refolded from the store");
    await Assert.That(await _closedAtAsync(conn, origin, 1)).IsEqualTo(untouchedAt).Because("a seal that agrees is left as sealed");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_digest_epoch_heal_queue")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_epoch_heal_refolded'"))
      .IsEqualTo(1L).Because("the refolds are counted where an operator can read them");
  }

  [Test]
  public async Task TheHeal_TakesTheLowestEpochsFirst_WithinItsBudgetAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await _execAsync(conn, $"UPDATE wh_digest_epochs SET digest_lo = 666 WHERE origin_service_id = '{origin}'");
    await _queueHealAsync(conn);

    var first = await _healAsync(conn, 1);

    await Assert.That(first).IsEqualTo((1L, 1L));
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue()
      .Because("the lowest epoch heals first: a seal advances only as far as its first drifted epoch");
    await Assert.That(await _foldIsFreshAsync(conn, origin, 1)).IsFalse().Because("the budget bounds one call");
    await Assert.That(await _healAsync(conn, 1)).IsEqualTo((1L, 1L));
    await Assert.That(await _healAsync(conn, 1)).IsEqualTo((0L, 0L)).Because("the queue is drained");
  }

  [Test]
  public async Task TheHeal_DropsAQueuedEpochItsLaneNoLongerSealsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await _queueHealAsync(conn);
    await _execAsync(conn, $"UPDATE wh_digest_epoch_frontiers SET closed_through_epoch = 0 WHERE origin_service_id = '{origin}'");

    await Assert.That(await _healAsync(conn, 10)).IsEqualTo((1L, 0L)).Because("only epoch 0 is still sealed");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_digest_epoch_heal_queue")).IsEqualTo(0L);
  }

  [Test]
  public async Task Closing_HealsTheQueue_WithTheConfiguredBudget_BesideTheEpochsItClosesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealReceivedLaneAsync(conn, origin);
    await _execAsync(conn, $"UPDATE wh_digest_epochs SET digest_lo = 666 WHERE origin_service_id = '{origin}'");
    await _queueHealAsync(conn);
    await _setAsync(conn, "integrity_epoch_heal_per_cycle", "1");

    var closed = await _scalarAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");

    await Assert.That(closed).IsEqualTo(0L).Because("the closure count reports closures only; nothing new was settled");
    await Assert.That(await _foldIsFreshAsync(conn, origin, 0)).IsTrue();
    await Assert.That(await _foldIsFreshAsync(conn, origin, 1)).IsFalse().Because("one epoch per cycle, as configured");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_digest_epoch_heal_queue")).IsEqualTo(1L);
  }

  // ── helpers ──────────────────────────────────────────────────────────

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Epoch width 10; settled events of the lane at 1 and 2 (epoch 0), 15 (epoch 1) and 25 (open epoch 2); then
  /// closes epochs 0 and 1.</summary>
  private static async Task _sealReceivedLaneAsync(NpgsqlConnection conn, Guid origin) {
    await _setAsync(conn, "integrity_epoch_width", WIDTH.ToString(CultureInfo.InvariantCulture));
    foreach (var seq in new long[] { 1, 2, 15, 25 }) {
      await _insertAsync(conn, origin, seq, commitSequence: null, settled: true);
    }
    await _execAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");
  }

  /// <summary>The local lane, as <see cref="_sealReceivedLaneAsync"/> seals a received one.</summary>
  private static async Task _sealLocalLaneAsync(NpgsqlConnection conn) {
    await _setAsync(conn, "integrity_epoch_width", WIDTH.ToString(CultureInfo.InvariantCulture));
    foreach (var seq in new long[] { 1, 2, 15, 25 }) {
      await _insertAsync(conn, null, originSequence: null, commitSequence: seq, settled: true);
    }
    await _execAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");
  }

  private static async Task<Guid> _insertAsync(NpgsqlConnection conn, Guid? origin, long? originSequence, long? commitSequence,
      bool settled, int flags = 0) {
    var id = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, flags,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@id, @id, @id, 'TestAggregate', 1, @t, NULL, @f, @cs, CASE WHEN @settled THEN NOW() - INTERVAL '2 hours' ELSE NOW() END,
              @o, @seq)
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("t", TYPE);
    cmd.Parameters.AddWithValue("f", flags);
    cmd.Parameters.AddWithValue(nameof(settled), settled);
    cmd.Parameters.Add(new NpgsqlParameter("cs", NpgsqlDbType.Bigint) { Value = (object?)commitSequence ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = (object?)originSequence ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  /// <summary>Stores one received event the way the receive path does: an inbox row leased to the instance, then the
  /// inbox emit chain.</summary>
  private static async Task _storeThroughTheInboxChainAsync(NpgsqlConnection conn, Guid instance, Guid messageId, Guid origin,
      long originSequence) {
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, scope,
             received_at, stream_id, is_event, source_service_id, source_commit_sequence)
          VALUES (@id, 'LateWriteHandler', @t, '{"p":{}}'::jsonb, '{}'::jsonb, '{}'::jsonb, NOW(), @id, true, @o, @seq)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number,
           instance_id, lease_expiry)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, @inst, NOW() + INTERVAL '5 minutes' FROM m
        """;
      cmd.Parameters.AddWithValue("id", messageId);
      cmd.Parameters.AddWithValue("t", TYPE);
      cmd.Parameters.AddWithValue("o", origin);
      cmd.Parameters.AddWithValue("seq", originSequence);
      cmd.Parameters.AddWithValue("inst", instance);
      await cmd.ExecuteNonQueryAsync();
    }
    await using var chain = conn.CreateCommand();
    chain.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    chain.Parameters.AddWithValue("inst", instance);
    await chain.ExecuteScalarAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("id", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Whether the stored rows of one sealed epoch equal the canonical fold of the store, bucket for bucket.</summary>
  private static async Task<bool> _foldIsFreshAsync(NpgsqlConnection conn, Guid lane, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT (SELECT array_agg((de.scope_tenant, de.event_type, de.digest_lo, de.digest_hi, de.event_count)::text
                               ORDER BY de.scope_tenant, de.event_type)
              FROM wh_digest_epochs de WHERE de.origin_service_id = @l AND de.epoch_id = @e)
        IS NOT DISTINCT FROM
             (SELECT array_agg((b.b_tenant, b.b_type, b.b_lo, b.b_hi, b.b_cnt)::text ORDER BY b.b_tenant, b.b_type)
              FROM _wh_epoch_buckets(@l, @e, (SELECT f.epoch_width FROM wh_digest_epoch_frontiers f WHERE f.origin_service_id = @l)) b)
      """;
    cmd.Parameters.AddWithValue(LANE, lane);
    cmd.Parameters.AddWithValue(EPOCH, epoch);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _storedCountAsync(NpgsqlConnection conn, Guid lane, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(event_count), 0) FROM wh_digest_epochs WHERE origin_service_id = @l AND epoch_id = @e";
    cmd.Parameters.AddWithValue(LANE, lane);
    cmd.Parameters.AddWithValue(EPOCH, epoch);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<DateTime?> _closedAtAsync(NpgsqlConnection conn, Guid lane, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT MAX(closed_at) FROM wh_digest_epochs WHERE origin_service_id = @l AND epoch_id = @e";
    cmd.Parameters.AddWithValue(LANE, lane);
    cmd.Parameters.AddWithValue(EPOCH, epoch);
    var value = await cmd.ExecuteScalarAsync();
    return value is DateTime at ? at : null;
  }

  private static async Task<Guid> _firstEventAsync(NpgsqlConnection conn, Guid origin, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT event_id FROM wh_event_store
      WHERE origin_service_id = @l AND origin_commit_sequence >= @e * 10 AND origin_commit_sequence < (@e + 1) * 10
      ORDER BY origin_commit_sequence LIMIT 1
      """;
    cmd.Parameters.AddWithValue(LANE, origin);
    cmd.Parameters.AddWithValue(EPOCH, epoch);
    return (Guid)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _storedOriginSequenceAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, $"SELECT origin_commit_sequence FROM wh_event_store WHERE event_id = '{eventId}'");

  private static Task<long> _generationAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT COALESCE((SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), 0)");

  private static Task<long> _touchesAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_sealed_epoch_touches WHERE txid = txid_current()");

  private static async Task _queueHealAsync(NpgsqlConnection conn) {
    await _execAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'integrity_epoch_heal_queued'");
    await _execAsync(conn, "SELECT _wh_queue_received_epoch_heal()");
  }

  private static async Task<(long Checked, long Refolded)> _healAsync(NpgsqlConnection conn, int max) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epochs_checked, epochs_refolded FROM wh_heal_queued_digest_epochs(NOW(), @max)";
    cmd.Parameters.AddWithValue(nameof(max), max);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1));
  }

  private static async Task _setAsync(NpgsqlConnection conn, string key, string value) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@k, @v, 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """;
    cmd.Parameters.AddWithValue("k", key);
    cmd.Parameters.AddWithValue("v", value);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    do {
      while (await reader.ReadAsync()) {
        // Drained: run for its effect, every row read so a failure surfaces.
      }
    } while (await reader.NextResultAsync());
  }
}
