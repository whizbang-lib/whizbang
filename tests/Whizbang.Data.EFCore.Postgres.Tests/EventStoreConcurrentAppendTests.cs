// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A direct append gets its version from the database, under the per-stream lock the emit chain
/// takes (#1555, migration 224). Many writers appending to one stream at once all succeed with
/// versions 0..N-1, a direct append waits for a chain emit holding the same stream, and the append
/// joins the caller's transaction.
/// </summary>
/// <remarks>
/// The concurrency cases are deterministic: a peer holds the stream's lock while every append is
/// started, the test waits until each append is observed waiting on that advisory lock (the
/// signal, not a delay), and only then releases it. Before the fix the appends never wait, so the
/// wait is never observed and the case fails rather than passing by luck.
/// </remarks>
/// <docs>fundamentals/events/event-store#appending-and-concurrency</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/224_DirectAppendTakesTheStreamLock.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class EventStoreConcurrentAppendTests : EFCoreTestBase {
  private const int WRITERS = 20;
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync() {
    var streamId = Guid.CreateVersion7();
    await using var holder = await _holdStreamLockAsync(streamId);

    var contexts = Enumerable.Range(0, WRITERS).Select(_ => CreateDbContext()).ToList();
    try {
      var appends = contexts.ConvertAll(
        c => Task.Run(() => new EFCoreEventStore<WorkCoordinationDbContext>(c).AppendAsync(streamId, _envelope())));
      var allWaited = await _untilWaitingOnAdvisoryLocksAsync(WRITERS, Task.WhenAll(appends));
      await holder.Transaction.CommitAsync();
      await Task.WhenAll(appends);

      await Assert.That(allWaited).IsTrue()
        .Because("every append must queue on the stream's lock, or the versions below were not decided under it");
    } finally {
      foreach (var c in contexts) {
        await c.DisposeAsync();
      }
    }

    var versions = await _versionsAsync(streamId);
    await Assert.That(versions).IsEquivalentTo(Enumerable.Range(0, WRITERS).ToList())
      .Because("twenty writers on one stream get versions 0..19: no gap, no duplicate, no 'Concurrent modification detected'");
  }

  [Test]
  public async Task ADirectAppend_WaitsForTheEmitChainsStreamLockAsync() {
    var streamId = Guid.CreateVersion7();
    // The peer plays the emit chain: it holds the chain's key for the stream and stores version 1,
    // as _emit_event_store_chain numbers a new stream from 1.
    await using var holder = await _holdStreamLockAsync(streamId);
    await _insertPointerAsync(holder, streamId, version: 1);

    await using var context = CreateDbContext();
    var append = new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(streamId, _envelope());
    var waited = await _untilWaitingOnAdvisoryLocksAsync(1, append);
    await holder.Transaction.CommitAsync();
    await append;

    await Assert.That(waited).IsTrue()
      .Because("a direct append and a chain emit on one stream take the same lock");
    int[] expectedVersions = [1, 2];
    await Assert.That(await _versionsAsync(streamId)).IsEquivalentTo(expectedVersions)
      .Because("the append read the tail after the chain's row committed");
  }

  [Test]
  public async Task TheAppend_JoinsTheCallersTransactionAsync() {
    var streamId = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await using (var transaction = await context.Database.BeginTransactionAsync()) {
      await store.AppendAsync(streamId, _envelope());
      await transaction.RollbackAsync();
    }

    await Assert.That(await _versionsAsync(streamId)).IsEmpty()
      .Because("the append ran inside the caller's transaction, so its rollback removed it");
  }

  [Test]
  public async Task PendingTrackedChanges_AreStillSavedByTheAppendAsync() {
    var streamId = Guid.CreateVersion7();
    var messageId = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    context.Set<MessageDeduplicationRecord>().Add(new MessageDeduplicationRecord { MessageId = messageId, FirstSeenAt = _fixedTime });

    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(streamId, _envelope());

    await using var reader = CreateDbContext();
    await Assert.That(await reader.Set<MessageDeduplicationRecord>().AnyAsync(r => r.MessageId == messageId)).IsTrue()
      .Because("the append always flushed the shared context's pending changes, and callers may rely on it");
    int[] expectedVersions = [0];
    await Assert.That(await _versionsAsync(streamId)).IsEquivalentTo(expectedVersions);
  }

  [Test]
  public async Task TheFunction_ExistsOnce_AndAReRunOfTheMigrationLeavesItSoAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    // A re-run of 224 re-runs every later migration that redefines the function too (the ledger's redefinition
    // closure), so the server ends on the last word, which the store calls (228 added a trailing parameter, #1546).
    var migrations = new PostgresMigrationProvider().GetMigrations()
      .Where(m => string.CompareOrdinal(m.Name, "224_") >= 0
        && m.Sql.Contains("FUNCTION public.wh_append_event(", StringComparison.Ordinal))
      .ToList();
    await Assert.That(migrations[0].Name).StartsWith("224_");
    foreach (var migration in migrations) {
      await using var rerun = connection.CreateCommand();
      rerun.CommandText = migration.Sql;  // schema and constants already substituted
      await rerun.ExecuteNonQueryAsync();
    }

    await using var count = connection.CreateCommand();
    count.CommandText = "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname = 'wh_append_event'";
    await Assert.That(Convert.ToInt32(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1);

    var streamId = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(streamId, _envelope());
    int[] expectedVersions = [0];
    await Assert.That(await _versionsAsync(streamId)).IsEquivalentTo(expectedVersions)
      .Because("the re-run function still appends; direct appends number from 0 as before");
  }

  // ===== Helpers =====

  private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable {
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;

    public async ValueTask DisposeAsync() {
      await Transaction.DisposeAsync();
      await Connection.DisposeAsync();
    }
  }

  /// <summary>Takes the emit chain's lock for the stream in an open transaction (214's key, copied verbatim).</summary>
  private async Task<LockHolder> _holdStreamLockAsync(Guid streamId) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var transaction = await connection.BeginTransactionAsync();
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @sid::text))";
    command.Parameters.AddWithValue("sid", streamId);
    await command.ExecuteNonQueryAsync();
    return new LockHolder(connection, transaction);
  }

  private static async Task _insertPointerAsync(LockHolder holder, Guid streamId, int version) {
    await using var command = holder.Connection.CreateCommand();
    command.Transaction = holder.Transaction;
    command.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@id, @sid, @sid, 'Chain', 'Chain, Chain', @v, @at)
      """;
    command.Parameters.AddWithValue("id", Guid.CreateVersion7());
    command.Parameters.AddWithValue("sid", streamId);
    command.Parameters.AddWithValue("v", version);
    command.Parameters.AddWithValue("at", _fixedTime);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Returns once <paramref name="expected"/> backends of this database wait on an advisory lock: the
  /// signal, not a delay. Returns false if the calls finish first, so an append that never waits fails
  /// the assertion instead of hanging the test.
  /// </summary>
  private async Task<bool> _untilWaitingOnAdvisoryLocksAsync(int expected, Task calls) {
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var command = observer.CreateCommand();
    command.CommandText = """
      SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
      WHERE l.locktype = 'advisory' AND NOT l.granted AND a.datname = current_database()
      """;
    while (!calls.IsCompleted) {
      if (Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) >= expected) {
        return true;
      }
      await Task.Yield();
    }
    return false;
  }

  private async Task<List<int>> _versionsAsync(Guid streamId) {
    await using var context = CreateDbContext();
    return await context.Set<EventStoreRecord>().AsNoTracking()
      .Where(e => e.StreamId == streamId).OrderBy(e => e.Version).Select(e => e.Version).ToListAsync();
  }

  private static MessageEnvelope<OrderCreatedEvent> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new OrderCreatedEvent { OrderId = Guid.CreateVersion7(), CustomerName = "Concurrent" },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = _fixedTime,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.CreateVersion7(),
          ServiceName = "test-service",
          HostName = "test-host",
          ProcessId = 123
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };
}
