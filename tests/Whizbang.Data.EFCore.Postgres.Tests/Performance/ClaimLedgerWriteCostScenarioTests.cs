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
/// What a claim's stream-ledger write costs, when one claim refreshes the streams it owns, takes over the streams of
/// an instance that is gone, and pins streams nobody has recorded yet: for the outbox acquisition, the inbox
/// acquisition, and a whole <c>claim_work</c> whose outbox and inbox acquisitions lease the same streams.
/// </summary>
/// <remarks>
/// <para>
/// Every claim that leases rows writes one <c>wh_active_streams</c> row per stream it leased on. Migration 200
/// (#1238) changed how the outbox acquisition locks those rows: one pass over every existing row in <c>stream_id</c>
/// order instead of the refreshes first and the pins after, which two claims could interleave into a deadlock.
/// Migration 202 (#1256) gave the inbox and perspective acquisitions the same pass and moved <c>claim_work</c>'s ledger
/// write after its last acquisition, once for every stream they leased, where each acquisition used to write its own.
/// These are correctness fixes, and the claim runs several times a second on every instance, so they must not cost
/// more.
/// </para>
/// <para>
/// One claim is measured per round, over streams split evenly between the three ledger paths, so every path the
/// statement has is exercised in each measured call. The table blocks come from the table counters; the whole call's
/// shared buffers come from its plan. Nothing is vacuumed and autovacuum is off, as the other cost scenarios do.
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

  private const string CLAIM = @"SELECT count(*) FROM claim_orphaned_outbox(
    @claimer, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '30 seconds', @rows, 1, @rows)";

  private const string CLAIM_INBOX = @"SELECT count(*) FROM claim_orphaned_inbox(
    @claimer, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '30 seconds', @rows)";

  private const string CLAIM_WORK = @"SELECT count(*) FROM claim_work(
    p_instance_id => @claimer, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
    p_max_streams => @rows, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => @rows,
    p_max_outbox_rows => @rows)";

  [Test]
  [Timeout(600000)]
  public Task ClaimLedgerWrite_RefreshTakeoverAndPin_CostPerClaim_ReportAsync(CancellationToken cancellationToken) =>
    _measureAsync(new Scenario("Claim ledger write cost, refresh + takeover + pin", "claim.ledger_write", CLAIM,
      Outbox: true, Inbox: false, ["wh_active_streams", "wh_outbox"]), cancellationToken);

  [Test]
  [Timeout(600000)]
  public Task InboxClaimLedgerWrite_RefreshTakeoverAndPin_CostPerClaim_ReportAsync(CancellationToken cancellationToken) =>
    _measureAsync(new Scenario("Inbox claim ledger write cost, refresh + takeover + pin", "claim.inbox_ledger_write",
      CLAIM_INBOX, Outbox: false, Inbox: true, ["wh_active_streams", "wh_inbox_state"]), cancellationToken);

  [Test]
  [Timeout(600000)]
  public Task ClaimWorkLedgerWrite_OutboxAndInboxOnTheSameStreams_CostPerClaim_ReportAsync(CancellationToken cancellationToken) =>
    _measureAsync(new Scenario("claim_work ledger write cost, outbox and inbox on the same streams",
      "claim.work_ledger_write", CLAIM_WORK, Outbox: true, Inbox: true,
      ["wh_active_streams", "wh_outbox", "wh_inbox_state"]), cancellationToken);

  /// <summary>One measured claim shape: the statement, the work it finds, and the tables whose blocks it reports.</summary>
  private sealed record Scenario(string Title, string Prefix, string Claim, bool Outbox, bool Inbox, string[] Tables);

  private async Task _measureAsync(Scenario scenario, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, scenario.Title);

    // The first claim on a session compiles the function; a claimer pays that once, so it is not a claim's cost.
    await _fillAsync(conn, scenario);
    _ = await _claimAsync(conn, scenario.Claim);

    var blocks = scenario.Tables.ToDictionary(t => t, _ => 0L);
    long ledgerOwned = 0;
    for (var round = 0; round < ROUNDS; round++) {
      await _fillAsync(conn, scenario);
      var window = await recorder.MeasureAsync(conn, scenario.Tables, async () => _ = await _claimAsync(conn, scenario.Claim));
      foreach (var table in scenario.Tables) {
        blocks[table] += window.Tables[table].Blocks;
      }
      ledgerOwned += await _ownedByTheClaimerAsync(conn);
    }

    await _fillAsync(conn, scenario);
    QueryPlan plan;
    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      plan = await QueryPlan.CaptureAsync(conn, scenario.Claim,
        new Dictionary<string, object?> { ["claimer"] = _claimer, ["rows"] = STREAMS }, cancellationToken);
      await tx.RollbackAsync(cancellationToken);
    }

    foreach (var table in scenario.Tables) {
      report.Measure($"{scenario.Prefix}.{table}.blocks_per_claim", (double)blocks[table] / ROUNDS, "blocks/claim");
    }
    report.Measure($"{scenario.Prefix}.shared_buffers_per_claim", plan.SharedBuffersVisited, "buffers/claim");
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

  private static async Task<long> _claimAsync(NpgsqlConnection conn, string claim) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = claim;
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
  /// Each stream has one unowned pending outbox row, inbox row, or both, as the scenario asks.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection conn, Scenario scenario) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_inbox_state SET (autovacuum_enabled = false);
      ALTER TABLE wh_active_streams SET (autovacuum_enabled = false);
      TRUNCATE wh_outbox, wh_inbox, wh_inbox_state, wh_active_streams;
      DELETE FROM wh_service_instances;

      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@claimer, 'perf', 'perf-host', 1, NOW() + INTERVAL '1 hour', NOW(), jsonb_build_object());

      CREATE TEMP TABLE IF NOT EXISTS perf_ledger_streams (n int, stream_id uuid);
      TRUNCATE perf_ledger_streams;
      INSERT INTO perf_ledger_streams SELECT n, gen_random_uuid() FROM generate_series(1, @streams) n;

      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      SELECT s.stream_id, compute_partition(s.stream_id, 10000),
             CASE WHEN s.n % 3 = 0 THEN @claimer ELSE @departed END,
             NOW() + INTERVAL '1 hour', NOW() - INTERVAL '1 minute'
      FROM perf_ledger_streams s
      WHERE s.n % 3 <> 2;

      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-perf', 'Perf.Events.SomethingHappened, Perf', 'MessageEnvelope',
             jsonb_build_object('payload', repeat('x', 200)), jsonb_build_object('hops', jsonb_build_array()),
             jsonb_build_object('t', 'tenant-perf'), s.stream_id, compute_partition(s.stream_id, 10000), true, 1, 0,
             NOW() - INTERVAL '1 hour' + (s.n * INTERVAL '1 millisecond')
      FROM perf_ledger_streams s
      WHERE @outbox;

      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        SELECT gen_random_uuid(), 'PerfHandler', 'Perf.Commands.DoSomething, Perf', jsonb_build_object(), jsonb_build_object(),
               NOW() - INTERVAL '1 hour' + (s.n * INTERVAL '1 millisecond'), s.stream_id
        FROM perf_ledger_streams s
        WHERE @inbox
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT m.message_id, m.stream_id, m.received_at, m.priority, m.is_event, 1, 0, compute_partition(m.stream_id, 10000)
      FROM m;

      ANALYZE wh_outbox;
      ANALYZE wh_inbox_state;
      ANALYZE wh_active_streams;
      """;
    cmd.Parameters.AddWithValue("claimer", _claimer);
    cmd.Parameters.AddWithValue("departed", _departed);
    cmd.Parameters.AddWithValue("streams", STREAMS);
    cmd.Parameters.AddWithValue("outbox", scenario.Outbox);
    cmd.Parameters.AddWithValue("inbox", scenario.Inbox);
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }
}
