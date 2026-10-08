// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the liveness calls wait on when they run beside the bulk paths (#1217): claims, heartbeats, role votes, the
/// lapsed-bridge expiry, due-schedule claims and retry notifications all at once, with the commit-order stamper
/// draining a backlog under its role fence.
/// </summary>
/// <remarks>
/// <para>
/// The liveness calls cost a few dozen blocks a round, yet they were reported at 8 to 16 seconds on average, so their
/// time was spent waiting. This scenario runs each of them on a connection of its own, concurrently, and samples
/// <c>pg_stat_activity</c> with <c>pg_blocking_pids()</c> from another connection for as long as the work runs, so
/// every wait it catches names the statement that held it up. The test database also has <c>log_lock_waits</c> on
/// and a 10 ms <c>deadlock_timeout</c>, so the server log carries the same waits for a person reading it.
/// </para>
/// <para>
/// Every worker runs a fixed number of rounds; nothing waits on time. The sample counts depend on the machine and
/// are recorded, never gated: the finding is WHICH statement blocks each call, and that is printed in full.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Benchmark")]
[Category("Performance")]
public class LivenessUnderLoadScenarioTests : EFCoreTestBase {
  private const string STAMPER_ROLE = "commit-stamper";
  /// <summary>Unstamped events the stamper drains while everything else runs.</summary>
  private const int BACKLOG_STREAMS = 20_000;
  private const int BACKLOG_VERSIONS = 10;
  private const int STAMP_BATCH = 1000;
  /// <summary>Fenced stamps the stamper runs; the other workers run until it finishes.</summary>
  private const int STAMP_ROUNDS = 40;

  private static readonly Guid _holder = new("aaaaaaaa-0000-0000-0000-0000000000a1");
  private static readonly Guid _peer = new("bbbbbbbb-0000-0000-0000-0000000000b2");
  private static readonly Guid _third = new("cccccccc-0000-0000-0000-0000000000c3");

  /// <summary>The calls whose waits this scenario names, keyed by a fragment of their statement text.</summary>
  private static readonly string[] LIVENESS_CALLS =
    ["record_heartbeat", "wh_vote_role", "wh_end_lapsed_bridge", "wh_claim_due_schedules", "notify_scheduled_retry_due"];

  [Test]
  [Timeout(1800000)]
  public async Task LivenessCalls_BesideClaimsAndAFencedStamper_NameWhatTheyWaitOn_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await _prepareAsync(dataSource, cancellationToken);
    // The lock-wait settings apply to sessions opened after them.
    dataSource.Clear();

    var epoch = await _grantStamperRoleAsync(dataSource, cancellationToken);
    using var done = new CancellationTokenSource();
    var samples = new ConcurrentBag<WaitSample>();
    var sampler = Task.Run(() => _sampleAsync(dataSource, samples, done.Token), cancellationToken);

    var stamper = Task.Run(() => _stampAsync(dataSource, epoch, cancellationToken), cancellationToken);
    // The other workers run until the stamper has finished its rounds.
    var others = new[] {
      _loopAsync(dataSource, stamper, conn => _claimAsync(conn, _peer)),
      _loopAsync(dataSource, stamper, conn => _claimAsync(conn, _third)),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT record_heartbeat('{_holder}'::uuid, 'perf', 'perf-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', 150)")),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT record_heartbeat('{_peer}'::uuid, 'perf', 'perf-host', 2, '{{}}'::jsonb, 'Running', '1.0.0', 150)")),
      // The holder renews its own role by voting, as DutyHolderWorker does.
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT * FROM wh_vote_role('{STAMPER_ROLE}', '{_holder}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)")),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT * FROM wh_vote_role('{STAMPER_ROLE}', '{_peer}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)")),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT wh_end_lapsed_bridge('{STAMPER_ROLE}', NULL)")),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn,
        $"SELECT * FROM wh_claim_due_schedules('{_holder}'::uuid, NOW() + INTERVAL '30 seconds', 10000, 100)")),
      _loopAsync(dataSource, stamper, conn => _execAsync(conn, "SELECT * FROM notify_scheduled_retry_due()")),
    };
    var stampsDone = await stamper;
    var rounds = await Task.WhenAll(others);
    await done.CancelAsync();
    await sampler;

    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Liveness calls under concurrent load");
    var detail = new StringBuilder();
    foreach (var call in LIVENESS_CALLS) {
      var seen = samples.Where(s => s.Query.Contains(call, StringComparison.Ordinal)).ToList();
      var waiting = seen.Where(s => s.WaitEventType == "Lock").ToList();
      report.Measure($"liveness.under_load.{call}.lock_wait_samples", waiting.Count, "samples");
      foreach (var group in waiting.GroupBy(w => $"{w.WaitEvent} behind [{w.Blocker}]").OrderByDescending(g => g.Count())) {
        detail.Append(CultureInfo.InvariantCulture, $"  {call,-28} {group.Count(),6} x {group.Key}\n");
      }
    }
    report.Measure("liveness.under_load.stamps", stampsDone, "stamps");
    var rendered = report.Render() + "\nWaits by blocking statement:\n" + detail;
    Console.WriteLine(rendered);
    Console.WriteLine("Rounds per worker: " + string.Join(", ", rounds));
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("liveness-under-load", rendered, report.RenderBaselineLines());

    await Assert.That(stampsDone).IsEqualTo(STAMP_ROUNDS)
      .Because("the stamper has to have run every round under its fence, or the scenario measured nothing");
    await Assert.That(samples.Count).IsGreaterThan(0)
      .Because("the sampler has to have seen the workers, or no wait could have been named");
    await Assert.That(report.Breaches).IsEmpty();
  }

  private sealed record WaitSample(string Query, string? WaitEventType, string? WaitEvent, string Blocker);

  /// <summary>
  /// Samples every active statement of this database except its own, with each blocking backend's statement, until
  /// the work is done. A sample is a reading, not a wait: the loop exits when the workers do.
  /// </summary>
  private static async Task _sampleAsync(NpgsqlDataSource dataSource, ConcurrentBag<WaitSample> samples, CancellationToken done) {
    await using var conn = await dataSource.OpenConnectionAsync(CancellationToken.None);
    while (!done.IsCancellationRequested) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"
        SELECT a.query, a.wait_event_type, a.wait_event,
               COALESCE((SELECT string_agg(left(regexp_replace(b.query, '\s+', ' ', 'g'), 90), ' | ')
                         FROM pg_stat_activity b WHERE b.pid = ANY(pg_blocking_pids(a.pid))), '')
        FROM pg_stat_activity a
        WHERE a.datname = current_database()
          AND a.pid <> pg_backend_pid()
          AND a.state = 'active'";
      await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
      while (await reader.ReadAsync(CancellationToken.None)) {
        samples.Add(new WaitSample(
          reader.GetString(0),
          await reader.IsDBNullAsync(1, CancellationToken.None) ? null : reader.GetString(1),
          await reader.IsDBNullAsync(2, CancellationToken.None) ? null : reader.GetString(2),
          reader.GetString(3)));
      }
    }
  }

  /// <summary>The stamper as a role holder runs it: the epoch check and the stamp in one transaction.</summary>
  private static async Task<int> _stampAsync(NpgsqlDataSource dataSource, long epoch, CancellationToken cancellationToken) {
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    for (var i = 0; i < STAMP_ROUNDS; i++) {
      await using var tx = await conn.BeginTransactionAsync(cancellationToken);
      await _execAsync(conn, $"SELECT wh_assert_role_epoch('{STAMPER_ROLE}', '{_holder}'::uuid, {epoch})");
      await _execAsync(conn, $"SELECT stamp_pending_commit_sequences({STAMP_BATCH})");
      await tx.CommitAsync(cancellationToken);
    }
    return STAMP_ROUNDS;
  }

  private static async Task<int> _loopAsync(NpgsqlDataSource dataSource, Task until, Func<NpgsqlConnection, Task> round) {
    await using var conn = await dataSource.OpenConnectionAsync();
    var rounds = 0;
    while (!until.IsCompleted) {
      await round(conn);
      rounds++;
    }
    return rounds;
  }

  private static Task _claimAsync(NpgsqlConnection conn, Guid instance) => _execAsync(conn, $@"
    SELECT count(*) FROM claim_work(
      p_instance_id => '{instance}'::uuid, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
      p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 100)");

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Three live instances; a backlog of unstamped events; an outbox backlog the claims can take; the lock-wait log on.
  /// </summary>
  private async Task _prepareAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
    await _execAsync(conn, $"""
      ALTER DATABASE {database} SET log_lock_waits = on;
      ALTER DATABASE {database} SET deadlock_timeout = '10ms';
      ALTER TABLE wh_event_store SET (autovacuum_enabled = false);
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{_holder}', 'perf', 'perf-host', 1, NOW(), NOW(), jsonb_build_object()),
             ('{_peer}', 'perf', 'perf-host', 2, NOW(), NOW(), jsonb_build_object()),
             ('{_third}', 'perf', 'perf-host', 3, NOW(), NOW(), jsonb_build_object());
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Perf', v, 'Perf.Events.Happened', NULL, NOW()
      FROM (SELECT gen_random_uuid() AS stream_id FROM generate_series(1, {BACKLOG_STREAMS})) s
      CROSS JOIN generate_series(1, {BACKLOG_VERSIONS}) v;
      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-perf', 'Perf.Events.Happened, Perf', 'MessageEnvelope',
             jsonb_build_object('payload', repeat('x', 200)), jsonb_build_object('hops', jsonb_build_array()),
             jsonb_build_object('t', 'tenant-perf'), s, compute_partition(s, 10000), true, 1, 0, NOW()
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, 5000)) streams;
      ANALYZE wh_event_store;
      ANALYZE wh_outbox;
      """);
  }

  private static async Task<long> _grantStamperRoleAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT epoch FROM wh_vote_role('{STAMPER_ROLE}', '{_holder}'::uuid, INTERVAL '30 seconds', "
      + "INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
  }

  private static async Task _writeReportAsync(string scenario, string rendered, string baselineLines) {
    var dir = Environment.GetEnvironmentVariable("WHIZBANG_PERF_REPORT_DIR")
      ?? Path.Combine(AppContext.BaseDirectory, "perf-reports");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(
      Path.Combine(dir, $"{scenario}.txt"),
      rendered + "\nBaseline lines for this run:\n" + baselineLines);
  }
}
