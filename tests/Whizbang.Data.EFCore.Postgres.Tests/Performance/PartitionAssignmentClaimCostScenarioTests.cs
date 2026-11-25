// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the partition assignment costs a claim (#1254): a claim that presents a cached, current assignment against one
/// that ranks itself, and the keyed read a claimer refreshes its copy with.
/// </summary>
/// <remarks>
/// <para>
/// The owner's decision: no added work per claim. The claim's fence is one read of a one-row table (one page, read
/// without its index), and it replaces the ranking query over the live registration rows rather than adding to it. A
/// claim with the assignment is gated at no more than the self-ranked claim, at a small fleet and at a large one. One
/// pending outbox row on each of thirty streams; each claim is measured inside a transaction that is rolled back, so
/// both modes see the same rows.
/// </para>
/// <para>
/// What is compared is the rows each claim read (<see cref="RowsRead"/>), not the shared buffers it visited (#2462).
/// Buffers counted catalog reloads and index metapage rereads whenever another session had invalidated what this one
/// cached, and the dead rows an earlier rolled-back claim left, so the comparison failed under load and passed alone.
/// The tables are vacuumed before every measurement, which removes those dead rows (a version an aborted transaction
/// wrote is dead whatever the horizon), so each claim reads the same table state.
/// </para>
/// <para>
/// What the vacuum cannot decide alone is what the fill committed dead (the vote and the publication rewrite their rows)
/// and whether the fill's pages are marked all-visible: both follow the horizon, which any open snapshot holds back
/// (#2587). The claim was measured not to follow it (the same claim reads the same rows with a snapshot from before the
/// fill held elsewhere and with none), and each test holds one from before its fill (<see cref="HeldHorizon"/>) so that
/// stays true by construction if the fill or the claim changes. The fill derives its ids from their ordinals, so the
/// partitions the claimer owns are the same on every run, the claim's walks start at a fixed seed, and autovacuum is off
/// on the measured tables.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
[Category("Benchmark")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
[Category("Performance")]
public class PartitionAssignmentClaimCostScenarioTests : EFCoreTestBase {
  /// <summary>The refresh read's ceiling: one keyed read of a one-row table reads one row.</summary>
  private const int KEYED_READ_ROWS = 1;
  private const int STREAMS = 30;
  private static readonly Guid _claimer = new("00000000-0000-0000-0000-0000000000a1");

  /// <summary>Where every walk the claim makes starts (<c>whizbang.claim_walk_seed</c>, #2580).</summary>
  private const string WALK_SEED = "00000000-0000-0000-0000-0000000025a7";

  /// <summary>The tables the fill writes and the claim reads.</summary>
  private static readonly string[] _measuredTables = [
    "wh_service_instances", "wh_outbox", "wh_active_streams", "wh_role_assignments", "wh_role_candidates",
    "wh_published_partition_assignment", "wh_partition_ownership",
  ];

  private const string CLAIM = @"SELECT count(*) FROM claim_work(
    p_instance_id => @claimer, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
    p_max_streams => 30, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 30,
    p_assignment_epoch => @epoch, p_assignment_revision => @revision)";

  [Test]
  [Arguments(20)]
  [Arguments(400)]
  [Timeout(600000)]
  public async Task ClaimWithTheAssignment_CostsNoMoreThanRankingItself_ReportAsync(int instances, CancellationToken cancellationToken) {
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await using var conn = await _openAsync(horizon, cancellationToken);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Partition assignment, claim cost and refresh read");
    var version = await _fillAsync(conn, instances);

    var selfRanked = await _claimAsync(conn, null, cancellationToken);
    var assigned = await _claimAsync(conn, version, cancellationToken);
    var refresh = await RowsRead.MeasureAsync(conn, async () => {
      await using var read = conn.CreateCommand();
      read.CommandText = "SELECT count(*) FROM wh_read_partition_assignment()";
      _ = await read.ExecuteScalarAsync(cancellationToken);
    }, commit: false, cancellationToken);

    report.Measure($"partition_assignment.claim.self_ranked.instances_{instances}.rows_read", selfRanked.Total, "rows/claim");
    report.Measure($"partition_assignment.claim.assigned.instances_{instances}.rows_read", assigned.Total, "rows/claim");
    report.Measure("partition_assignment.refresh_read.rows_read", refresh.Total, "rows/read");
    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());

    await Assert.That(assigned.Total).IsLessThanOrEqualTo(selfRanked.Total)
      .Because($"the fence replaces the ranking query, so presenting the assignment adds no work to a claim. Assigned: {assigned}; self-ranked: {selfRanked}. {rendered}");
    await Assert.That(refresh.Total).IsLessThanOrEqualTo(KEYED_READ_ROWS)
      .Because($"the refresh path is one keyed read of a one-row table: {refresh}. {rendered}");
    await Assert.That(report.Breaches).IsEmpty().Because($"a declared ceiling was passed. {rendered}");
  }

  /// <summary>
  /// The claim's measured cost is the claim's alone (#2462): another session invalidating what this session cached about
  /// the claim's tables (an analyze, as autovacuum runs one on a loaded server) between two measurements of the same
  /// claim does not move the number. A count that includes catalog reloads, index metapage rereads or the debris an
  /// earlier rolled-back claim left behind measures the server and the order of the test, not the claim.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task TheClaimCost_IsNotMovedByAnotherSessionsWork_Async(CancellationToken cancellationToken) {
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await using var conn = await _openAsync(horizon, cancellationToken);
    var version = await _fillAsync(conn, 20);

    var first = await _claimAsync(conn, version, cancellationToken);
    await using (var other = new NpgsqlConnection(ConnectionString)) {
      await other.OpenAsync(cancellationToken);
      await using var analyze = other.CreateCommand();
      analyze.CommandText = "ANALYZE wh_service_instances; ANALYZE wh_outbox; ANALYZE wh_published_partition_assignment;";
      _ = await analyze.ExecuteNonQueryAsync(cancellationToken);
    }
    var second = await _claimAsync(conn, version, cancellationToken);

    await Assert.That(second.Total).IsEqualTo(first.Total)
      .Because($"the same claim on the same rows does the same work, whatever another session did in between. First: {first}; second: {second}");
  }

  /// <summary>
  /// The claim's measured cost is not moved by a horizon another session holds (#2587): the same claim on the same fill,
  /// measured while another session of this database holds a snapshot from before the fill and again once it has let
  /// go, reads the same rows from the same relations. Whether the vacuum before each claim may remove what the fill left
  /// dead (the vote and the publication rewrite their rows) and whether the fill's pages are marked all-visible are
  /// decided against that horizon, and on a loaded server something else holds it; this fails if the claim starts
  /// reading what those decisions change.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task TheClaimCost_IsNotMovedByAHorizonHeldElsewhere_Async(CancellationToken cancellationToken) {
    await using var horizon = new NpgsqlConnection(ConnectionString);
    await using var conn = await _openAsync(horizon, cancellationToken);
    PartitionAssignmentVersion version;
    RowsReadResult held;
    await using (var elsewhere = new NpgsqlConnection(ConnectionString)) {
      await HeldHorizon.HoldAsync(elsewhere, cancellationToken);
      version = await _fillAsync(conn, 20);
      held = await _claimAsync(conn, version, cancellationToken);
    }
    var released = await _claimAsync(conn, version, cancellationToken);

    await Assert.That(released.ByRelation).IsEquivalentTo(held.ByRelation)
      .Because($"the same claim on the same rows does the same work, whatever horizon another session holds. Held: {held}; released: {released}");
  }

  /// <summary>
  /// Turns autovacuum off on the measured tables (so no analyze rewrites the statistics between the fill's and the
  /// claim), holds the horizon on <paramref name="horizon"/> from before the fill (#2587), and opens the measuring
  /// connection with the walk seeded.
  /// </summary>
  private async Task<NpgsqlConnection> _openAsync(NpgsqlConnection horizon, CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await HeldHorizon.DisableAutovacuumAsync(conn, _measuredTables, cancellationToken);
    await HeldHorizon.HoldAsync(horizon, cancellationToken);
    await using var seed = conn.CreateCommand();
    seed.CommandText = $"SET whizbang.claim_walk_seed = '{WALK_SEED}'";
    _ = await seed.ExecuteNonQueryAsync(cancellationToken);
    return conn;
  }

  /// <summary>
  /// One claim, rolled back, measured in rows read. The database is vacuumed first, so the claim reads the rows the fill
  /// left and none an earlier rolled-back claim left dead.
  /// </summary>
  private static async Task<RowsReadResult> _claimAsync(
      NpgsqlConnection conn, PartitionAssignmentVersion? version, CancellationToken cancellationToken) {
    await using (var vacuum = conn.CreateCommand()) {
      vacuum.CommandText = "VACUUM";
      vacuum.CommandTimeout = 300;
      _ = await vacuum.ExecuteNonQueryAsync(cancellationToken);
    }
    return await RowsRead.MeasureAsync(conn, async () => {
      await using var claim = conn.CreateCommand();
      claim.CommandText = CLAIM;
      claim.Parameters.AddWithValue("claimer", _claimer);
      claim.Parameters.Add(new NpgsqlParameter("epoch", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)version?.Epoch ?? DBNull.Value });
      claim.Parameters.Add(new NpgsqlParameter("revision", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)version?.Revision ?? DBNull.Value });
      _ = await claim.ExecuteScalarAsync(cancellationToken);
    }, commit: false, cancellationToken);
  }

  /// <summary>Live registrations, the claimer elected and publishing all of them, thirty pending outbox rows.</summary>
  private static async Task<PartitionAssignmentVersion> _fillAsync(NpgsqlConnection conn, int instances) {
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        SELECT CASE WHEN n = 1 THEN @claimer ELSE md5('partition-claim-instance-' || n)::uuid END, 'perf', 'perf-host', n,
               NOW(), NOW(), jsonb_build_object()
        FROM generate_series(1, @instances) n;

        INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
                               stream_id, partition_number)
        SELECT md5('partition-claim-message-' || n)::uuid, 'topic-perf', 'Perf.Events.SomethingHappened, Perf',
               jsonb_build_object(), jsonb_build_object(), 0, 0, NOW() - INTERVAL '1 hour', s, compute_partition(s, 10000)
        FROM (SELECT n, md5('partition-claim-stream-' || n)::uuid AS s FROM generate_series(1, @streams) n) streams;

        ANALYZE wh_service_instances;
        ANALYZE wh_outbox;
        """;
      cmd.Parameters.AddWithValue("claimer", _claimer);
      cmd.Parameters.AddWithValue(nameof(instances), instances);
      cmd.Parameters.AddWithValue("streams", STREAMS);
      await cmd.ExecuteNonQueryAsync();
    }
    long epoch;
    await using (var vote = conn.CreateCommand()) {
      vote.CommandText = """
        SELECT epoch FROM wh_vote_role(@role, @claimer, INTERVAL '5 minutes', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)
        """;
      vote.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      vote.Parameters.AddWithValue("claimer", _claimer);
      epoch = Convert.ToInt64(await vote.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    await using var publish = conn.CreateCommand();
    publish.CommandText = """
      SELECT wh_publish_partition_assignment(@role, @claimer, @epoch,
        (SELECT array_agg(instance_id ORDER BY instance_id) FROM wh_service_instances), INTERVAL '5 minutes')
      """;
    publish.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    publish.Parameters.AddWithValue("claimer", _claimer);
    publish.Parameters.AddWithValue("epoch", epoch);
    var revision = Convert.ToInt64(await publish.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    return new PartitionAssignmentVersion(epoch, revision);
  }
}
