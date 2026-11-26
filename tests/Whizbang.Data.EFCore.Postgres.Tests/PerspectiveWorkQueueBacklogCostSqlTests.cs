// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Performance;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the perspective work-queue calls read as the queue behind them grows (#2286): each call is priced by the batch
/// it is given, never by the backlog or by the history the drain has already applied.
/// </summary>
/// <remarks>
/// <para>
/// A fan-out to many new streams leaves one pending perspective row per stream, leased to the instance that stored it
/// for the lease's length; a fan-out that outlasts the lease leaves them orphaned, so the claim acquires them again.
/// The drain then works through the backlog a batch at a time: the claim's acquisition and its ledger write,
/// <c>get_stream_events</c> for the claimed streams, <c>complete_perspective</c> for what was applied, and
/// <c>cleanup_completed_streams</c> for the streams left with nothing to do. Every stream applied leaves a cursor and
/// an applied-ledger row behind, so the history grows with the backlog drained.
/// </para>
/// <para>
/// Each call is measured in rows read (<see cref="RowsRead"/>: index entries and sequentially scanned rows, counted by
/// the backend inside the call's own transaction, on a database this test alone uses), once over a backlog and a
/// history of the same size, and once over four times both, with a batch of the size the worker hands each call
/// (1,000 streams).
/// </para>
/// <para>
/// Rows read are work counters, not blocks, and the test holds the one input on a shared server that moves them
/// (#2587): the horizon. Whether a vacuum may remove the versions the preparing calls leave dead (the fetch's lease, the
/// completion's retire, the shallower round's writes), whether a scan may skip one, and whether the fill's pages are
/// marked all-visible are decided against it, and any open snapshot holds it back, so a measurement that vacuumed them
/// away in isolation read every dead entry again under load. Each measurement holds a snapshot from before its first
/// fill (<see cref="HeldHorizon"/>), so every dead version stays on every run and no vacuum is asked to remove one; the
/// fills derive their ids from their names, and autovacuum is off on the measured tables, so no analyze rewrites the
/// statistics between the fill and the call. None of these calls draws a walk start, so there is nothing to seed.
/// </para>
/// <para>
/// Every call is measured with the plan made for its arguments and with the generic plan a long-lived connection may
/// settle on: a batch that is a few percent of the queue made a whole-table scan look as cheap as a probe per key to
/// the first, and the two are not always the same plan.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard5")]
[Category("Integration")]
[Category("Performance")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
public class PerspectiveWorkQueueBacklogCostSqlTests : EFCoreTestBase {
  private const int BATCH = 1_000;
  private const int BACKLOG = 10_000;
  private const int DEEPER = 4 * BACKLOG;
  private const string PERSPECTIVE = "Perf.OrderPerspective";
  private static readonly Guid _instance = new("00000000-0000-0000-0000-0000000000b1");

  /// <summary>
  /// The tables a fill writes and the measured calls read or write, the completion's doorbell row among them: left in
  /// place, the second round finds the row the first wrote and reads its entry once more.
  /// </summary>
  private static readonly string[] _measuredTables = [
    "wh_event_store", "wh_event_body", "wh_perspective_events", "wh_active_streams", "wh_perspective_cursors",
    "wh_perspective_applied", "wh_outbox", "wh_notify_state",
  ];

  /// <summary>
  /// Rows an acquisition may read per stream it leases: the window it chooses streams from is eight rows per stream
  /// (157), and each window row costs its urgency entry, its stream's ledger row and its ordering probe.
  /// </summary>
  private const int ACQUIRE_ROWS_PER_STREAM = 24;

  /// <summary>Rows the claim's ledger write may read per stream: the stream's ledger row, found and then written.</summary>
  private const int LEASE_ROWS_PER_STREAM = 3;

  /// <summary>
  /// Rows a fetch may read per stream of its batch: the work row (to lease it, to read it back and to return it), its
  /// event (twice), the stream's ledger row, its body: eight, measured, with room for two more.
  /// </summary>
  private const int FETCH_ROWS_PER_STREAM = 10;

  /// <summary>
  /// Rows a completion may read per stream of its batch: the work row, its event (cursor and applied ledger), the cursor,
  /// the applied ledger, the stream's ledger row (for the owner's doorbell and the unclaimed search), the pending probe.
  /// </summary>
  private const int COMPLETE_ROWS_PER_STREAM = 16;

  /// <summary>Rows an eviction may read per stream of its batch: the ledger row and one probe per work table.</summary>
  private const int CLEANUP_ROWS_PER_STREAM = 3;

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task AcquireOrphanedPerspectiveWork_ReadsWhatItLeases_NotTheBacklogAsync(string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, ACQUIRE_ROWS_PER_STREAM, async (conn, _) => {
      var leased = 0L;
      var read = await RowsRead.MeasureAsync(conn, async () => {
        await using var acquire = conn.CreateCommand();
        acquire.CommandText = """
          SELECT count(*) FROM claim_orphaned_perspective_events(
            @instance, NOW() + INTERVAL '5 minutes', NOW(), @batch, 0, 1, @rows, false)
          """;
        acquire.Parameters.AddWithValue("instance", _instance);
        acquire.Parameters.AddWithValue("batch", BATCH);
        acquire.Parameters.AddWithValue("rows", 8 * BATCH);
        leased = (long)(await acquire.ExecuteScalarAsync(cancellationToken))!;
      }, commit: true, cancellationToken);
      await Assert.That(leased).IsEqualTo(BATCH).Because("the acquisition leases a full batch of orphaned rows, or it measured nothing");
      return read;
    }, cancellationToken);
  }

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task LeaseClaimedStreams_ReadsWhatItsBatchHolds_NotTheLedgerAsync(string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, LEASE_ROWS_PER_STREAM, async (conn, batch) => {
      var streams = 0;
      var read = await RowsRead.MeasureAsync(conn, async () => {
        await using var lease = conn.CreateCommand();
        lease.CommandText = """
          SELECT wh_lease_claimed_streams(@instance, NOW(), NOW() + INTERVAL '5 minutes', @streams,
                                          ARRAY(SELECT 0 FROM unnest(@streams::uuid[])))
          """;
        lease.Parameters.AddWithValue("instance", _instance);
        lease.Parameters.AddWithValue("streams", batch);
        streams = Convert.ToInt32(await lease.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
      }, commit: true, cancellationToken);
      await Assert.That(streams).IsEqualTo(BATCH).Because("the ledger write covers every stream of the batch, or it measured nothing");
      return read;
    }, cancellationToken);
  }

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task GetStreamEvents_ReadsWhatItsBatchHolds_NotTheBacklogAsync(string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, FETCH_ROWS_PER_STREAM, async (conn, batch) => {
      var rows = 0L;
      var read = await RowsRead.MeasureAsync(conn, async () => rows = await _fetchAsync(conn, batch, cancellationToken),
        commit: true, cancellationToken);
      await Assert.That(rows).IsEqualTo(BATCH).Because("the fetch returns every row of its batch, or it measured nothing");
      return read;
    }, cancellationToken);
  }

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task CompletePerspective_ReadsWhatItsBatchHolds_NotTheBacklogAsync(string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, COMPLETE_ROWS_PER_STREAM,
      (conn, batch) => _measureCompleteAsync(conn, batch, cancellationToken), cancellationToken);
  }

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task CleanupCompletedStreams_ReadsWhatItsBatchHolds_NotTheBacklogAsync(string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, CLEANUP_ROWS_PER_STREAM,
      (conn, batch) => _measureCleanupAsync(conn, batch, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// The measure is the call's alone (#2587): the same round on the same fill, run again while another session of this
  /// database holds a snapshot from before the fill, reads the same rows from the same relations. That snapshot is what
  /// a loaded server does to a measurement: it holds back the horizon, which decides whether the vacuum after the
  /// preparing calls may remove the versions they left dead and whether the fill's pages are marked all-visible.
  /// </summary>
  [Test]
  [Arguments("complete")]
  [Arguments("cleanup")]
  [Timeout(600000)]
  public async Task AMeasure_UnderAHorizonHeldElsewhere_ReadsTheSameRowsAsync(string call, CancellationToken cancellationToken) {
    Func<NpgsqlConnection, Guid[], Task<RowsReadResult>> measure = call == "cleanup"
      ? (conn, batch) => _measureCleanupAsync(conn, batch, cancellationToken)
      : (conn, batch) => _measureCompleteAsync(conn, batch, cancellationToken);
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await _prepareAsync(setup, cancellationToken);
    }

    RowsReadResult plain;
    await using (var horizon = new NpgsqlConnection(ConnectionString)) {
      await HeldHorizon.HoldAsync(horizon, cancellationToken);
      plain = await _measureAtDepthAsync("force_custom_plan", "control", BACKLOG, measure, cancellationToken);
    }
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await using var truncate = setup.CreateCommand();
      truncate.CommandText = $"TRUNCATE {string.Join(", ", _measuredTables)} CASCADE";
      _ = await truncate.ExecuteNonQueryAsync(cancellationToken);
    }
    RowsReadResult disturbed;
    await using (var horizon = new NpgsqlConnection(ConnectionString)) {
      await HeldHorizon.HoldAsync(horizon, cancellationToken);
      disturbed = await _measureAtDepthAsync("force_custom_plan", "control", BACKLOG, measure, cancellationToken, disturb: true);
    }

    await Assert.That(disturbed.ByRelation).IsEquivalentTo(plain.ByRelation)
      .Because($"the same call on the same fill reads the same rows whatever another session holds ({call}): "
        + $"{plain} undisturbed, {disturbed} disturbed");
  }

  /// <summary>Fetches the batch (its lease written), then measures the completion that retires it.</summary>
  private static async Task<RowsReadResult> _measureCompleteAsync(
      NpgsqlConnection conn, Guid[] batch, CancellationToken cancellationToken) {
    _ = await _fetchAsync(conn, batch, cancellationToken);
    var read = await RowsRead.MeasureAsync(conn, () => _completeAsync(conn, batch, cancellationToken),
      commit: true, cancellationToken);
    await Assert.That(await _pendingAsync(conn, batch, cancellationToken)).IsEqualTo(0L)
      .Because("the completion retires every row of its batch, or it measured nothing");
    return read;
  }

  /// <summary>Fetches and completes the batch, then measures the eviction of its streams.</summary>
  private static async Task<RowsReadResult> _measureCleanupAsync(
      NpgsqlConnection conn, Guid[] batch, CancellationToken cancellationToken) {
    _ = await _fetchAsync(conn, batch, cancellationToken);
    await _completeAsync(conn, batch, cancellationToken);
    var evicted = 0;
    var read = await RowsRead.MeasureAsync(conn, async () => {
      await using var cleanup = conn.CreateCommand();
      cleanup.CommandText = "SELECT cleanup_completed_streams(@streams)";
      cleanup.Parameters.AddWithValue("streams", batch);
      evicted = Convert.ToInt32(await cleanup.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }, commit: true, cancellationToken);
    await Assert.That(evicted).IsEqualTo(BATCH).Because("every stream of the batch is left with nothing to do, or it measured nothing");
    return read;
  }

  /// <summary>
  /// The eviction's probes keep what the anti joins decided (536): a stream with a pending outbox row, a pending inbox
  /// row or a pending perspective row stays in the ledger, and a stream whose rows are all processed, or that has none,
  /// leaves it.
  /// </summary>
  [Test]
  public async Task CleanupCompletedStreams_EvictsOnlyStreamsWithNoPendingWorkAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var outboxBusy = (Guid)TrackedGuid.New();
    var inboxBusy = (Guid)TrackedGuid.New();
    var perspectiveBusy = (Guid)TrackedGuid.New();
    var processed = (Guid)TrackedGuid.New();
    var idle = (Guid)TrackedGuid.New();
    Guid[] all = [outboxBusy, inboxBusy, perspectiveBusy, processed, idle];
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
        SELECT s, 0, @instance, NOW(), NOW() FROM unnest(@all) s;

        INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
                               stream_id, partition_number)
        VALUES (gen_random_uuid(), 'topic', 'Perf.Message, Perf', '{}'::jsonb, '{}'::jsonb, 0, 0, NOW(), @outboxBusy, 0);

        WITH m AS (
          INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
          VALUES (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', NOW(), @inboxBusy)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, failure_reason)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 99 FROM m;

        INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, status, attempts, created_at,
                                           partition_number, processed_at)
        VALUES (@perspectiveBusy, @perspective, gen_random_uuid(), 0, 0, NOW(), 0, NULL),
               (@processed, @perspective, gen_random_uuid(), 0, 0, NOW(), 0, NOW());
        """;
      seed.Parameters.AddWithValue("instance", _instance);
      seed.Parameters.AddWithValue(nameof(all), all);
      seed.Parameters.AddWithValue(nameof(outboxBusy), outboxBusy);
      seed.Parameters.AddWithValue(nameof(inboxBusy), inboxBusy);
      seed.Parameters.AddWithValue(nameof(perspectiveBusy), perspectiveBusy);
      seed.Parameters.AddWithValue(nameof(processed), processed);
      seed.Parameters.AddWithValue("perspective", PERSPECTIVE);
      _ = await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var cleanup = conn.CreateCommand()) {
      cleanup.CommandText = "SELECT cleanup_completed_streams(@all)";
      cleanup.Parameters.AddWithValue(nameof(all), all);
      await Assert.That(Convert.ToInt32(await cleanup.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture))
        .IsEqualTo(2);
    }
    var remaining = new List<Guid>();
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT stream_id FROM wh_active_streams WHERE stream_id = ANY(@all)";
    read.Parameters.AddWithValue(nameof(all), all);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      remaining.Add(reader.GetGuid(0));
    }
    await Assert.That(remaining).IsEquivalentTo([outboxBusy, inboxBusy, perspectiveBusy]);
  }

  /// <summary>
  /// The eviction's outbox probe reads what its batch holds, not the outbox (#2538). Its check for a pending outbox row
  /// (processed_at IS NULL) matched no outbox index, so every candidate stream with no pending inbox or perspective work
  /// read the whole outbox: 753 blocks a stream at 12,500 pending rows, 381,015 for a batch of 1,000. The probe now asks
  /// for the rows the claim can still take (processed_at IS NULL AND published_at IS NULL), which idx_outbox_stream_run
  /// answers in one descent.
  /// </summary>
  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task CleanupCompletedStreams_WithAFullOutbox_ReadsWhatItsBatchHolds_NotTheOutboxAsync(
      string planMode, CancellationToken cancellationToken) {
    const int pendingOutbox = 12_500;
    Guid[] batch;
    // The horizon, held where the test puts it (#2587): from before the fill to the end of the measurement.
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await _prepareAsync(setup, cancellationToken);
      await HeldHorizon.HoldAsync(horizon, cancellationToken);
      batch = await _fillAsync(setup, "fill-outbox", BATCH, cancellationToken);
      _ = await _fetchAsync(setup, batch, cancellationToken);
      await _completeAsync(setup, batch, cancellationToken);
      await using var outbox = setup.CreateCommand();
      outbox.CommandTimeout = 300;
      // Pending rows of other streams: what the outbox holds while a service publishes, none of it the batch's.
      outbox.CommandText = """
        INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
                               stream_id, partition_number)
        SELECT md5('fill-outbox-message-' || n)::uuid, 'topic', 'Perf.Message, Perf', '{}'::jsonb, '{}'::jsonb, 0, 0, NOW(),
               md5('fill-outbox-other-stream-' || n)::uuid, 0
        FROM generate_series(1, @rows) n;
        """;
      outbox.Parameters.AddWithValue("rows", pendingOutbox);
      _ = await outbox.ExecuteNonQueryAsync(cancellationToken);
      await using var analyze = setup.CreateCommand();
      analyze.CommandTimeout = 300;
      analyze.CommandText = "VACUUM ANALYZE wh_outbox";
      _ = await analyze.ExecuteNonQueryAsync(cancellationToken);
    }
    // A fresh connection, so the call is planned in the mode under test and inherits no cached plan.
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var mode = conn.CreateCommand()) {
      mode.CommandText = $"SET plan_cache_mode = {planMode}";
      _ = await mode.ExecuteNonQueryAsync(cancellationToken);
    }

    var evicted = 0;
    var read = await RowsRead.MeasureAsync(conn, async () => {
      await using var cleanup = conn.CreateCommand();
      cleanup.CommandTimeout = 300;
      cleanup.CommandText = "SELECT cleanup_completed_streams(@streams)";
      cleanup.Parameters.AddWithValue("streams", batch);
      evicted = Convert.ToInt32(await cleanup.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }, commit: true, cancellationToken);
    Console.WriteLine($"MEASURE {planMode} cleanup over {pendingOutbox} pending outbox rows: {read}");

    await Assert.That(evicted).IsEqualTo(BATCH).Because("every stream of the batch is left with nothing to do, or it measured nothing");
    await Assert.That(read.Total).IsLessThanOrEqualTo((long)CLEANUP_ROWS_PER_STREAM * BATCH)
      .Because($"the outbox probe stops at the stream's first pending row in an index, whatever the outbox holds ({planMode}): {read}");
  }

  /// <summary>
  /// An outbox row stamped published is not pending work for the eviction (#2538), as it is not for the claim: the
  /// claim's run walk and the store's emptiness probe both skip a row with published_at set, and the framework sets it
  /// only together with processed_at. The eviction now asks the same question, so a stream whose only outbox row is
  /// published leaves the ledger; a stream with an unpublished pending row stays.
  /// </summary>
  [Test]
  public async Task CleanupCompletedStreams_AnOutboxRowAlreadyPublished_DoesNotHoldTheStreamAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var published = (Guid)TrackedGuid.New();
    var unpublished = (Guid)TrackedGuid.New();
    Guid[] all = [published, unpublished];
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
        SELECT s, 0, @instance, NOW(), NOW() FROM unnest(@all) s;

        INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
                               stream_id, partition_number, published_at)
        VALUES (gen_random_uuid(), 'topic', 'Perf.Message, Perf', '{}'::jsonb, '{}'::jsonb, 4, 0, NOW(), @published, 0, NOW()),
               (gen_random_uuid(), 'topic', 'Perf.Message, Perf', '{}'::jsonb, '{}'::jsonb, 0, 0, NOW(), @unpublished, 0, NULL);
        """;
      seed.Parameters.AddWithValue("instance", _instance);
      seed.Parameters.AddWithValue(nameof(all), all);
      seed.Parameters.AddWithValue(nameof(published), published);
      seed.Parameters.AddWithValue(nameof(unpublished), unpublished);
      _ = await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var cleanup = conn.CreateCommand()) {
      cleanup.CommandText = "SELECT cleanup_completed_streams(@all)";
      cleanup.Parameters.AddWithValue(nameof(all), all);
      await Assert.That(Convert.ToInt32(await cleanup.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture))
        .IsEqualTo(1);
    }
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT array_agg(stream_id) FROM wh_active_streams WHERE stream_id = ANY(@all)";
    read.Parameters.AddWithValue(nameof(all), all);
    var remaining = (Guid[])(await read.ExecuteScalarAsync(cancellationToken))!;
    await Assert.That(remaining).IsEquivalentTo([unpublished])
      .Because("the published row is no work the claim can take, so only the unpublished row holds its stream");
  }

  /// <summary>
  /// Measures one call over a backlog and over four times it, each time on a batch of new streams from the latest fill,
  /// and requires the deeper call to read no more than the shallower one and no more than
  /// <paramref name="rowsPerStream"/> rows per stream of its batch. The deeper round may read up to two more rows per
  /// stream of the batch: the shallower round's batch is still in the tables, leased, and the acquisition's window
  /// passes over each of those rows once and over the version its lease left dead once, which the held horizon keeps
  /// on every run (#2587). Growth with the backlog is three backlogs, fifteen times that allowance.
  /// </summary>
  private async Task _assertPricedByTheBatchAsync(
      string planMode, int rowsPerStream, Func<NpgsqlConnection, Guid[], Task<RowsReadResult>> call,
      CancellationToken cancellationToken) {
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await _prepareAsync(setup, cancellationToken);
    }
    // The horizon, held where the test puts it (#2587): from before the first fill to the end of the deeper round.
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await HeldHorizon.HoldAsync(horizon, cancellationToken);

    var shallow = await _measureAtDepthAsync(planMode, "fill-1", BACKLOG, call, cancellationToken);
    var deep = await _measureAtDepthAsync(planMode, "fill-2", DEEPER - BACKLOG, call, cancellationToken);
    var story = $"at {BACKLOG} pending and applied: {shallow}; at {DEEPER} pending and applied: {deep}";
    Console.WriteLine($"MEASURE {planMode} {story}");

    await Assert.That(deep.Total).IsLessThanOrEqualTo(shallow.Total + (2 * BATCH))
      .Because($"four times the backlog and history must not cost more for the same batch ({planMode}), {story}");
    await Assert.That(deep.Total).IsLessThanOrEqualTo((long)rowsPerStream * BATCH)
      .Because($"a call reads a few rows per stream of its batch ({planMode}), {story}");
  }

  /// <summary>Adds <paramref name="streams"/> new streams to the backlog, then measures the call on a batch of them.</summary>
  private async Task<RowsReadResult> _measureAtDepthAsync(
      string planMode, string fill, int streams, Func<NpgsqlConnection, Guid[], Task<RowsReadResult>> call,
      CancellationToken cancellationToken, bool disturb = false) {
    Guid[] batch;
    await using var elsewhere = new NpgsqlConnection(ConnectionString);
    if (disturb) {
      // Another session's open snapshot, as a loaded server has: the horizon stays where it was before the fill.
      await HeldHorizon.HoldAsync(elsewhere, cancellationToken);
    }
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      batch = await _fillAsync(setup, fill, streams, cancellationToken);
    }
    // A fresh connection, so every statement is planned in the mode under test and none inherits a cached plan.
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var mode = conn.CreateCommand()) {
      mode.CommandText = $"SET plan_cache_mode = {planMode}";
      _ = await mode.ExecuteNonQueryAsync(cancellationToken);
    }
    return await call(conn, batch);
  }

  /// <summary>
  /// The rows the store writes for one new event on each of <paramref name="streams"/> new streams (the event, its
  /// body, a perspective work row leased to the storing instance, the stream's ledger row), with the lease already
  /// lapsed, as it is for most of a fan-out that outlasts it; and as many streams already applied (the event, its
  /// cursor, its applied-ledger row). Then a vacuum and analyze so the planner sees the tables as they are. Returns a
  /// batch of the new streams.
  /// </summary>
  private static async Task<Guid[]> _fillAsync(
      NpgsqlConnection conn, string fill, int streams, CancellationToken cancellationToken) {
    await using (var insert = conn.CreateCommand()) {
      insert.CommandTimeout = 300;
      insert.CommandText = """
        WITH s AS MATERIALIZED (
          SELECT md5(@fill || '-stream-' || n)::uuid AS stream_id, md5(@fill || '-event-' || n)::uuid AS event_id, n
          FROM generate_series(1, @streams) n
        ),
        es AS (
          INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
                                      created_at, flags, commit_sequence)
          SELECT event_id, stream_id, stream_id, @fill, 'Perf.OrderCreated, Perf', NULL, 1, NOW(), 0,
                 COALESCE((SELECT max(commit_sequence) FROM wh_event_store), 0) + n
          FROM s
          RETURNING 1
        ),
        eb AS (
          INSERT INTO wh_event_body (event_id, event_data, metadata)
          SELECT event_id, '{"Amount":1}'::jsonb, '{"Hops":[]}'::jsonb FROM s
          RETURNING 1
        ),
        pe AS (
          INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, instance_id, lease_expiry,
                                             partition_number, status, priority, created_at)
          SELECT stream_id, @perspective, event_id, @instance, NOW() - INTERVAL '1 hour',
                 compute_partition(stream_id, 10000), 1, 150, NOW()
          FROM s
          RETURNING 1
        )
        INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
        SELECT stream_id, compute_partition(stream_id, 10000), @instance, NOW(), NOW() FROM s;

        WITH h AS MATERIALIZED (
          SELECT md5(@fill || '-applied-stream-' || n)::uuid AS stream_id, md5(@fill || '-applied-event-' || n)::uuid AS event_id, n
          FROM generate_series(1, @streams) n
        ),
        es AS (
          INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
                                      created_at, flags, commit_sequence)
          SELECT event_id, stream_id, stream_id, 'applied', 'Perf.OrderCreated, Perf', NULL, 1, NOW(), 0,
                 COALESCE((SELECT max(commit_sequence) FROM wh_event_store), 0) + n
          FROM h
          RETURNING 1
        ),
        applied AS (
          INSERT INTO wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
          SELECT event_id, @perspective, stream_id, NOW() FROM h
          RETURNING 1
        )
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
        SELECT stream_id, @perspective, event_id, 2, NOW() FROM h
        """;
      insert.Parameters.AddWithValue(nameof(streams), streams);
      insert.Parameters.AddWithValue(nameof(fill), fill);
      insert.Parameters.AddWithValue("perspective", PERSPECTIVE);
      insert.Parameters.AddWithValue("instance", _instance);
      _ = await insert.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var vacuum = conn.CreateCommand()) {
      vacuum.CommandTimeout = 300;
      vacuum.CommandText = "VACUUM ANALYZE wh_event_store, wh_event_body, wh_perspective_events, wh_active_streams, wh_perspective_cursors, wh_perspective_applied";
      _ = await vacuum.ExecuteNonQueryAsync(cancellationToken);
    }
    var batch = new List<Guid>(BATCH);
    await using var pick = conn.CreateCommand();
    pick.CommandText = "SELECT stream_id FROM wh_event_store WHERE aggregate_type = @fill ORDER BY stream_id LIMIT @batch";
    pick.Parameters.AddWithValue(nameof(fill), fill);
    pick.Parameters.AddWithValue(nameof(batch), BATCH);
    await using var reader = await pick.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      batch.Add(reader.GetGuid(0));
    }
    return [.. batch];
  }

  /// <summary>
  /// Registers the instance and turns autovacuum off on the measured tables: the preparing calls rewrite every row of a
  /// batch, which is enough to make autovacuum analyze a table whenever it next visits this database, and an analyze
  /// between the fill's and the measured call rewrites the statistics the call is planned from.
  /// </summary>
  private static async Task _prepareAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await HeldHorizon.DisableAutovacuumAsync(conn, _measuredTables, cancellationToken);
    await using var register = conn.CreateCommand();
    register.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@instance, 'perf', 'perf-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    register.Parameters.AddWithValue("instance", _instance);
    _ = await register.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>The drain's fetch for the batch; returns the rows it handed back.</summary>
  private static async Task<long> _fetchAsync(NpgsqlConnection conn, Guid[] batch, CancellationToken cancellationToken) {
    await using var fetch = conn.CreateCommand();
    fetch.CommandText = "SELECT count(*) FROM get_stream_events(@instance, @streams)";
    fetch.Parameters.AddWithValue("instance", _instance);
    fetch.Parameters.AddWithValue("streams", batch);
    return (long)(await fetch.ExecuteScalarAsync(cancellationToken))!;
  }

  /// <summary>One completion flush for every row of the batch: each pair's cursor and every work row.</summary>
  private static async Task _completeAsync(NpgsqlConnection conn, Guid[] batch, CancellationToken cancellationToken) {
    await using var complete = conn.CreateCommand();
    complete.CommandText = """
      WITH rows AS (
        SELECT pe.event_work_id, pe.stream_id, pe.perspective_name, pe.event_id
        FROM wh_perspective_events pe
        WHERE pe.stream_id = ANY(@streams) AND pe.processed_at IS NULL
      )
      SELECT complete_perspective(
        (SELECT jsonb_agg(jsonb_build_object('StreamId', stream_id, 'PerspectiveName', perspective_name,
                                             'LastEventId', event_id, 'Status', 1,
                                             'ProcessedEventIds', jsonb_build_array(event_id))) FROM rows),
        (SELECT array_agg(event_work_id) FROM rows),
        false)
      """;
    complete.Parameters.AddWithValue("streams", batch);
    _ = await complete.ExecuteScalarAsync(cancellationToken);
  }

  private static async Task<long> _pendingAsync(NpgsqlConnection conn, Guid[] batch, CancellationToken cancellationToken) {
    await using var pending = conn.CreateCommand();
    pending.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE stream_id = ANY(@streams) AND processed_at IS NULL";
    pending.Parameters.AddWithValue("streams", batch);
    return (long)(await pending.ExecuteScalarAsync(cancellationToken))!;
  }
}
