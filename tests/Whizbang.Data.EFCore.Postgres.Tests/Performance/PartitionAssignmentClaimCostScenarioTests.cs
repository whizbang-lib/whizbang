// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the partition assignment costs a claim (#1254): a claim that presents a cached, current assignment against one
/// that ranks itself, and the keyed read a claimer refreshes its copy with.
/// </summary>
/// <remarks>
/// The owner's decision: no added work per claim. The claim's fence is one read of a one-row table (one page, read
/// without its index), and it replaces the ranking query over the live registration rows rather than adding to it. A
/// claim with the assignment is gated at no more than the self-ranked claim, at a small fleet and at a large one. One
/// pending outbox row on each of thirty streams; each claim is measured inside a transaction that is rolled back, so
/// both modes see the same rows.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
[Category("Benchmark")]
[Category("Performance")]
public class PartitionAssignmentClaimCostScenarioTests : EFCoreTestBase {
  /// <summary>The refresh read's ceiling: one keyed read of a one-row table, an index page and a heap page.</summary>
  private const int KEYED_READ_BUFFERS = 2;
  private const int STREAMS = 30;
  private static readonly Guid _claimer = new("00000000-0000-0000-0000-0000000000a1");

  private const string CLAIM = @"SELECT count(*) FROM claim_work(
    p_instance_id => @claimer, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
    p_max_streams => 30, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 30,
    p_assignment_epoch => @epoch, p_assignment_revision => @revision)";

  [Test]
  [Arguments(20)]
  [Arguments(400)]
  [Timeout(600000)]
  public async Task ClaimWithTheAssignment_CostsNoMoreThanRankingItself_ReportAsync(int instances, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Partition assignment, claim cost and refresh read");
    var version = await _fillAsync(conn, instances);

    // The first claim of each kind on a session compiles its statements and warms the catalog for the tables it reads;
    // a claimer pays that once, so it is not a claim's cost.
    _ = await _planAsync(conn, null, cancellationToken);
    _ = await _planAsync(conn, version, cancellationToken);
    var selfRanked = await _planAsync(conn, null, cancellationToken);
    var assigned = await _planAsync(conn, version, cancellationToken);
    QueryPlan refresh;
    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      refresh = await QueryPlan.CaptureAsync(conn, "SELECT * FROM wh_read_partition_assignment()",
        new Dictionary<string, object?>(), cancellationToken);
      await tx.RollbackAsync(cancellationToken);
    }

    report.Measure($"partition_assignment.claim.self_ranked.instances_{instances}.shared_buffers", selfRanked.SharedBuffersVisited, "buffers/claim");
    report.Measure($"partition_assignment.claim.assigned.instances_{instances}.shared_buffers", assigned.SharedBuffersVisited, "buffers/claim");
    report.Measure("partition_assignment.refresh_read.shared_buffers", refresh.SharedBuffersVisited, "buffers/read");
    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());

    await Assert.That(assigned.SharedBuffersVisited).IsLessThanOrEqualTo(selfRanked.SharedBuffersVisited)
      .Because($"the fence replaces the ranking query, so presenting the assignment adds no work to a claim. {rendered}");
    await Assert.That(refresh.SharedBuffersVisited).IsLessThanOrEqualTo(KEYED_READ_BUFFERS)
      .Because($"the refresh path is one keyed read of a one-row table. {rendered}");
    await Assert.That(report.Breaches).IsEmpty().Because($"a declared ceiling was passed. {rendered}");
  }

  private static async Task<QueryPlan> _planAsync(
      NpgsqlConnection conn, PartitionAssignmentVersion? version, CancellationToken cancellationToken) {
    await using var tx = await conn.BeginTransactionAsync(cancellationToken);
    var plan = await QueryPlan.CaptureAsync(conn, CLAIM, new Dictionary<string, object?> {
      ["claimer"] = _claimer,
      ["epoch"] = version?.Epoch,
      ["revision"] = version?.Revision,
    }, cancellationToken);
    await tx.RollbackAsync(cancellationToken);
    return plan;
  }

  /// <summary>Live registrations, the claimer elected and publishing all of them, thirty pending outbox rows.</summary>
  private static async Task<PartitionAssignmentVersion> _fillAsync(NpgsqlConnection conn, int instances) {
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        SELECT CASE WHEN n = 1 THEN @claimer ELSE gen_random_uuid() END, 'perf', 'perf-host', n, NOW(), NOW(), jsonb_build_object()
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
