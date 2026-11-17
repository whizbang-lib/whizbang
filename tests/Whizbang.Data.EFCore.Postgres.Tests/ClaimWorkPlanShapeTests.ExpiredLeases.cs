// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>
/// #2345, the remaining part: the held rows whose lease ran out. The re-offer walk reads the rows a poller holds and
/// passes over those whose lease ran out, which acquisition re-leases a batch at a time and not at all while the
/// poller's working set is full; until then every poll's walk read them again. Measured before the change: with
/// <see cref="EXPIRED_ROWS"/> such rows held, one poll's walk removed thousands of entries by its lease filter, and the
/// next poll removed them again.
/// </para>
/// <para>
/// The claim now renews a held row whose lease ran out while the poller still owns its stream (acquisition's rule, no
/// stealing), reading them through <c>idx_inbox_state_lease_order</c>, which is keyed on the lease's expiry so a poll
/// reaches only the leases that ran out. A renewed row has a live lease, so the walk lands on it and offers it rather
/// than passing over it, and no poll after the renewal reads it as an expired lease.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop#an-owner-renews-the-leases-it-let-run-out</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/475_HeldLeasesAreRenewedByTheirOwner.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ConcurrentIndexBuild.cs</code-under-test>
public partial class ClaimWorkPlanShapeTests {
  private const string LEASE_ORDER = "idx_inbox_state_lease_order";
  /// <summary>Held rows whose lease ran out, one per stream, all on a partition the poller's rank owns.</summary>
  private const int EXPIRED_ROWS = 4_000;
  /// <summary>The first poll's batch: its renewal window (eight batches) covers every expired row.</summary>
  private const int RENEWING_BATCH = 500;

  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_HeldRowsWhoseLeaseRanOut_AreRenewed_AndTheNextPollDoesNotPassOverThemAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openPlanConnectionAsync(dbContext, cancellationToken);
    var poller = TrackedGuid.New().Value;
    await _heartbeatAsync(connection, poller);
    await _fillHeldInboxAsync(connection, poller, live: true);
    await _fillHeldInboxRowsAsync(connection, poller, EXPIRED_ROWS);
    await _vacuumQueuesAsync(connection);

    var first = await _explainClaimAsync(connection, poller, RENEWING_BATCH);
    var stillExpired = await _expiredHeldAsync(connection, poller);
    await _vacuumQueuesAsync(connection);
    var second = await _explainClaimAsync(connection, poller, BATCH);

    var removed = _walkRemovedByFilter(second);
    Console.WriteLine($"#2345 walk entries passed over: first poll {_walkRemovedByFilter(first)}, second poll {removed}; "
      + $"held leases still expired after the first poll: {stillExpired}");
    foreach (var plan in first.Where(p => p.Contains($"using {LEASE_ORDER}", StringComparison.Ordinal))) {
      Console.WriteLine(plan);
    }
    await Assert.That(stillExpired).IsEqualTo(0L)
      .Because($"the poller owns every one of the streams, so the first poll renews all {EXPIRED_ROWS} leases (its window is eight batches)");
    await Assert.That(removed).IsEqualTo(0L)
      .Because($"no held row's lease has run out, so the next poll's walk passes over none; it passed over {removed} "
        + $"(first poll: {_walkRemovedByFilter(first)})");
  }

  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_RenewingExpiredLeases_ReadsThemThroughTheLeaseOrderIndexAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openPlanConnectionAsync(dbContext, cancellationToken);
    var poller = TrackedGuid.New().Value;
    await _heartbeatAsync(connection, poller);
    await _fillHeldInboxAsync(connection, poller, live: true);
    await _fillHeldInboxRowsAsync(connection, poller, EXPIRED_ROWS);
    await _vacuumQueuesAsync(connection);

    var plans = await _explainClaimAsync(connection, poller, BATCH);

    // The renewal: the statement that reads the expired leases and writes them. The fixture's table is small enough
    // that a planner may scan it for other statements (the empty-call probe); this one must reach only the leases
    // that ran out, by index, and write them by row address.
    var renewal = plans.SingleOrDefault(p => p.Contains($"using {LEASE_ORDER}", StringComparison.Ordinal)
                                             && p.Contains("Update on wh_inbox_state", StringComparison.Ordinal));
    await Assert.That(renewal).IsNotNull()
      .Because("the leases that ran out are reached by the index keyed on lease expiry, not by reading every held row");
    await Assert.That(renewal).DoesNotContain("Seq Scan on wh_inbox_state")
      .Because($"the renewal reads and writes only the rows its window found: {renewal}");
    await Assert.That(renewal).Contains("Tid Scan on wh_inbox_state")
      .Because("the lock and the write reach the window's rows by their address");
  }

  private static async Task<NpgsqlConnection> _openPlanConnectionAsync(DbContext dbContext, CancellationToken cancellationToken) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    // As the maintenance step builds them after the migrations, before the first poll.
    foreach (var index in ConcurrentIndexBuild.FrameworkIndexes.Where(i => i.Table == "wh_inbox_state")) {
      await ConcurrentIndexBuild.EnsureAsync(connection, "public", index, 300, cancellationToken: cancellationToken);
    }
    return connection;
  }

  private static async Task _vacuumQueuesAsync(NpgsqlConnection connection) {
    await using var vacuum = connection.CreateCommand();
    vacuum.CommandText = "VACUUM (ANALYZE) wh_inbox_state, wh_inbox, wh_active_streams";
    vacuum.CommandTimeout = 300;
    await vacuum.ExecuteNonQueryAsync();
  }

  /// <summary>Held commands whose lease ran out a minute ago, one per stream, on partition 0, charged one attempt.</summary>
  private static async Task _fillHeldInboxRowsAsync(NpgsqlConnection connection, Guid holder, int rows) {
    await using var fill = connection.CreateCommand();
    fill.CommandText = @"
      WITH inserted AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'TestHandler', 'TestCommand', jsonb_build_object('pad', repeat('x', 200)), '{}',
               NOW() - INTERVAL '1 hour' + (g * INTERVAL '1 microsecond'), gen_random_uuid(), FALSE, 150
        FROM generate_series(1, @rows) g
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT i.message_id, i.stream_id, i.received_at, 150, FALSE, 0, 1, 0, @holder, NOW() - INTERVAL '1 minute'
      FROM inserted i";
    fill.Parameters.AddWithValue(nameof(rows), rows);
    fill.Parameters.AddWithValue(nameof(holder), holder);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  private static async Task<long> _expiredHeldAsync(NpgsqlConnection connection, Guid holder) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_inbox_state WHERE instance_id = @holder AND processed_at IS NULL AND lease_expiry < NOW()";
    cmd.Parameters.AddWithValue(nameof(holder), holder);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>One claim (inbox acquisition off: the poller's working set is full), with every statement's plan.</summary>
  private static async Task<List<string>> _explainClaimAsync(NpgsqlConnection connection, Guid poller, int batch) {
    var plans = new List<string>();
    void Capture(object? _, NpgsqlNoticeEventArgs e) => plans.Add(e.Notice.MessageText);
    await using (var on = connection.CreateCommand()) {
      on.CommandText = "LOAD 'auto_explain'; SET auto_explain.log_min_duration = 0; SET auto_explain.log_nested_statements = on; "
        + "SET auto_explain.log_analyze = on; SET auto_explain.log_timing = off; SET auto_explain.log_buffers = on; SET client_min_messages = log";
      await on.ExecuteNonQueryAsync();
    }
    connection.Notice += Capture;
    try {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @id, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => @batch, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => @batch,
          p_acquire_inbox => FALSE)";
      cmd.Parameters.AddWithValue("id", poller);
      cmd.Parameters.AddWithValue(nameof(batch), batch);
      cmd.CommandTimeout = 300;
      await cmd.ExecuteScalarAsync();
    } finally {
      connection.Notice -= Capture;
      await using var off = connection.CreateCommand();
      off.CommandText = "SET client_min_messages = notice; SET auto_explain.log_min_duration = -1";
      await off.ExecuteNonQueryAsync();
    }
    return plans;
  }

  /// <summary>Entries the poll's held-lanes walks read and threw away (each one a held row they passed over).</summary>
  private static long _walkRemovedByFilter(List<string> plans) =>
    plans.Where(p => p.Contains("Recursive Union", StringComparison.Ordinal)
                     && p.Contains("using idx_inbox_state_held_lanes", StringComparison.Ordinal))
      .Sum(_removedFromInboxStateScans);

  /// <summary>The filter removals of the plan's scans of <c>wh_inbox_state</c> only (a node's detail lines follow it,
  /// up to the next node).</summary>
  private static long _removedFromInboxStateScans(string plan) {
    long removed = 0;
    var inScan = false;
    foreach (var line in plan.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("->", StringComparison.Ordinal) || trimmed.Contains("(cost=", StringComparison.Ordinal)) {
        inScan = trimmed.Contains(" on wh_inbox_state", StringComparison.Ordinal);
        continue;
      }
      var match = _removedByFilter().Match(trimmed);
      if (inScan && match.Success) {
        removed += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
      }
    }
    return removed;
  }

  [GeneratedRegex(@"Rows Removed by Filter: (\d+)")]
  private static partial Regex _removedByFilter();
}
