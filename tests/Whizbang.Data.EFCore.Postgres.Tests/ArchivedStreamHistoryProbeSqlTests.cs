// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1933: the two version questions the event store answers from the hot store alone also account for the archive the
/// stream lifecycle ledger records. <c>GetLastSequenceAsync</c> of a stream archived whole is the tombstone's last version,
/// not "no stream"; <c>HasStreamEventsBeforeAsync</c> of a stream whose earlier events are archived (or whose archive was
/// deleted) is true. A stream that was never archived costs exactly the one statement it always did.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/ArchivedStreamHistorySql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
/// <docs>fundamentals/events/archive-store#version-questions</docs>
[Category("Integration")]
[Category("Shard1")]
public class ArchivedStreamHistoryProbeSqlTests : EFCoreTestBase {
  private static readonly Type[] _handled = [typeof(HistoryProbeContracts.HandledEvent)];

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  // Sourced events through the emit chain, versions 1..count, of a type no test perspective folds.
  private static async Task<Guid[]> _streamAsync(NpgsqlConnection connection, Guid streamId, int count) {
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    foreach (var id in ids) {
      var request = $$"""
        {
          "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
          "new_outbox_messages": [{
            "MessageId": "{{id}}", "Destination": "out", "MessageType": "Whizbang.Tests.LedgerEntry", "EnvelopeType": null,
            "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{id}}", "Hops": []},
            "Metadata": null, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
          }]
        }
        """;
      await using var call = connection.CreateCommand();
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }
    return ids;
  }

  private static IEventStore _store(WorkCoordinationDbContext ctx) => new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

  // ── GetLastSequenceAsync ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task LastSequence_OfAStreamArchivedWhole_IsTheTombstonesLastVersion_AndTheSameAfterARestoreAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    var archive = _archive();
    var store = _store(ctx);

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(3L).Because("the control: three hot events");
    await Assert.That((await archive.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(3L)
      .Because("a stream archived whole still holds versions 1 to 3; its tombstone records the last one, so it is not 'no stream'");

    _ = await archive.DeleteAsync(stream);
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(3L)
      .Because("a deleted archive keeps its tombstone, and the versions it held are still taken");
  }

  [Test]
  public async Task LastSequence_OfAStreamArchivedThroughAPrefix_IsItsNewestHotVersion_AndOfNoStreamMinusOneAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 4);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var store = _store(ctx);

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(4L);
    await Assert.That(await store.GetLastSequenceAsync(Guid.CreateVersion7())).IsEqualTo(-1L)
      .Because("a stream that holds nothing, hot or archived, has no last sequence");
  }

  // ── HasStreamEventsBeforeAsync ───────────────────────────────────────────────────────────────────

  [Test]
  public async Task Probe_AStreamArchivedThroughAPrefix_HasHistoryBeforeItsFirstHotEvent_OnBothFormsAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 4);
    var store = _store(ctx);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[2])).IsTrue()
      .Because("versions 1 and 2 are archived, and an archive always takes a stream's lowest versions");
    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[2], _handled)).IsTrue()
      .Because("the ledger records that an archive exists, not its event types, so archived history counts for every perspective");
  }

  [Test]
  public async Task Probe_AStreamNeverArchived_BeforeItsFirstEvent_HasNoHistory_OnBothFormsAsync() {
    // The control for the test above: the same position on a stream with nothing archived.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 2);
    var store = _store(ctx);

    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[0])).IsFalse();
    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[0], _handled)).IsFalse();
    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[1])).IsTrue()
      .Because("the harness sees hot history when there is some");
  }

  [Test]
  public async Task Probe_AStreamArchivedWhole_AndAfterItsArchiveIsDeleted_HasHistory_AndAfterARestoreOnlyItsHotEventsCountAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var whole = Guid.CreateVersion7();
    var deleted = Guid.CreateVersion7();
    var restored = Guid.CreateVersion7();
    _ = await _streamAsync(connection, whole, 2);
    _ = await _streamAsync(connection, deleted, 2);
    var restoredIds = await _streamAsync(connection, restored, 2);
    var archive = _archive();
    foreach (var stream in new[] { whole, deleted, restored }) {
      await Assert.That((await archive.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    }
    _ = await archive.DeleteAsync(deleted);
    await Assert.That((await archive.RestoreAsync(restored)).Status).IsEqualTo(ArchiveStatus.RESTORED);
    var store = _store(ctx);
    var waking = Guid.CreateVersion7();

    await Assert.That(await store.HasStreamEventsBeforeAsync(whole, waking)).IsTrue();
    await Assert.That(await store.HasStreamEventsBeforeAsync(whole, waking, _handled)).IsTrue();
    await Assert.That(await store.HasStreamEventsBeforeAsync(deleted, waking)).IsTrue()
      .Because("the deleted history existed; answering false would fold a woken row from its new events alone");
    await Assert.That(await store.HasStreamEventsBeforeAsync(deleted, waking, _handled)).IsTrue();
    await Assert.That(await store.HasStreamEventsBeforeAsync(restored, restoredIds[0])).IsFalse()
      .Because("a restore puts everything back hot and clears the locator, so the hot store alone answers");
    await Assert.That(await store.HasStreamEventsBeforeAsync(restored, restoredIds[0], _handled)).IsFalse();
  }

  [Test]
  public async Task Questions_InsideTheContextsTransaction_SeeWhatItHasNotCommittedAsync() {
    // The questions run on the context's connection and in its transaction, as the append does.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 1);
    var store = _store(ctx);
    await using var transaction = await ctx.Database.BeginTransactionAsync();
    await using (var archive = connection.CreateCommand()) {
      archive.CommandText = "SELECT archive_status FROM wh_archive_stream(@stream, NULL, 'postgres-table://wh_event_archive?codec=none&format=1')";
      archive.Parameters.AddWithValue("stream", stream);
      await Assert.That(await archive.ExecuteScalarAsync() as string).IsEqualTo(ArchiveStatus.ARCHIVED);
    }

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(1L);
    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[0])).IsTrue()
      .Because("the uncommitted archive is visible, so the question ran in the context's transaction");

    await transaction.RollbackAsync();
    await Assert.That(await store.HasStreamEventsBeforeAsync(stream, ids[0])).IsFalse().Because("the control: rolled back");
  }

  [Test]
  public async Task Questions_InAnAbortedTransaction_Throw_AndTheContextsConnectionStaysUsableAfterTheRollbackAsync() {
    // A statement that fails inside the context's transaction aborts it, so the next question fails inside its own try;
    // the failure reaches the caller and the context's connection is released as it was taken.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 1);
    var store = _store(ctx);
    var transaction = await ctx.Database.BeginTransactionAsync();
    await using (var failing = connection.CreateCommand()) {
      failing.CommandText = "SELECT 1 / 0";
      await Assert.That(async () => await failing.ExecuteScalarAsync()).Throws<PostgresException>();
    }

    var thrown = await Assert.That(async () => await store.GetLastSequenceAsync(stream)).Throws<PostgresException>();

    await Assert.That(thrown!.SqlState).IsEqualTo(PostgresErrorCodes.InFailedSqlTransaction)
      .Because("the question ran in the aborted transaction and its failure is not swallowed");
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Open)
      .Because("the caller opened the connection, so the question's own open and close leave it open");
    await transaction.RollbackAsync();
    await transaction.DisposeAsync();
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(1L)
      .Because("the control: after the rollback the same question answers on the same connection");
  }

  // ── Cost ─────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task NeverArchived_EachQuestionIsOneStatement_AndTheLastSequenceReadsTheLedgerOnlyWhenNothingIsHotAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 2);
    var store = _store(ctx);

    await Assert.That(await _countStatementsAsync(() => store.GetLastSequenceAsync(stream))).IsEqualTo(1);
    await Assert.That(await _countStatementsAsync(() => store.HasStreamEventsBeforeAsync(stream, ids[1]))).IsEqualTo(1);
    await Assert.That(await _countStatementsAsync(() => store.HasStreamEventsBeforeAsync(stream, ids[1], _handled))).IsEqualTo(1);

    var hotPlan = await _planAsync(connection, ArchivedStreamHistorySql.LastSequence(string.Empty), stream);
    var emptyPlan = await _planAsync(connection, ArchivedStreamHistorySql.LastSequence(string.Empty), Guid.CreateVersion7());
    await Assert.That(_ledgerProbeNeverExecuted(hotPlan)).IsTrue()
      .Because("a stream with a hot event answers from the event store; the ledger lookup is never run");
    await Assert.That(_ledgerProbeNeverExecuted(emptyPlan)).IsFalse()
      .Because("the control: with nothing hot the plan does run the ledger lookup, so the harness can tell the two apart");
  }

  private async Task<int> _countStatementsAsync(Func<Task> call) {
    var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
    var count = 0;
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Npgsql",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = activity => {
        if (activity.GetTagItem("db.namespace") as string == database && activity.GetTagItem("db.query.text") is string) {
          Interlocked.Increment(ref count);
        }
      },
    };
    ActivitySource.AddActivityListener(listener);
    await call();
    return Volatile.Read(ref count);
  }

  private static async Task<string> _planAsync(NpgsqlConnection connection, string sql, Guid stream) {
    await using var explain = connection.CreateCommand();
    explain.CommandText = "EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) " + sql;
    explain.Parameters.AddWithValue("StreamId", stream);
    var lines = new List<string>();
    await using var reader = await explain.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join('\n', lines);
  }

  // The ledger lookup is the plan node that scans wh_stream_lifecycle; "never executed" is how EXPLAIN ANALYZE reports a
  // subplan COALESCE did not need.
  private static bool _ledgerProbeNeverExecuted(string plan) {
    var lines = plan.Split('\n');
    var ledger = Array.FindIndex(lines, l => l.Contains("wh_stream_lifecycle", StringComparison.Ordinal));
    return ledger >= 0 && lines[ledger].Contains("never executed", StringComparison.Ordinal);
  }
}
