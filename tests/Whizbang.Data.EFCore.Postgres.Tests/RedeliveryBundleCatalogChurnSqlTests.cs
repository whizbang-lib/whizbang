// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The two functions that run once per redelivery bundle write nothing to the system catalog once a connection has
/// called them. Each gathers the bundle's rows in a temporary working table. Created per call and dropped at commit,
/// that table cost a set of system catalog inserts and deletes on every bundle, a new plan for every statement that
/// reads it, and cache invalidations for every other connection to the database. A consumer receiving hundreds of
/// one-event bundles a minute spent most of each call on that churn, not on the bundle. The working table is made
/// once per connection and emptied at commit instead.
/// </summary>
/// <remarks>
/// The catalog counters are read inside the second call's transaction (<c>pg_stat_xact_sys_tables</c>), so no other
/// session can move them. They are read before and after the call and the difference is asserted: that view also
/// includes this connection's counts from earlier transactions that have not been flushed yet (the first call's own
/// table creation among them), so a single read would count the first call too.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/320_RepairWaitsForItsAnswer.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/301_StreamDigestsAreFoundByStream.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/420_RedeliveryReconcilesKeepNoWorkTable.sql</code-under-test>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveryBundleCatalogChurnSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly string[] _originNames = ["origin-svc"];
  private static readonly int[] _firstBundle = [1];

  private const string RECORD_ANSWER =
    "SELECT count(*) FROM wh_integrity_record_bundle_answer($1, $2, $3, $4)";
  private const string RECONCILE =
    "SELECT count(*) FROM wh_reconcile_redelivered_bundle_by_first_hop($1, $2, $3, $4)";
  private const string RECONCILE_BATCH =
    "SELECT count(*) FROM wh_reconcile_redelivered_bundles($1, $2, $3, $4, $5)";
  private const string ADOPT =
    "SELECT count(*) FROM wh_adopt_redelivered_origin($1, $2, $3)";

  [Test]
  public async Task RecordBundleAnswer_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync() {
    await using var conn = await _openAsync();
    await _recordAnswerAsync(conn, transaction: null);

    await using var tx = await conn.BeginTransactionAsync();
    var before = await _catalogWritesAsync(conn, tx);
    await _recordAnswerAsync(conn, tx);
    var writes = await _catalogWritesAsync(conn, tx) - before;
    await tx.CommitAsync();

    await Assert.That(writes).IsEqualTo(0L)
      .Because("the working table is made once per connection; making and dropping it per bundle was the churn");
  }

  /// <summary>
  /// A pooled connection is reset (<c>DISCARD ALL</c>) each time the pool hands it out, which drops every temporary
  /// table the session made. A working table kept per connection was therefore made again on almost every bundle in a
  /// deployed consumer: catalog writes, an invalidation for every other session, and new statement entries each time.
  /// </summary>
  [Test]
  public async Task RecordBundleAnswer_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync() {
    await using var conn = await _openAsync();
    await _recordAnswerAsync(conn, transaction: null);
    await using (var reset = new NpgsqlCommand("DISCARD ALL", conn)) {
      await reset.ExecuteNonQueryAsync();
    }

    await using var tx = await conn.BeginTransactionAsync();
    var before = await _catalogWritesAsync(conn, tx);
    await _recordAnswerAsync(conn, tx);
    var writes = await _catalogWritesAsync(conn, tx) - before;
    await tx.CommitAsync();

    await Assert.That(writes).IsEqualTo(0L)
      .Because("the pool resets a connection each time it hands it out, so anything kept per connection is made per call");
  }

  [Test]
  public async Task ReconcileBundle_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync() {
    await using var conn = await _openAsync();
    await _reconcileAsync(conn, transaction: null);

    await using var tx = await conn.BeginTransactionAsync();
    var before = await _catalogWritesAsync(conn, tx);
    await _reconcileAsync(conn, tx);
    var writes = await _catalogWritesAsync(conn, tx) - before;
    await tx.CommitAsync();

    await Assert.That(writes).IsEqualTo(0L)
      .Because("the working table is made once per connection; making and dropping it per bundle was the churn");
  }

  /// <summary>
  /// #2181: the three reconcile functions that run per bundle or per batch kept their working rows in a temporary
  /// table too, and every pooled connection is reset before it is handed out, so the table was made again on almost
  /// every call. They gather their rows into arrays instead, as the bundle answer does (#2179).
  /// </summary>
  [Test]
  public async Task ReconcileBundle_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync() {
    var writes = await _catalogWritesOfACallAfterAResetAsync(async (conn, tx) => await _reconcileAsync(conn, tx));

    await Assert.That(writes).IsEqualTo(0L)
      .Because("the pool resets a connection each time it hands it out, so anything kept per connection is made per call");
  }

  [Test]
  public async Task ReconcileBatch_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync() {
    var writes = await _catalogWritesOfACallAfterAResetAsync(async (conn, tx) => await _reconcileBatchAsync(conn, tx));

    await Assert.That(writes).IsEqualTo(0L)
      .Because("the pool resets a connection each time it hands it out, so anything kept per connection is made per call");
  }

  [Test]
  public async Task AdoptRedeliveredOrigin_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync() {
    var writes = await _catalogWritesOfACallAfterAResetAsync(async (conn, tx) => await _adoptAsync(conn, tx));

    await Assert.That(writes).IsEqualTo(0L)
      .Because("a working table created per call writes its definition to the catalog and drops it again at commit");
  }

  [Test]
  public async Task RecordBundleAnswer_TwiceInOneTransaction_AnswersEachBundleForItsOwnEventsAsync() {
    await using var conn = await _openAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var first = await _recordAnswerAsync(conn, tx, events: 3);
    var second = await _recordAnswerAsync(conn, tx, events: 1);
    await tx.CommitAsync();

    await Assert.That(first).IsEqualTo(1L).Because("three unknown events share the one bucket with no keys");
    await Assert.That(second).IsEqualTo(1L);
    await Assert.That(await _absentAsync(conn, events: 2)).IsEqualTo(2)
      .Because("a reused working table must start empty: a later bundle counts only its own events");
  }

  [Test]
  public async Task ReconcileBundle_TwiceInOneTransaction_StartsFromAnEmptyWorkingTableAsync() {
    await using var conn = await _openAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var first = await _reconcileAsync(conn, tx);
    var second = await _reconcileAsync(conn, tx);
    await tx.CommitAsync();

    await Assert.That(first).IsEqualTo(0L).Because("no event of the bundle is stored here, so no bucket answers");
    await Assert.That(second).IsEqualTo(0L);
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static Guid[] _ids(int n) => [.. Enumerable.Range(0, n).Select(_ => TrackedGuid.New().Value)];

  private static async Task<long> _recordAnswerAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction, int events = 1) {
    await using var cmd = new NpgsqlCommand(RECORD_ANSWER, conn, transaction);
    cmd.Parameters.Add(new NpgsqlParameter { Value = TrackedGuid.New().Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(events), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = new long?[events], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _absentAsync(NpgsqlConnection conn, int events) {
    await using var cmd = new NpgsqlCommand(
      "SELECT absent_count FROM wh_integrity_record_bundle_answer($1, $2, $3, $4)", conn);
    cmd.Parameters.Add(new NpgsqlParameter { Value = TrackedGuid.New().Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(events), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = new long?[events], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _reconcileAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction) {
    await using var cmd = new NpgsqlCommand(RECONCILE, conn, transaction);
    cmd.Parameters.Add(new NpgsqlParameter { Value = TrackedGuid.New().Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = "origin-svc", NpgsqlDbType = NpgsqlDbType.Text });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(1), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = new long?[] { 1 }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// Calls the function once, resets the session as the pool does, and returns what a second call wrote to the catalog.
  /// </summary>
  private async Task<long> _catalogWritesOfACallAfterAResetAsync(Func<NpgsqlConnection, NpgsqlTransaction?, Task> call) {
    await using var conn = await _openAsync();
    await call(conn, null);
    await using (var reset = new NpgsqlCommand("DISCARD ALL", conn)) {
      await reset.ExecuteNonQueryAsync();
    }

    await using var tx = await conn.BeginTransactionAsync();
    var before = await _catalogWritesAsync(conn, tx);
    await call(conn, tx);
    var writes = await _catalogWritesAsync(conn, tx) - before;
    await tx.CommitAsync();
    return writes;
  }

  private static async Task<long> _reconcileBatchAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction) {
    await using var cmd = new NpgsqlCommand(RECONCILE_BATCH, conn, transaction);
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(1), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _originNames, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _firstBundle, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(1), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = new long?[] { 1 }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _adoptAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction) {
    await using var cmd = new NpgsqlCommand(ADOPT, conn, transaction);
    cmd.Parameters.Add(new NpgsqlParameter { Value = TrackedGuid.New().Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _ids(1), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = new long?[] { 1 }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>Rows this transaction inserted, updated or deleted in the catalog tables a table definition lives in.</summary>
  private static async Task<long> _catalogWritesAsync(NpgsqlConnection conn, NpgsqlTransaction transaction) {
    await using var cmd = new NpgsqlCommand("""
      SELECT COALESCE(SUM(n_tup_ins + n_tup_upd + n_tup_del), 0)::bigint
      FROM pg_stat_xact_sys_tables
      WHERE relname IN ('pg_class', 'pg_attribute', 'pg_type', 'pg_depend', 'pg_index', 'pg_constraint')
      """, conn, transaction);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
