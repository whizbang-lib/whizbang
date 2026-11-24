// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The commit-order stamper's fence counts only the backends that could still write this database's event store
/// (#2562). An autovacuum worker is not one of them: it never inserts an event row, yet while it vacuums or analyzes a
/// table (a shared catalog such as <c>pg_database</c> included, which it does from whichever database it is connected
/// to) it holds a transaction id, and the fence used to stop at that id. Every event committed after the worker took
/// its id then stayed unstamped until the worker finished, so perspectives could not see it and the outbox held it
/// for its stamp.
/// </summary>
/// <remarks>
/// <para>
/// No test can make PostgreSQL run an autovacuum worker on demand, so the worker is stood in for by a real backend
/// holding a real, older transaction id that the stamper's session sees labeled <c>autovacuum worker</c>: a temporary
/// view named <c>pg_stat_activity</c>, which PostgreSQL resolves before the catalog's for that session only. The
/// same view labeling the same backend <c>client backend</c> is the control: the fence must still hold for a backend
/// that can write, so the only thing the two tests vary is the backend's type.
/// </para>
/// <para>
/// Found by <c>CleanupIntegrityMatrixTests</c>, whose harness stamps each write once and reads the sequence back: on a
/// shared test server whose databases are created and dropped all day, autovacuum visited the shared catalog from a
/// test's own database often enough to leave a cell's events unstamped, a different cell each time.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/559_EventsWaitForTheirCommitOrderStamp.sql</code-under-test>
[Category("Shard1")]
public class StampFenceAutovacuumSqlTests : EFCoreTestBase {
  private const string AUTOVACUUM_WORKER = "autovacuum worker";
  private const string CLIENT_BACKEND = "client backend";

  [Test]
  public async Task Stamp_AnAutovacuumWorkerHoldingAnOlderTransactionId_DoesNotHoldBackTheRowsAsync() {
    var (stamped, sequence) = await _stampBehindAnOlderTransactionLabeledAsync(AUTOVACUUM_WORKER);

    await Assert.That(stamped).IsEqualTo(1)
      .Because("an autovacuum worker never writes the event store, so its transaction id cannot hide a row that "
        + "would commit behind the ones stamped now; fencing on it left every newer event unstamped until the "
        + "worker finished");
    await Assert.That(sequence.HasValue).IsTrue();
  }

  [Test]
  public async Task Stamp_AClientBackendHoldingAnOlderTransactionId_StillHoldsBackTheRowsAsync() {
    var (stamped, sequence) = await _stampBehindAnOlderTransactionLabeledAsync(CLIENT_BACKEND);

    await Assert.That(stamped).IsEqualTo(0)
      .Because("a client backend can still insert an event row under its older transaction id, so a row committed "
        + "after it must wait, or the older row would receive the higher sequence");
    await Assert.That(sequence.HasValue).IsFalse();
  }

  /// <summary>
  /// One backend takes a transaction id and stays open; a second appends a row under a newer id and commits; a third
  /// stamps, seeing the first backend labeled <paramref name="backendType"/>. Returns what the stamp numbered and the
  /// row's sequence afterwards.
  /// </summary>
  private async Task<(int Stamped, long? Sequence)> _stampBehindAnOlderTransactionLabeledAsync(string backendType) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var holder = await dataSource.OpenConnectionAsync();
    await using var writer = await dataSource.OpenConnectionAsync();
    await using var stamper = await dataSource.OpenConnectionAsync();
    var eventId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();

    await _execAsync(holder, "BEGIN");
    var holderXid = await _xidAsync(holder);
    var holderPid = await _scalarAsync(holder, "SELECT pg_backend_pid()");

    await _execAsync(writer, "BEGIN");
    var writerXid = await _xidAsync(writer);
    await _appendAsync(writer, eventId, streamId);
    await _execAsync(writer, "COMMIT");
    await Assert.That(holderXid).IsLessThan(writerXid)
      .Because("precondition: the open transaction is older than the row's, so a fence on it holds the row back");

    // The stamper's session alone sees the holder under the given type; every other column is the catalog's own.
    await _execAsync(stamper, $"""
      CREATE TEMP VIEW pg_stat_activity AS
      SELECT a.datid, a.datname, a.pid, a.backend_xid, a.backend_xmin, a.state, a.query,
             CASE WHEN a.pid = {holderPid.ToString(CultureInfo.InvariantCulture)} THEN '{backendType}'::text
                  ELSE a.backend_type END AS backend_type
      FROM pg_catalog.pg_stat_activity a
      """);
    await Assert.That(await _scalarAsync(stamper,
        $"SELECT count(*) FROM pg_stat_activity WHERE pid = {holderPid.ToString(CultureInfo.InvariantCulture)} "
        + $"AND backend_type = '{backendType}' AND backend_xid IS NOT NULL")).IsEqualTo(1L)
      .Because("precondition: the stamper's session sees the open transaction's backend, holding its id, as the type "
        + "under test");

    var stamped = (int)await _scalarAsync(stamper, "SELECT stamp_pending_commit_sequences(1000)");
    var sequence = await _sequenceAsync(stamper, eventId);
    await _execAsync(holder, "ROLLBACK");
    return (stamped, sequence);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>Assigns the transaction its id now, as an earlier write in it would, and returns it.</summary>
  private static Task<long> _xidAsync(NpgsqlConnection conn) => _scalarAsync(conn, "SELECT pg_current_xact_id()::text::bigint");

  private static async Task _appendAsync(NpgsqlConnection conn, Guid eventId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@eid, @sid, @sid, 'TestAggregate', 1, 'TestEvent', NULL, NOW())
      """;
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long?> _sequenceAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }
}
