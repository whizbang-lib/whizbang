// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What a role holder's vote waits on while the commit-order stamper is held back by an open import transaction
/// (#1217).
/// </summary>
/// <remarks>
/// <para>
/// Captured on a deployed environment under bulk imports: heartbeats, bridge expiry and schedule claims no longer
/// waited, but the holder's role vote waited a median of 14.5 s and up to 30 s, and 95% of those waits led back to
/// the stamper's fenced transaction, which was running, not blocked, for up to 47 s at a time.
/// <c>LivenessUnderLoadScenarioTests</c> names the same blocker and never sees the duration, because its stamper
/// always has eligible rows to stamp.
/// </para>
/// <para>
/// An import does not leave the stamper rows to stamp. Its transactions keep the visibility horizon at the oldest of
/// them, so every row inserted after that one is unstamped and not yet eligible. The stamp walks the unstamped rows
/// in insertion order until it has its batch, and with nothing eligible it never has it: it reads every unstamped
/// row behind the horizon and stamps none. The worker then retries on its fenced interval because rows are pending,
/// and each retry reads them all again under the role fence, holding the role row the holder's vote has to write.
/// </para>
/// <para>
/// This scenario holds one import transaction open, commits a backlog behind it, and runs the stamper's fenced
/// calls beside the holder's votes. The index entries each call reads come from <c>pg_stat_xact_user_indexes</c>
/// inside the call's own transaction, so they are the same on every machine; the vote's waits are sampled from
/// <c>pg_stat_activity</c> and recorded, never gated, because their length depends on the machine.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
[Category("Benchmark")]
[Category("Performance")]
public class RoleVoteBehindAHeldBackStampScenarioTests : EFCoreTestBase {
  private const string STAMPER_ROLE = "commit-stamper";
  private const string WALK_INDEX = "idx_event_store_unstamped_insert_order";
  /// <summary>Streams the import commits behind its open transaction.</summary>
  private const int HELD_BACK_STREAMS = 10_000;
  private const int HELD_BACK_VERSIONS = 10;
  /// <summary>The worker's steady-state batch: a call that stamps nothing never grows it.</summary>
  private const int STAMP_BATCH = 1000;
  /// <summary>Fenced calls the stamper makes; the holder votes until they finish.</summary>
  private const int STAMP_ROUNDS = 20;

  private static readonly Guid _holder = new("aaaaaaaa-0000-0000-0000-0000000000a1");

  [Test]
  [Timeout(1800000)]
  public async Task HolderVote_BesideAStampHeldBackByAnOpenImport_NamesWhatEachStampReads_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await _prepareAsync(dataSource, cancellationToken);
    var epoch = await _grantStamperRoleAsync(dataSource, cancellationToken);

    // The import's oldest transaction: open, with a write, so it holds the horizon for the whole run.
    await using var import = await dataSource.OpenConnectionAsync(cancellationToken);
    await _execAsync(import, "BEGIN");
    await _execAsync(import, _insertEventsSql(1, 1));
    // The rest of the import, committed behind it: unstamped and not yet eligible.
    await using (var committing = await dataSource.OpenConnectionAsync(cancellationToken)) {
      await _execAsync(committing, _insertEventsSql(HELD_BACK_STREAMS, HELD_BACK_VERSIONS));
      await _execAsync(committing, "ANALYZE wh_event_store");
    }
    var heldBack = HELD_BACK_STREAMS * HELD_BACK_VERSIONS;

    using var done = new CancellationTokenSource();
    var samples = new ConcurrentBag<VoteWait>();
    var sampler = Task.Run(() => _sampleVoteWaitsAsync(dataSource, samples, done.Token), cancellationToken);
    var stamper = Task.Run(() => _stampAsync(dataSource, epoch, cancellationToken), cancellationToken);
    // The holder renews its role by voting, as DutyHolderWorker does, until the stamper has made its calls.
    var voter = _voteUntilAsync(dataSource, stamper);

    var calls = await stamper;
    var votes = await voter;
    await done.CancelAsync();
    await sampler;
    await _execAsync(import, "ROLLBACK");

    var waits = samples.ToList();
    var longestWaitMs = waits.Count == 0 ? 0 : waits.Max(w => w.WaitedMs);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Role vote beside a stamp held back by an open import");
    report.Measure("role_vote.held_back_stamp.rows_behind_the_horizon", heldBack, "rows");
    report.Measure("role_vote.held_back_stamp.rows_stamped_per_call", calls.Max(c => c.Stamped), "rows");
    report.Measure("role_vote.held_back_stamp.index_entries_read_per_call", calls.Max(c => c.EntriesRead), "entries");
    report.Measure("role_vote.held_back_stamp.vote_lock_wait_samples", waits.Count, "samples");
    report.Measure("role_vote.held_back_stamp.stamp_call_median_ms", _median(calls.Select(c => c.ElapsedMs)), "ms");
    report.Measure("role_vote.held_back_stamp.longest_vote_wait_ms", longestWaitMs, "ms");
    var detail = new StringBuilder();
    foreach (var group in waits.GroupBy(w => $"{w.WaitEvent} behind [{w.Blocker}]").OrderByDescending(g => g.Count())) {
      detail.Append(CultureInfo.InvariantCulture, $"  {group.Count(),6} x {group.Key}\n");
    }
    var rendered = report.Render()
      + "\nVote waits by blocking statement:\n" + detail
      + string.Create(CultureInfo.InvariantCulture,
        $"\nStamp calls: {calls.Count}, median {_median(calls.Select(c => c.ElapsedMs))} ms, longest "
        + $"{calls.Max(c => c.ElapsedMs)} ms. Votes: {votes}, longest wait seen {longestWaitMs} ms.\n");
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("role-vote-held-back-stamp", rendered, report.RenderBaselineLines());

    await Assert.That(calls.Count).IsEqualTo(STAMP_ROUNDS)
      .Because("the stamper has to have made every fenced call, or the scenario measured nothing");
    await Assert.That(calls.Max(c => c.Stamped)).IsEqualTo(0)
      .Because("the open import transaction holds the horizon, so no row behind it may be stamped; a stamp here "
        + "would mean the scenario no longer holds the stamper back");
    await Assert.That(report.Breaches).IsEmpty();
  }

  private sealed record StampCall(int Stamped, long EntriesRead, long ElapsedMs);

  private sealed record VoteWait(string? WaitEvent, string Blocker, long WaitedMs);

  /// <summary>
  /// The stamper's fenced retry as the worker makes it while rows are pending: the epoch check, the stamp and a read of
  /// what the stamp's walk read, in one transaction. Back to back rather than on the worker's interval, so the count of
  /// calls, not the clock, bounds the run.
  /// </summary>
  private static async Task<List<StampCall>> _stampAsync(NpgsqlDataSource dataSource, long epoch, CancellationToken cancellationToken) {
    var calls = new List<StampCall>(STAMP_ROUNDS);
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    for (var i = 0; i < STAMP_ROUNDS; i++) {
      await using var tx = await conn.BeginTransactionAsync(cancellationToken);
      await _execAsync(conn, $"SELECT wh_assert_role_epoch('{STAMPER_ROLE}', '{_holder}'::uuid, {epoch})");
      // The counter holds every read this backend has not yet flushed, earlier transactions included, so a call's
      // reads are the difference across it.
      var before = await _scalarAsync(conn, _walkReadsSql());
      var clock = Stopwatch.StartNew();
      var stamped = await _scalarAsync(conn, $"SELECT stamp_pending_commit_sequences({STAMP_BATCH}, false)");
      clock.Stop();
      var read = await _scalarAsync(conn, _walkReadsSql()) - before;
      await tx.CommitAsync(cancellationToken);
      calls.Add(new StampCall((int)stamped, read, clock.ElapsedMilliseconds));
    }
    return calls;
  }

  private static string _walkReadsSql() => $"SELECT pg_stat_get_xact_tuples_returned('{WALK_INDEX}'::regclass)";

  private static async Task<int> _voteUntilAsync(NpgsqlDataSource dataSource, Task until) {
    await using var conn = await dataSource.OpenConnectionAsync();
    var votes = 0;
    while (!until.IsCompleted) {
      await _execAsync(conn, _voteSql());
      votes++;
    }
    return votes;
  }

  /// <summary>
  /// Samples the holder's vote whenever it is waiting on a lock, with what blocks it and how long it has waited, until
  /// the work is done. A sample is a reading, not a wait: the loop exits when the stamper does.
  /// </summary>
  private static async Task _sampleVoteWaitsAsync(NpgsqlDataSource dataSource, ConcurrentBag<VoteWait> samples, CancellationToken done) {
    await using var conn = await dataSource.OpenConnectionAsync(CancellationToken.None);
    while (!done.IsCancellationRequested) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"
        SELECT a.wait_event,
               COALESCE((SELECT string_agg(left(regexp_replace(b.query, '\s+', ' ', 'g'), 90), ' | ')
                         FROM pg_stat_activity b WHERE b.pid = ANY(pg_blocking_pids(a.pid))), ''),
               (extract(epoch FROM clock_timestamp() - a.query_start) * 1000)::bigint
        FROM pg_stat_activity a
        WHERE a.datname = current_database()
          AND a.pid <> pg_backend_pid()
          AND a.state = 'active'
          AND a.wait_event_type = 'Lock'
          AND a.query LIKE '%wh_vote_role%'";
      await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
      while (await reader.ReadAsync(CancellationToken.None)) {
        samples.Add(new VoteWait(
          await reader.IsDBNullAsync(0, CancellationToken.None) ? null : reader.GetString(0),
          reader.GetString(1),
          reader.GetInt64(2)));
      }
    }
  }

  private static async Task _prepareAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    await _execAsync(conn, $"""
      ALTER TABLE wh_event_store SET (autovacuum_enabled = false);
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{_holder}', 'perf', 'perf-host', 1, NOW(), NOW(), jsonb_build_object());
      """);
  }

  private static string _insertEventsSql(int streams, int versions) => $"""
    INSERT INTO wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
    SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Perf', v, 'Perf.Events.Imported', NULL, NOW()
    FROM (SELECT gen_random_uuid() AS stream_id FROM generate_series(1, {streams})) s
    CROSS JOIN generate_series(1, {versions}) v
    """;

  private static string _voteSql() =>
    $"SELECT * FROM wh_vote_role('{STAMPER_ROLE}', '{_holder}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)";

  private static async Task<long> _grantStamperRoleAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
    return await _scalarAsync(conn, $"SELECT epoch FROM ({_voteSql()}) v");
  }

  private static long _median(IEnumerable<long> values) {
    var sorted = values.Order().ToList();
    return sorted[sorted.Count / 2];
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
