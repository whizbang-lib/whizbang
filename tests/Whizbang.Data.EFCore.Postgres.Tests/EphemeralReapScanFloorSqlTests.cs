// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Performance;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The ephemeral reap's candidate scans start at the oldest ephemeral event that still has a body (migration 597, #2574).
/// </summary>
/// <remarks>
/// The reap deletes a body and keeps its pointer, and the pointer prune is opt-in, so the ephemeral index fills with
/// pointers that no longer have a body. Both scans walked every one of them on every maintenance cycle. The floor is an
/// instant below which no pointer has a body; each scan reads only what lies above it.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#the-reap-scans-start-at-the-oldest-body</docs>
[Category("Shard3")]
[Category("Integration")]
[Category("Performance")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
public class EphemeralReapScanFloorSqlTests : EFCoreTestBase {
  /// <summary>Pointers whose bodies the reap already removed: the retained set the scans used to walk every cycle.</summary>
  private const int REAPED_POINTERS = 4_000;
  private const string EVENT_TYPE = "Whizbang.Tests.ScanFloor";
  private const string FLOOR_IS_AT = "SELECT wh_ephemeral_reap_scan_floor() = (SELECT created_at FROM wh_event_store WHERE event_id = @id)";

  [Test]
  public async Task Floor_StopsAtTheOldestPointerWithABody_AndNeverPassesItAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _pointerAsync(conn, Guid.NewGuid(), daysAgo: 10, withBody: false, cancellationToken);
    var live = Guid.NewGuid();
    await _pointerAsync(conn, live, daysAgo: 5, withBody: true, cancellationToken);
    await _pointerAsync(conn, Guid.NewGuid(), daysAgo: 2, withBody: false, cancellationToken);

    await Assert.That(await _floorIsAtAsync(conn, live, cancellationToken)).IsTrue()
      .Because("the walk passes the pointer whose body is gone and stops at the first one whose body is stored");
    await Assert.That(await _floorIsAtAsync(conn, live, cancellationToken)).IsTrue()
      .Because("while that body is stored the floor stays on it, so the scans still read it");
  }

  [Test]
  public async Task Floor_WithNoBodyLeft_RestsOnTheNewestPointerItWalkedPastAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _pointerAsync(conn, Guid.NewGuid(), daysAgo: 10, withBody: false, cancellationToken);
    var newest = Guid.NewGuid();
    await _pointerAsync(conn, newest, daysAgo: 2, withBody: false, cancellationToken);

    await Assert.That(await _floorIsAtAsync(conn, newest, cancellationToken)).IsTrue()
      .Because("every pointer it walked had lost its body, so the floor moves to the newest of them and no further");
  }

  [Test]
  public async Task Floor_NeverWalksPointersInsideTheMarginAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var older = Guid.NewGuid();
    await _pointerAsync(conn, older, daysAgo: 1, withBody: false, cancellationToken);
    // Thirty minutes old, inside the default one-hour margin: a transaction that commits late can still store an event
    // with a created_at this old, so the floor never moves past this range.
    var recent = Guid.NewGuid();
    await _pointerAsync(conn, recent, daysAgo: 0, withBody: false, cancellationToken);
    await _execAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '30 minutes' WHERE event_id = @id", recent, cancellationToken);

    await Assert.That(await _floorIsAtAsync(conn, older, cancellationToken)).IsTrue()
      .Because("the walk reads only pointers older than the margin");
  }

  [Test]
  public async Task Floor_WithNothingOlderThanTheMargin_StaysAtTheBeginningAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _pointerAsync(conn, Guid.NewGuid(), daysAgo: 0, withBody: false, cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_ephemeral_reap_scan_floor() = '-infinity'::timestamptz";
    await Assert.That((bool)(await cmd.ExecuteScalarAsync(cancellationToken))!).IsTrue()
      .Because("a pointer inside the margin is never walked, so the floor stays at the beginning");
  }

  [Test]
  public async Task Floor_AfterTheRescanInterval_StartsFromTheBeginningAgainAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var reaped = Guid.NewGuid();
    await _pointerAsync(conn, reaped, daysAgo: 2, withBody: false, cancellationToken);
    await Assert.That(await _floorIsAtAsync(conn, reaped, cancellationToken)).IsTrue();

    // A body that reached the store below the floor by another path (a restore keeps an archived created_at).
    var restored = Guid.NewGuid();
    await _pointerAsync(conn, restored, daysAgo: 20, withBody: true, cancellationToken);
    await Assert.That(await _floorIsAtAsync(conn, reaped, cancellationToken)).IsTrue()
      .Because("inside the interval the walk goes on from the stored floor");

    await _execAsync(conn,
      "UPDATE wh_settings SET setting_value = (NOW() - INTERVAL '2 days')::text WHERE setting_key = 'ephemeral_reap_scan_floor_rescanned_at'",
      null, cancellationToken);
    await Assert.That(await _floorIsAtAsync(conn, restored, cancellationToken)).IsTrue()
      .Because("past the rescan interval the walk starts from the beginning, and finds the body below the old floor");
  }

  // Both statistics states the planner can meet: freshly analyzed, and as a bulk insert leaves them before autovacuum
  // has analyzed the table. The floor has to bound the scan in both, so it cannot rest on the planner guessing well.
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  [Timeout(600000)]
  public async Task Scans_ACycleAfterTheFirst_ReadOnlyThePointersAboveTheFloorAsync(bool analyzed, CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, created_at, flags)
      SELECT x.id, x.id, x.id, 'TestAggregate', '{EVENT_TYPE}', 1, x.g, NOW() - INTERVAL '30 days' + x.g * INTERVAL '1 second', 8
      FROM (SELECT gen_random_uuid() AS id, g FROM generate_series(1, {REAPED_POINTERS}) g) x;
      """, null, cancellationToken);
    if (analyzed) {
      await _execAsync(conn, "ANALYZE wh_event_store; ANALYZE wh_event_body;", null, cancellationToken);
    }
    // The one body still stored, consumed and past its grace: the reap's only candidate.
    var live = Guid.NewGuid();
    await _pointerAsync(conn, live, daysAgo: 1, withBody: true, cancellationToken);

    var (firstRead, _) = await _cycleAsync(conn, cancellationToken);
    var (secondRead, secondOffered) = await _cycleAsync(conn, cancellationToken);
    Console.WriteLine($"first cycle: {firstRead}; second cycle: {secondRead}");

    await Assert.That(secondOffered).IsEquivalentTo((List<Guid>)[live])
      .Because("the floor rests on the body still stored, so the scan still offers it");
    // The event store and its indexes only: what the floor bounds. The settings lookups each statement makes (the grace
    // window, the floor's own keys) read wh_settings, whose size is the number of settings and not the retained set, and
    // whose plan a small table leaves to the planner's choice of a sequential scan.
    await Assert.That(_eventStoreRows(firstRead)).IsGreaterThanOrEqualTo(REAPED_POINTERS)
      .Because($"the first cycle walks the reaped pointers once to place the floor. {firstRead}");
    await Assert.That(_eventStoreRows(secondRead)).IsLessThan(REAPED_POINTERS / 10)
      .Because($"a cycle after the first reads the pointers above the floor, not the {REAPED_POINTERS} whose bodies are "
        + $"gone. {secondRead}");
  }

  /// <summary>The rows read from <c>wh_event_store</c> and its indexes.</summary>
  private static long _eventStoreRows(RowsReadResult read) =>
    read.ByRelation
      .Where(entry => entry.Relation.StartsWith("wh_event_store", StringComparison.Ordinal)
        || entry.Relation.StartsWith("idx_event_store", StringComparison.Ordinal))
      .Sum(entry => entry.Rows);

  /// <summary>Both candidate scans, as a maintenance cycle asks them, measured in rows read.</summary>
  private static async Task<(RowsReadResult Read, List<Guid> Offered)> _cycleAsync(
      NpgsqlConnection conn, CancellationToken cancellationToken) {
    var offered = new List<Guid>();
    var read = await RowsRead.MeasureAsync(conn, async () => {
      await using (var pairs = conn.CreateCommand()) {
        _ = await CoordinatorCatalogSql.EphemeralPairsNeedingSnapshotAsync(pairs, "", cancellationToken);
      }
      await using var collect = conn.CreateCommand();
      foreach (var target in await EphemeralDestructionSql.CollectAboutToReapAsync(collect, "", cancellationToken)) {
        offered.Add(target.EventId);
      }
    }, commit: true, cancellationToken);
    return (read, offered);
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext, CancellationToken cancellationToken) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    return conn;
  }

  /// <summary>One ephemeral pointer on a stream of its own, aged, with or without its body.</summary>
  private static async Task _pointerAsync(
      NpgsqlConnection conn, Guid eventId, int daysAgo, bool withBody, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, created_at, flags)
      VALUES (@id, @id, @id, 'TestAggregate', '{EVENT_TYPE}', 1, 1, NOW() - @days * INTERVAL '1 day', 8);
      INSERT INTO wh_event_body (event_id, event_data, metadata) SELECT @id, '{"{}"}'::jsonb, '{"{}"}'::jsonb WHERE @body;
      """;
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("days", daysAgo);
    cmd.Parameters.AddWithValue("body", withBody);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<bool> _floorIsAtAsync(NpgsqlConnection conn, Guid eventId, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = FLOOR_IS_AT;
    cmd.Parameters.AddWithValue("id", eventId);
    return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, Guid? eventId, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (eventId is Guid value) {
      cmd.Parameters.AddWithValue("id", value);
    }
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
