// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>
/// #2345: the re-offer of the inbox rows a poller holds, measured on <c>wh_inbox_state</c> itself (the table the walk
/// reads; the tests above measure <c>wh_inbox</c>, which the walk never touches). Beside the rows it returns, the poller
/// holds rows whose lease ran out while its working set was full; the walk passes over them, reading their entries.
/// </para>
/// <para>
/// The walk was written as one index-only probe per stream, its index covering every column it reads. Migration 349 added
/// a test on <c>chain_emitted_at</c>, which the index does not hold, so every entry the walk read (each one it passes over
/// included) cost a heap page: measured here, 3,779 blocks of 8,111 for one poll with 4,000 passed-over rows, and the poll
/// grew with them (8,166 entries examined at 4,000, 12,576 at 8,000). The covering index makes the walk index-only again,
/// so an entry on an all-visible page costs only its share of an index page.
/// </para>
/// <para>
/// What it does not change, measured here after the fix: 3,383 heap fetches at 4,000 passed over. This table is small
/// enough that the same poll's acquisition, which re-leases up to a batch of the expired rows before the walk runs,
/// touches nearly every heap page and clears its visibility bit, so the index-only walk still visits them. On a store
/// whose held rows span many more pages than a batch, the re-lease touches a small share of them. The walk still reads
/// every passed-over entry (an expired lease is a time test no index key can skip); that remaining cost is reported on
/// #2345, not locked here.
/// </para>
/// </summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/ConcurrentIndexBuild.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/390_TheClaimLeasesNoInboxAFullWorkingSetWouldHandBack.sql</code-under-test>
public partial class ClaimWorkPlanShapeTests {
  private const string HELD_LANES_COVERING = "idx_inbox_state_held_lanes_covering";
  /// <summary>Rows the walk returns: live leases, chain run, over 20 streams.</summary>
  private const int LIVE_HELD_STREAMS = 20;
  /// <summary>Rows the walk passes over, per fill: held here, the lease ran out, the chain ran.</summary>
  private const int PASSED_OVER_ROWS = 4_000;
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_ReofferingHeldInbox_WalksTheCoveringIndex_IndexOnlyAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    // As the maintenance step builds it after the migrations, before the first poll.
    var covering = ConcurrentIndexBuild.FrameworkIndexes.SingleOrDefault(i => i.Name == HELD_LANES_COVERING);
    if (covering is not null) {
      await ConcurrentIndexBuild.EnsureAsync(connection, "public", covering, 300, cancellationToken: cancellationToken);
    }
    var poller = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    await _fillHeldInboxAsync(connection, poller, live: true);
    await _fillHeldInboxAsync(connection, poller, live: false);

    var once = await _measureInboxStatePollAsync(connection, poller);
    await _fillHeldInboxAsync(connection, poller, live: false);
    var twice = await _measureInboxStatePollAsync(connection, poller);

    var report = $"{PASSED_OVER_ROWS} passed over: walk {once.WalkBlocks} blocks, {once.WalkHeapFetches} heap fetches, {once.WalkScan}; "
      + $"{2 * PASSED_OVER_ROWS}: walk {twice.WalkBlocks} blocks, {twice.WalkHeapFetches} heap fetches; poll {once.Blocks} and {twice.Blocks} blocks";
    await Assert.That(once.Claimed).IsGreaterThan(0).Because("positive control: the walk returns the live rows");
    await Assert.That(twice.WalkScan).StartsWith($"Index Only Scan using {HELD_LANES_COVERING}")
      .Because($"the walk reads every column from the index it walks, so it never needs the table for a row it passes over ({report})");
  }

  /// <summary>One settled poll: rows claimed, blocks of <c>wh_inbox_state</c> and its indexes, and the held-lanes walk's
  /// scan (from the plan auto_explain logs for the walk's statement) with its heap fetches.</summary>
  private readonly record struct HeldPoll(int Claimed, long Blocks, string WalkScan, long WalkHeapFetches, long WalkBlocks);

  private async Task<HeldPoll> _measureInboxStatePollAsync(NpgsqlConnection connection, Guid poller) {
    await using (var vacuum = connection.CreateCommand()) {
      vacuum.CommandText = "VACUUM (ANALYZE) wh_inbox_state, wh_inbox, wh_outbox, wh_perspective_events, wh_event_store";
      vacuum.CommandTimeout = 300;
      await vacuum.ExecuteNonQueryAsync();
    }
    var before = await _inboxStateBlocksAsync(connection);
    var plans = new List<string>();
    void Capture(object? _, NpgsqlNoticeEventArgs e) => plans.Add(e.Notice.MessageText);
    await using (var on = connection.CreateCommand()) {
      on.CommandText = "LOAD 'auto_explain'; SET auto_explain.log_min_duration = 0; SET auto_explain.log_nested_statements = on; "
        + "SET auto_explain.log_analyze = on; SET auto_explain.log_timing = off; SET auto_explain.log_buffers = on; SET client_min_messages = log";
      await on.ExecuteNonQueryAsync();
    }
    connection.Notice += Capture;
    var claimed = await _claimAsync(connection, poller);
    connection.Notice -= Capture;
    await using (var off = connection.CreateCommand()) {
      off.CommandText = "SET client_min_messages = notice; SET auto_explain.log_min_duration = -1";
      await off.ExecuteNonQueryAsync();
    }
    var after = await _inboxStateBlocksAsync(connection);
    // The walk is the statement that recurses over the held lanes; its probes are the scans of wh_inbox_state in it.
    var walk = plans.First(p => p.Contains("Recursive Union", StringComparison.Ordinal)
      && p.Contains("using idx_inbox_state_held_lanes", StringComparison.Ordinal));
    var scans = walk.Split('\n').Select(l => l.Trim().TrimStart('-', '>', ' ')).Where(l => l.Contains(" on wh_inbox_state", StringComparison.Ordinal)).ToList();
    var fetches = _heapFetches().Matches(walk).Sum(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    var buffers = _sharedHit().Match(walk);
    var walkBlocks = buffers.Success ? long.Parse(buffers.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    return new HeldPoll(claimed, after - before, scans.Count == 0 ? "(no scan)" : scans[0], fetches, walkBlocks);
  }

  [GeneratedRegex(@"Buffers: shared hit=(\d+)")]
  private static partial Regex _sharedHit();

  [GeneratedRegex(@"Heap Fetches: (\d+)")]
  private static partial Regex _heapFetches();

  private async Task<long> _inboxStateBlocksAsync(NpgsqlConnection reporter) {
    await using (var flush = reporter.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = @"
      SELECT t.heap_blks_read + t.heap_blks_hit
             + COALESCE((SELECT SUM(i.idx_blks_read + i.idx_blks_hit) FROM pg_statio_user_indexes i WHERE i.relid = t.relid), 0)
      FROM pg_statio_user_tables t WHERE t.relname = 'wh_inbox_state'";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Inbox events the poller holds, all in one lane (priority 150, retried). Live: 10 rows on each of
  /// <see cref="LIVE_HELD_STREAMS"/> streams, live leases. Passed over: <see cref="PASSED_OVER_ROWS"/> rows, one per
  /// stream, interleaved with the live streams by id, whose lease ran out. All chained (chain_emitted_at set).
  /// </summary>
  private static async Task _fillHeldInboxAsync(NpgsqlConnection connection, Guid holder, bool live) {
    await using var fill = connection.CreateCommand();
    fill.CommandText = @"
      WITH rows AS (
        SELECT st.stream_id, r AS seq, gen_random_uuid() AS message_id
        FROM (SELECT gen_random_uuid() AS stream_id FROM generate_series(1, @streams)) st
        CROSS JOIN generate_series(1, @per_stream) r
      ),
      inserted AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT rows.message_id, 'TestHandler', 'TestEvent', jsonb_build_object('pad', repeat('x', 200)), '{}',
               NOW() - INTERVAL '1 hour' + (rows.seq * INTERVAL '1 microsecond'), rows.stream_id, TRUE, 150
        FROM rows
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id,
         lease_expiry, chain_emitted_at)
      SELECT i.message_id, i.stream_id, i.received_at, 150, TRUE, 0, 1, 0, @holder,
             CASE WHEN @live THEN NOW() + INTERVAL '5 minutes' ELSE NOW() - INTERVAL '1 minute' END,
             NOW() - INTERVAL '1 hour'
      FROM inserted i";
    fill.Parameters.AddWithValue(nameof(holder), holder);
    fill.Parameters.AddWithValue(nameof(live), live);
    fill.Parameters.AddWithValue("streams", live ? LIVE_HELD_STREAMS : PASSED_OVER_ROWS);
    fill.Parameters.AddWithValue("per_stream", live ? 10 : 1);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }
}
