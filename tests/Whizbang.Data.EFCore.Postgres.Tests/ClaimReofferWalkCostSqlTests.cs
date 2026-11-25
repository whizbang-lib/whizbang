// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Performance;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the claim's re-offer walk reads when the statistics say every lease has lapsed (#2474): one probe per held
/// stream, priced by the batch, never a read of every live lease per step of the walk.
/// </summary>
/// <remarks>
/// <para>
/// A fan-out that outlasts the lease its rows were stored under leaves them lapsed, and the statistics are taken then.
/// The claim acquires them again, so their leases are live, and the statistics still say none is. A walk that tests the
/// lease as an index condition then looks cheapest through the lease-expiry index, which the planner believes holds no
/// live lease, and reads every live lease on every step: for 1,000 held streams one claim read 1,001,001 index entries
/// (#2286). The walk is one probe per stream only through the held-lanes index, which the lease test must not displace.
/// </para>
/// <para>
/// Each walk is measured in rows read (<see cref="RowsRead"/>, on a database this test alone uses), over a batch of
/// held streams and over four times as many: a walk priced by the batch reads four times as much, one priced by the
/// batch squared sixteen. The claim measured is the one after the acquisition, so it acquires nothing and its rows read
/// are the walk's and the guards'. <c>claim_work</c> plans for its arguments whatever the session asks (its own
/// <c>plan_cache_mode</c> clause, 157), so the generic run proves that the session cannot hand it a generic plan.
/// </para>
/// <para>
/// Rows read are work counters, not blocks, and the test holds the one input on a shared server that moves them
/// (#2587). Whether the vacuum after the acquisition may remove the versions it left dead, whether a scan may skip one,
/// and whether the fill's pages are marked all-visible (which the planner prices index-only scans by) are decided by
/// the horizon, and any open snapshot holds that back: a measurement that vacuumed them away in isolation read every
/// dead entry again when something else held the horizon, and the deeper batch could pass the four-times bound with
/// the claim unchanged. Each measurement holds a snapshot from before its fill, so every one of those decisions is the
/// same on every run. With the dead versions kept, where the walk starts among the streams decides how many dead
/// entries its probes pass, so the walk is seeded (<see cref="WALK_SEED"/>) and the fills derive their ids from their
/// ordinals. The other server-wide input, a reset of the session's cached relation state when other
/// databases' schema changes overflow the invalidation queue, moves blocks (the reload rereads catalog and index
/// metapages) and nothing these counters count: catalogs are outside the schema, and <c>claim_work</c> plans every
/// statement on every execution anyway, so a reset cannot add a planning. The control
/// (<see cref="ReOfferMeasure_UnderAHorizonHeldElsewhereAndAnInvalidation_ReadsTheSameRowsAsync"/>) applies both and
/// requires the same rows from the same relations.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard5")]
[Category("Integration")]
[Category("Performance")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
public class ClaimReofferWalkCostSqlTests : EFCoreTestBase {
  private const int BATCH = 250;
  private const int DEEPER = 4 * BATCH;
  private static readonly Guid _instance = new("00000000-0000-0000-0000-0000000024b4");

  /// <summary>
  /// Where every re-offer walk starts (<c>whizbang.claim_walk_seed</c>, #2580). With the versions the acquisition left
  /// dead in place, where the walk starts among the streams decides how many dead entries its probes pass: unseeded, with
  /// ids drawn at random, one batch's probes read 502 to 994 held-lanes entries (#2587). The fills derive their ids from
  /// their ordinals, so with the start fixed every run walks the same streams in the same order.
  /// </summary>
  private const string WALK_SEED = "00000000-0000-0000-0000-000000002587";

  /// <summary>The tables the claim reads or writes in these measurements.</summary>
  private static readonly string[] _measuredTables = [
    "wh_perspective_events", "wh_inbox_state", "wh_inbox", "wh_active_streams", "wh_event_store", "wh_event_body",
  ];

  /// <summary>
  /// Rows a re-offer claim may read per held stream: the walk's probe (which, with the horizon held, also returns the
  /// entry of the version the acquisition left dead), the watermark's lease probe and its event-store lookup, with room
  /// for the guards and the lane probes, which read a few entries per claim.
  /// </summary>
  private const int ROWS_PER_HELD_STREAM = 6;

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task PerspectiveReOffer_OnStatisticsThatSayEveryLeaseLapsed_ReadsWhatItHolds_OncePerStreamAsync(
      string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, "perspective_stream", _fillPerspectiveAsync, cancellationToken);
  }

  [Test]
  [Arguments("force_custom_plan")]
  [Arguments("force_generic_plan")]
  [Timeout(600000)]
  public async Task InboxReOffer_OnStatisticsThatSayEveryLeaseLapsed_ReadsWhatItHolds_OncePerStreamAsync(
      string planMode, CancellationToken cancellationToken) {
    await _assertPricedByTheBatchAsync(planMode, "inbox", _fillInboxAsync, cancellationToken);
  }

  /// <summary>
  /// The measure is the re-offer's alone (#2587): the same re-offer, measured again while another session of this
  /// database holds a snapshot from before the fill and invalidates what the measuring session cached about the measured
  /// tables, reads the same rows from the same relations. Those are what a server other test processes share does to a
  /// measurement: an open snapshot holds back the horizon, which decides whether the vacuum after the acquisition may
  /// remove the versions the acquisition left dead (and whether the fill's pages are marked all-visible), and another
  /// database's schema changes overflow the invalidation queue, which drops the session's cached relation state.
  /// </summary>
  [Test]
  [Arguments("perspective_stream")]
  [Arguments("inbox")]
  [Timeout(600000)]
  public async Task ReOfferMeasure_UnderAHorizonHeldElsewhereAndAnInvalidation_ReadsTheSameRowsAsync(
      string source, CancellationToken cancellationToken) {
    var fill = source == "inbox" ? (Func<NpgsqlConnection, int, CancellationToken, Task>)_fillInboxAsync : _fillPerspectiveAsync;
    await _prepareAsync(cancellationToken);

    var plain = await _measureAsync("force_custom_plan", source, BATCH, fill, cancellationToken);
    var disturbed = await _measureAsync("force_custom_plan", source, BATCH, fill, cancellationToken, disturb: true);

    await Assert.That(disturbed.ByRelation).IsEquivalentTo(plain.ByRelation)
      .Because($"the same re-offer reads the same rows whatever another session holds or invalidates ({source}): "
        + $"{plain} undisturbed, {disturbed} disturbed");
  }

  /// <summary>
  /// Measures the re-offer over <see cref="BATCH"/> held streams and over four times as many, and requires the larger
  /// to read no more than four times the smaller (plus one row per stream of the smaller batch, for the guards) and no
  /// more than <see cref="ROWS_PER_HELD_STREAM"/> rows per stream it holds.
  /// </summary>
  private async Task _assertPricedByTheBatchAsync(
      string planMode, string source, Func<NpgsqlConnection, int, CancellationToken, Task> fill,
      CancellationToken cancellationToken) {
    await _prepareAsync(cancellationToken);

    var shallow = await _measureAsync(planMode, source, BATCH, fill, cancellationToken);
    var deep = await _measureAsync(planMode, source, DEEPER, fill, cancellationToken);
    var story = $"{BATCH} held streams: {shallow}; {DEEPER} held streams: {deep}";
    Console.WriteLine($"MEASURE {source} {planMode} {story}");

    await Assert.That(deep.Total).IsLessThanOrEqualTo((4 * shallow.Total) + BATCH)
      .Because($"four times the held streams cost four times the rows, not sixteen ({source}, {planMode}): {story}");
    await Assert.That(deep.Total).IsLessThanOrEqualTo((long)ROWS_PER_HELD_STREAM * DEEPER)
      .Because($"the walk reads one probe per held stream ({source}, {planMode}): {story}");
  }

  /// <summary>
  /// Registers the instance, builds the indexes the maintenance step builds before the first poll, and turns autovacuum
  /// off on the measured tables, so no analyze rewrites the statistics between the fill's and the measured claim: the
  /// acquisition changes every row, which is enough to make autovacuum analyze a table whenever it next visits this
  /// database, and the statistics it would take say every lease is live.
  /// </summary>
  private async Task _prepareAsync(CancellationToken cancellationToken) {
    await using var setup = new NpgsqlConnection(ConnectionString);
    await setup.OpenAsync(cancellationToken);
    foreach (var table in _measuredTables) {
      await _executeAsync(setup, $"ALTER TABLE {table} SET (autovacuum_enabled = false)", cancellationToken);
    }
    await _registerAsync(setup, cancellationToken);
    // As the maintenance step builds them after the migrations, before the first poll.
    foreach (var index in ConcurrentIndexBuild.FrameworkIndexes.Where(i => i.Table == "wh_inbox_state")) {
      _ = await ConcurrentIndexBuild.EnsureAsync(setup, "public", index, 300, cancellationToken: cancellationToken);
    }
  }

  /// <summary>
  /// Empties the queues, fills <paramref name="streams"/> held streams whose leases lapsed an hour ago, analyzes them
  /// (so the statistics say every lease lapsed), claims them once (so every lease is live and the statistics still say
  /// otherwise), and measures the next claim on a fresh connection in <paramref name="planMode"/>.
  /// </summary>
  private async Task<RowsReadResult> _measureAsync(
      string planMode, string source, int streams, Func<NpgsqlConnection, int, CancellationToken, Task> fill,
      CancellationToken cancellationToken, bool disturb = false) {
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await using var elsewhere = new NpgsqlConnection(ConnectionString);
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync(cancellationToken);
      await _executeAsync(setup, "TRUNCATE wh_perspective_events, wh_inbox_state, wh_inbox, wh_active_streams, wh_event_store, wh_event_body CASCADE", cancellationToken);
      // The horizon, held where the test puts it (#2587): from before the fill to the end of the measurement, so no
      // version the fill or the acquisition writes is removable, by a vacuum or by a scan, and no page the fill writes
      // is marked all-visible, whatever snapshot another session holds. On a shared server those decisions otherwise
      // follow whatever else is running.
      await _holdSnapshotAsync(horizon, cancellationToken);
      if (disturb) {
        // Another session's open snapshot, as a loaded server has: the horizon stays where it was before the fill.
        await _holdSnapshotAsync(elsewhere, cancellationToken);
      }
      await fill(setup, streams, cancellationToken);
      await _executeAsync(setup, "VACUUM ANALYZE wh_perspective_events, wh_inbox_state, wh_inbox, wh_active_streams, wh_event_store, wh_event_body", cancellationToken);
      var acquired = await _claimAsync(setup, source, streams, cancellationToken);
      await Assert.That(acquired).IsEqualTo(streams).Because($"precondition: the first claim takes back every held stream ({source})");
      // The versions the acquisition left dead stay, on every run: the held horizon keeps them, so the measured claim's
      // index probes return their entries too, the same number every time. Statistics stay as the fill analyzed them,
      // because autovacuum is off on the measured tables (_prepareAsync).
      // The planner never estimates fewer than one row, so one is what "none" looks like.
      await Assert.That(await _livePlannedAsync(setup, source, cancellationToken)).IsLessThanOrEqualTo(1.0)
        .Because($"precondition: the statistics say no lease is live ({source})");
    }
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _executeAsync(conn, $"SET plan_cache_mode = {planMode}", cancellationToken);
    var offered = 0;
    var read = await RowsRead.MeasureAsync(conn, async () => {
      if (disturb) {
        await _invalidateAsync(cancellationToken);
      }
      offered = await _claimAsync(conn, source, streams, cancellationToken);
    }, commit: true, cancellationToken);
    await Assert.That(offered).IsEqualTo(streams).Because($"the claim re-offers every held stream ({source}), or it measured nothing");
    return read;
  }

  /// <summary>
  /// Opens a repeatable-read transaction on <paramref name="conn"/> and takes its snapshot, which it holds until the
  /// connection closes: no row version that commits after it is removable, by a vacuum or by a scan, in this database.
  /// </summary>
  private static async Task _holdSnapshotAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await conn.OpenAsync(cancellationToken);
    await _executeAsync(conn, "BEGIN ISOLATION LEVEL REPEATABLE READ", cancellationToken);
    await _executeAsync(conn, "SELECT 1", cancellationToken);
  }

  /// <summary>
  /// From another session, rewrites the options of every measured table (to the value they hold), which invalidates
  /// what every session cached about them, as a reset of the invalidation queue does; the measuring session rebuilds
  /// its cache on its next access.
  /// </summary>
  private async Task _invalidateAsync(CancellationToken cancellationToken) {
    await using var other = new NpgsqlConnection(ConnectionString);
    await other.OpenAsync(cancellationToken);
    foreach (var table in _measuredTables) {
      await _executeAsync(other, $"ALTER TABLE {table} SET (autovacuum_enabled = false)", cancellationToken);
    }
  }

  /// <summary>
  /// One claim for a batch of <paramref name="streams"/>, its walks started at <see cref="WALK_SEED"/>; returns the rows
  /// of <paramref name="source"/> it returned.
  /// </summary>
  private static async Task<int> _claimAsync(NpgsqlConnection conn, string source, int streams, CancellationToken cancellationToken) {
    await using var claim = conn.CreateCommand();
    claim.CommandTimeout = 300;
    claim.CommandText = $"""
      SET whizbang.claim_walk_seed = '{WALK_SEED}';
      SELECT count(*) FILTER (WHERE source = @source)::int FROM claim_work(
        p_instance_id => @instance, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
        p_max_streams => @streams, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => @streams)
      """;
    claim.Parameters.AddWithValue(nameof(source), source);
    claim.Parameters.AddWithValue("instance", _instance);
    claim.Parameters.AddWithValue(nameof(streams), streams);
    return (int)(await claim.ExecuteScalarAsync(cancellationToken))!;
  }

  /// <summary>How many live leases the planner believes the measured table holds.</summary>
  private static async Task<double> _livePlannedAsync(NpgsqlConnection conn, string source, CancellationToken cancellationToken) {
    var table = source == "inbox" ? "wh_inbox_state" : "wh_perspective_events";
    await using var explain = conn.CreateCommand();
    explain.CommandText = $"EXPLAIN (FORMAT JSON) SELECT 1 FROM {table} WHERE lease_expiry > NOW() AND processed_at IS NULL";
    var plan = (string)(await explain.ExecuteScalarAsync(cancellationToken))!;
    using var json = System.Text.Json.JsonDocument.Parse(plan);
    return json.RootElement[0].GetProperty("Plan").GetProperty("Plan Rows").GetDouble();
  }

  /// <summary>
  /// One stored event on each of <paramref name="streams"/> new streams, its perspective work row leased to the instance
  /// with the lease lapsed an hour ago, and the stream's ledger row.
  /// </summary>
  private static Task _fillPerspectiveAsync(NpgsqlConnection conn, int streams, CancellationToken cancellationToken) =>
    _executeAsync(conn, """
      WITH s AS MATERIALIZED (
        SELECT md5('reoffer-walk-stream-' || n)::uuid AS stream_id, md5('reoffer-walk-event-' || n)::uuid AS event_id, n
        FROM generate_series(1, @streams) n
      ),
      es AS (
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
                                    created_at, flags, commit_sequence)
        SELECT event_id, stream_id, stream_id, 'perf', 'Perf.OrderCreated, Perf', NULL, 1, NOW(), 0, n FROM s
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
        SELECT stream_id, 'Perf.OrderPerspective', event_id, @instance, NOW() - INTERVAL '1 hour',
               compute_partition(stream_id, 10000), 1, 150, NOW()
        FROM s
        RETURNING 1
      )
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      SELECT stream_id, compute_partition(stream_id, 10000), @instance, NOW(), NOW() FROM s
      """, streams, cancellationToken);

  /// <summary>
  /// One command on each of <paramref name="streams"/> new streams, held by the instance with the lease lapsed an hour
  /// ago, never tried.
  /// </summary>
  private static Task _fillInboxAsync(NpgsqlConnection conn, int streams, CancellationToken cancellationToken) =>
    _executeAsync(conn, """
      WITH inserted AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT md5('reoffer-walk-message-' || g)::uuid, 'TestHandler', 'TestCommand', '{}', '{}',
               NOW() - INTERVAL '2 hours' + (g * INTERVAL '1 microsecond'), md5('reoffer-walk-stream-' || g)::uuid, FALSE, 150
        FROM generate_series(1, @streams) g
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT i.message_id, i.stream_id, i.received_at, 150, FALSE, 0, 0, compute_partition(i.stream_id, 10000),
             @instance, NOW() - INTERVAL '1 hour'
      FROM inserted i
      """, streams, cancellationToken);

  private static async Task _registerAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var register = conn.CreateCommand();
    register.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@instance, 'perf', 'perf-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    register.Parameters.AddWithValue("instance", _instance);
    _ = await register.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 300;
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>Runs a fill for <paramref name="streams"/> streams held by the instance.</summary>
  private static async Task _executeAsync(NpgsqlConnection conn, string sql, int streams, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 300;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(streams), streams);
    cmd.Parameters.AddWithValue("instance", _instance);
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
