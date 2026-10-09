// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the outbox acquisition's stream-ledger write costs per claim, when one claim refreshes the streams it owns,
/// takes over the streams of an instance that is gone, and pins streams nobody has recorded yet.
/// </summary>
/// <remarks>
/// <para>
/// Every claim that leases outbox rows writes one <c>wh_active_streams</c> row per stream it leased on. Migration 200
/// (#1238) changed how those rows are locked: one pass over every existing row in <c>stream_id</c> order instead of
/// the refreshes first and the pins after, which two claims could interleave into a deadlock. The change is a
/// correctness fix, and the claim runs several times a second on every instance, so it must not cost more.
/// </para>
/// <para>
/// One claim is measured per round, over streams split evenly between the three ledger paths, so every path the
/// statement has is exercised in each measured call. The ledger and outbox blocks come from the table counters; the
/// whole call's shared buffers come from its plan. Nothing is vacuumed and autovacuum is off, as the other cost
/// scenarios do.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Benchmark")]
[Category("Performance")]
public class ClaimLedgerWriteCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams per ledger path: owned by the claimer, owned by a deregistered instance, not recorded.</summary>
  private const int STREAMS_PER_PATH = 10;
  private const int STREAMS = STREAMS_PER_PATH * 3;
  /// <summary>Claims measured.</summary>
  private const int ROUNDS = 6;

  private static readonly Guid _claimer = new("aaaaaaaa-0000-0000-0000-0000000000a1");
  private static readonly Guid _departed = new("cccccccc-0000-0000-0000-0000000000c3");

  private static readonly string[] _measuredTables = ["wh_active_streams", "wh_outbox"];

  private const string CLAIM = @"SELECT count(*) FROM claim_orphaned_outbox(
    @claimer, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '30 seconds', @rows, 1, @rows)";

  [Test]
  [Timeout(600000)]
  public async Task ClaimLedgerWrite_RefreshTakeoverAndPin_CostPerClaim_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Claim ledger write cost, refresh + takeover + pin");

    // The first claim on a session compiles the function; a claimer pays that once, so it is not a claim's cost.
    await _fillAsync(conn);
    _ = await _claimAsync(conn);

    long ledgerBlocks = 0;
    long outboxBlocks = 0;
    long ledgerOwned = 0;
    for (var round = 0; round < ROUNDS; round++) {
      await _fillAsync(conn);
      var window = await recorder.MeasureAsync(conn, _measuredTables, async () => _ = await _claimAsync(conn));
      ledgerBlocks += window.Tables["wh_active_streams"].Blocks;
      outboxBlocks += window.Tables["wh_outbox"].Blocks;
      ledgerOwned += await _ownedByTheClaimerAsync(conn);
    }

    await _fillAsync(conn);
    QueryPlan plan;
    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      plan = await QueryPlan.CaptureAsync(conn, CLAIM,
        new Dictionary<string, object?> { ["claimer"] = _claimer, ["rows"] = STREAMS }, cancellationToken);
      await tx.RollbackAsync(cancellationToken);
    }

    report.Measure("claim.ledger_write.wh_active_streams.blocks_per_claim", (double)ledgerBlocks / ROUNDS, "blocks/claim");
    report.Measure("claim.ledger_write.wh_outbox.blocks_per_claim", (double)outboxBlocks / ROUNDS, "blocks/claim");
    report.Measure("claim.ledger_write.shared_buffers_per_claim", plan.SharedBuffersVisited, "buffers/claim");
    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());

    await Assert.That(ledgerOwned).IsEqualTo((long)STREAMS * ROUNDS)
      .Because("every measured claim must reach all three ledger paths and leave the claimer owning every stream, or "
        + "a claim that skipped the ledger would report a perfect score");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nThe ledger write is one primary-key probe and one write "
        + "per claimed stream, whichever path the stream takes");
  }

  private static async Task<long> _claimAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = CLAIM;
    cmd.Parameters.AddWithValue("claimer", _claimer);
    cmd.Parameters.AddWithValue("rows", STREAMS);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<long> _ownedByTheClaimerAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_active_streams WHERE assigned_instance_id = @claimer AND lease_expiry > NOW()";
    cmd.Parameters.AddWithValue("claimer", _claimer);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The claimer registered and live; the departed instance has no registration. A third of the streams are the
  /// claimer's under a live lease, a third the departed instance's under a live lease, a third have no ledger row.
  /// Each stream has one unowned pending outbox row.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_active_streams SET (autovacuum_enabled = false);
      TRUNCATE wh_outbox, wh_active_streams;
      DELETE FROM wh_service_instances;

      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{_claimer}', 'perf', 'perf-host', 1, NOW() + INTERVAL '1 hour', NOW(), jsonb_build_object());

      CREATE TEMP TABLE IF NOT EXISTS perf_ledger_streams (n int, stream_id uuid);
      TRUNCATE perf_ledger_streams;
      INSERT INTO perf_ledger_streams SELECT n, gen_random_uuid() FROM generate_series(1, {STREAMS}) n;

      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      SELECT s.stream_id, compute_partition(s.stream_id, 10000),
             CASE WHEN s.n % 3 = 0 THEN '{_claimer}'::uuid ELSE '{_departed}'::uuid END,
             NOW() + INTERVAL '1 hour', NOW() - INTERVAL '1 minute'
      FROM perf_ledger_streams s
      WHERE s.n % 3 <> 2;

      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-perf', 'Perf.Events.SomethingHappened, Perf', 'MessageEnvelope',
             jsonb_build_object('payload', repeat('x', 200)), jsonb_build_object('hops', jsonb_build_array()),
             jsonb_build_object('t', 'tenant-perf'), s.stream_id, compute_partition(s.stream_id, 10000), true, 1, 0,
             NOW() - INTERVAL '1 hour' + (s.n * INTERVAL '1 millisecond')
      FROM perf_ledger_streams s;

      ANALYZE wh_outbox;
      ANALYZE wh_active_streams;
      """;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }
}
