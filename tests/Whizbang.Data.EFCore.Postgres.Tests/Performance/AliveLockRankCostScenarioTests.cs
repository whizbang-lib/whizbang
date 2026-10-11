// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What ranking peers by their alive-lock costs a self-ranked claim (#1286): a fleet of direct instances with fresh
/// heartbeats against the same fleet with half of it on the slow cadence (heartbeats 45 seconds old, past the
/// 30 second cutoff) and holding their locks.
/// </summary>
/// <remarks>
/// <para>
/// The rule: a peer is live by a fresh heartbeat, or, when it is direct, by its held alive-lock. The lock is read only
/// for a direct row past the cutoff, as a hashed subplan built once per claim: a fleet with fresh rows never reads
/// <c>pg_locks</c>, and a fleet on the slow cadence reads it once (no shared buffers: it is shared memory) plus one more
/// pass over the registration rows. The scenario gates the difference at that one pass (and the registry's own growth
/// from the fixture's update, which the claim's pass reads in either case), and
/// checks that the slow cadence leaves every share where it was: the claim takes the same partitions in both states.
/// </para>
/// <para>
/// The ranking statement is explained on its own as well, because <c>EXPLAIN</c> of the claim shows only the function
/// call: there the lock read (<c>pg_lock_status</c>) can be seen never executing for the fresh fleet and executing
/// once for the slow one. Its text is claim_work's ranking statement (204), with the cutoff bound as a parameter.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
[Category("Benchmark")]
[Category("Performance")]
public class AliveLockRankCostScenarioTests : EFCoreTestBase {
  private const int STREAMS = 30;
  private static readonly Guid _claimer = new("ffffffff-0000-0000-0000-0000000000a1");

  private const string CLAIM = @"SELECT count(*) FROM claim_work(
    p_instance_id => @claimer, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
    p_max_streams => 30, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 30)";

  private const string RANK = @"
    SELECT ranked.instance_rank, ranked.active_instance_count
    FROM (
      SELECT live.instance_id,
             (ROW_NUMBER() OVER (ORDER BY live.instance_id) - 1)::INTEGER AS instance_rank,
             COUNT(*) OVER ()::INTEGER AS active_instance_count
      FROM (
        SELECT si.instance_id
        FROM wh_service_instances si
        WHERE si.last_heartbeat_at >= @cutoff
           OR (si.connection_mode = 'direct'
               AND si.instance_id IN (SELECT h.instance_id FROM wh_direct_alive_lock_holders() h))
        UNION
        SELECT @claimer
      ) live
    ) ranked
    WHERE ranked.instance_id = @claimer";

  [Test]
  [Arguments(20)]
  [Arguments(400)]
  [Timeout(600000)]
  public async Task SelfRankedClaim_WithHalfTheFleetOnTheSlowCadence_ReadsTheLocksOnce_ReportAsync(
      int instances, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Alive-lock rank, self-ranked claim cost");
    var slowHalf = await _fillAsync(conn, instances);
    await _compactAsync(conn);
    var registryPages = await _registryPagesAsync(conn);
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await _holdLocksAsync(holder, slowHalf);

    // The first claim on a session compiles its statements and warms the catalog; a claimer pays that once.
    _ = await _claimAsync(conn, cancellationToken);
    var (fresh, freshPartitions) = await _claimAsync(conn, cancellationToken);
    var freshRank = await _rankPlanAsync(conn, cancellationToken);

    await _ageAsync(conn, slowHalf);
    // The aging update leaves a second version of each row it touched; compacted, the registry is the same size in
    // both states, so the difference is the work the rule adds and not the fixture's own update.
    await _compactAsync(conn);
    var slowRegistryPages = await _registryPagesAsync(conn);
    _ = await _claimAsync(conn, cancellationToken);
    var (slow, slowPartitions) = await _claimAsync(conn, cancellationToken);
    var slowRank = await _rankPlanAsync(conn, cancellationToken);

    var extra = slow.SharedBuffersVisited - fresh.SharedBuffersVisited;
    // The claim's own pass reads the registry as it is in each state; the rule adds one more pass in the slow one.
    var allowed = (slowRegistryPages - registryPages) + slowRegistryPages;
    report.Measure($"alive_lock_rank.claim.fresh.instances_{instances}.shared_buffers", fresh.SharedBuffersVisited, "buffers/claim");
    report.Measure($"alive_lock_rank.claim.slow_cadence.instances_{instances}.shared_buffers", slow.SharedBuffersVisited, "buffers/claim");
    report.Measure($"alive_lock_rank.rank.fresh.instances_{instances}.ms", freshRank.ActualTotalTimeMs, "ms/rank");
    report.Measure($"alive_lock_rank.rank.slow_cadence.instances_{instances}.ms", slowRank.ActualTotalTimeMs, "ms/rank");
    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine($"registry pages: {registryPages} fresh, {slowRegistryPages} slow; extra buffers on the slow cadence: {extra} (allowed {allowed})");
    Console.WriteLine("rank, fresh:\n" + _describe(freshRank) + "rank, slow cadence:\n" + _describe(slowRank));
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());

    await Assert.That(_lockReads(freshRank)).IsEqualTo(0L)
      .Because($"with every heartbeat fresh the lock is never consulted, so pg_locks is never read. {freshRank.Json}");
    await Assert.That(_lockReads(slowRank)).IsEqualTo(1L)
      .Because($"the lock read is a hashed subplan built once per claim, never once per row. {slowRank.Json}");
    await Assert.That(slowPartitions).IsEquivalentTo(freshPartitions)
      .Because("a direct peer holding its lock is live on the slow cadence, so every share stays where it was");
    await Assert.That(extra).IsLessThanOrEqualTo(allowed)
      .Because($"the slow cadence costs one more pass over the registration rows and nothing else. {rendered}");
    await Assert.That(report.Breaches).IsEmpty().Because($"a declared ceiling was passed. {rendered}");
  }

  private static string _describe(QueryPlan plan) => string.Concat(plan.Nodes.Select(n =>
    $"  {n.NodeType} {n.RelationName ?? n.IndexName ?? n.FunctionName} loops={n.ActualLoops} rows={n.ActualRows} hit={n.SharedHit}\n"));

  /// <summary>How many times the lock table was read: the loops of every <c>pg_lock_status</c> function scan.</summary>
  private static long _lockReads(QueryPlan plan) =>
    plan.Nodes.Where(n => string.Equals(n.FunctionName, "pg_lock_status", StringComparison.Ordinal)).Sum(n => n.ActualLoops);

  private static async Task<(QueryPlan Plan, List<int> Partitions)> _claimAsync(
      NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var tx = await conn.BeginTransactionAsync(cancellationToken);
    var plan = await QueryPlan.CaptureAsync(conn, CLAIM, new Dictionary<string, object?> { ["claimer"] = _claimer }, cancellationToken);
    var partitions = new List<int>();
    await using (var leased = conn.CreateCommand()) {
      leased.CommandText = "SELECT partition_number FROM wh_outbox WHERE instance_id = @claimer ORDER BY partition_number";
      leased.Parameters.AddWithValue("claimer", _claimer);
      await using var reader = await leased.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        partitions.Add(reader.GetInt32(0));
      }
    }
    await tx.RollbackAsync(cancellationToken);
    return (plan, partitions);
  }

  private static async Task<QueryPlan> _rankPlanAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var tx = await conn.BeginTransactionAsync(cancellationToken);
    var plan = await QueryPlan.CaptureAsync(conn, RANK, new Dictionary<string, object?> {
      ["claimer"] = _claimer,
      ["cutoff"] = DateTimeOffset.UtcNow.AddSeconds(-30),
    }, cancellationToken);
    await tx.RollbackAsync(cancellationToken);
    return plan;
  }

  /// <summary>Direct registrations with fresh heartbeats, thirty pending outbox rows; returns every other peer.</summary>
  private static async Task<List<Guid>> _fillAsync(NpgsqlConnection conn, int instances) {
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata, connection_mode)
        SELECT CASE WHEN n = 1 THEN @claimer ELSE gen_random_uuid() END, 'perf', 'perf-host', n, NOW(), NOW(),
               jsonb_build_object(), 'direct'
        FROM generate_series(1, @instances) n;

        INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
                               stream_id, partition_number)
        SELECT gen_random_uuid(), 'topic-perf', 'Perf.Events.SomethingHappened, Perf', jsonb_build_object(), jsonb_build_object(),
               0, 0, NOW() - INTERVAL '1 hour', s, compute_partition(s, 10000)
        FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, @streams)) streams;

        ANALYZE wh_service_instances;
        ANALYZE wh_outbox;
        """;
      cmd.Parameters.AddWithValue("claimer", _claimer);
      cmd.Parameters.AddWithValue(nameof(instances), instances);
      cmd.Parameters.AddWithValue("streams", STREAMS);
      await cmd.ExecuteNonQueryAsync();
    }
    var peers = new List<Guid>();
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT instance_id FROM wh_service_instances WHERE instance_id <> @claimer ORDER BY instance_id";
    read.Parameters.AddWithValue("claimer", _claimer);
    await using var reader = await read.ExecuteReaderAsync();
    var i = 0;
    while (await reader.ReadAsync()) {
      if (i++ % 2 == 0) {
        peers.Add(reader.GetGuid(0));
      }
    }
    return peers;
  }

  private static async Task _compactAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "VACUUM FULL wh_service_instances";
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _registryPagesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT pg_relation_size('wh_service_instances') / current_setting('block_size')::BIGINT";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>One session holding the alive-lock of each instance, as each instance's own connection would.</summary>
  private static async Task _holdLocksAsync(NpgsqlConnection holder, List<Guid> instances) {
    await using var cmd = holder.CreateCommand();
    cmd.CommandText = "SELECT bool_and(claim_instance_alive_lock(id)) FROM unnest(@ids) id";
    cmd.Parameters.AddWithValue("ids", instances.ToArray());
    if (await cmd.ExecuteScalarAsync() is not true) {
      throw new InvalidOperationException("an alive-lock was already held");
    }
  }

  /// <summary>Puts the instances on the slow cadence: heartbeats 45 seconds old, past the 30 second cutoff.</summary>
  private static async Task _ageAsync(NpgsqlConnection conn, List<Guid> instances) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '45 seconds' WHERE instance_id = ANY(@ids)";
    cmd.Parameters.AddWithValue("ids", instances.ToArray());
    await cmd.ExecuteNonQueryAsync();
  }
}
