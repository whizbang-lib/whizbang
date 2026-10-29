// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1871 on real PostgreSQL. Per-stream outbox flushes that cannot get a connection from the pool fail with Npgsql's
/// "connection pool has been exhausted", which the store itself does not retry (only deadlocks and serialization
/// failures are); the sliding-window batcher dropped the batch. It now retries a transient failure, and a batch that
/// still cannot be stored is written to wh_dead_letters through <c>wh_store_outbox_dead_letters</c>, from where
/// dead-letter recovery stores it through <c>store_outbox_messages</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/256_UnstoredOutboxMessagesAreDeadLettered.sql</code-under-test>
[Category("Shard5")]
public class OutboxFlushPoolExhaustionTests : EFCoreTestBase {
  private const int POOL_SIZE = 2;
  private const int STREAMS = 6;
  private const int PARTITION_COUNT = 10000;

  /// <summary>
  /// The pool of a data source of its own is held by the test, so every per-stream flush's first try is certain to
  /// find it exhausted, as a remote database's pool is under a burst of concurrent flushes. Once every stream has
  /// failed once the pool is released, and the retries (no more of them at once than the pool holds, as the
  /// work-coordinator gate allows in production) store every message.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task PerStreamFlushes_PoolExhausted_EveryAcceptedMessageReachesTheOutboxAsync(CancellationToken cancellationToken) {
    // A small pool with a short wait for a connection: the failure a remote database's pool produces under a burst
    // of concurrent flushes, without needing the burst.
    await using var dataSource = _dataSource(new NpgsqlConnectionStringBuilder(ConnectionString) {
      MaxPoolSize = POOL_SIZE,
      Timeout = 1,
    }.ConnectionString);
    var contextOptions = _contextOptions(dataSource);

    // Every pooled connection is taken before any flush runs, so every flush's first try is certain to fail.
    var held = new List<NpgsqlConnection>();
    for (var i = 0; i < POOL_SIZE; i++) {
      held.Add(await dataSource.OpenConnectionAsync(cancellationToken));
    }

    var attemptsByStream = new ConcurrentDictionary<Guid, int>();
    var firstTryFailures = 0;
    var everyStreamFailedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // The work-coordinator gate's part: no more stores at once than the pool has connections.
    var gate = new SemaphoreSlim(POOL_SIZE, POOL_SIZE);
    var clock = new TimerAdvancingClock(TimeSpan.FromMinutes(5));
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: async (messages, ct) => {
        var first = attemptsByStream.AddOrUpdate(messages[0].StreamId!.Value, 1, static (_, n) => n + 1) == 1;
        await gate.WaitAsync(ct);
        try {
          // What the registered flush callback does: a fresh context per batch, then the store.
          await using var context = new WorkCoordinationDbContext(contextOptions);
          var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
          await coordinator.StoreOutboxMessagesAsync(messages, PARTITION_COUNT, ct);
        } catch (NpgsqlException) when (first) {
          if (Interlocked.Increment(ref firstTryFailures) == STREAMS) {
            everyStreamFailedOnce.TrySetResult();
          }
          throw;
        } finally {
          gate.Release();
        }
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: new SlidingWindowOutboxOptions {
        // One message per batch: each stream's drain worker flushes as soon as its message is appended.
        MaxSize = 1,
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromSeconds(30),
      },
      timeProvider: clock,
      onFlushFailed: null,
      metrics: null);

    var appended = new List<Guid>(STREAMS);
    for (var i = 0; i < STREAMS; i++) {
      var messageId = (Guid)TrackedGuid.New();
      appended.Add(messageId);
      await sut.AppendAsync(CreateTestOutboxMessage(messageId, "out-topic", (Guid)TrackedGuid.New()), cancellationToken);
    }

    await everyStreamFailedOnce.Task.WaitAsync(cancellationToken);
    // The pool frees up, as a remote database's does once the burst passes. Only then are the retries' waits let
    // elapse, so every retry finds a connection.
    foreach (var connection in held) {
      await connection.DisposeAsync();
    }
    await clock.AdvanceTimersUntilAsync(sut.FlushAndStopAsync(CancellationToken.None), cancellationToken);

    var stored = await _countAsync("SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)", appended, cancellationToken);
    await Assert.That(stored).IsEqualTo((long)STREAMS)
      .Because($"{STREAMS - stored} of {STREAMS} accepted messages have no wh_outbox row: a flush that found the pool "
             + "exhausted has to be retried, not dropped (#1871)");
  }

  /// <summary>
  /// Messages that were never stored are written as wh_outbox dead letters with the request's audit columns, each
  /// is logged in wh_log under its message id, and recovery stores the message through store_outbox_messages.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task StoreOutboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var first = CreateTestOutboxMessage((Guid)TrackedGuid.New(), "out-topic", stream);
    var second = CreateTestOutboxMessage((Guid)TrackedGuid.New(), "out-topic", stream);
    var instanceId = (Guid)TrackedGuid.New();

    var kept = await coordinator.StoreOutboxDeadLettersAsync([first, second],
      new OutboxDeadLetterRequest(MessageFailureReason.OutboxStoreFailed, "pool exhausted", 5, instanceId, "gen-1"),
      cancellationToken);

    await Assert.That(kept).IsTrue();
    List<Guid> ids = [first.MessageId, second.MessageId];
    await Assert.That(await _countAsync(
      "SELECT count(*) FROM wh_dead_letters WHERE source_table = 'wh_outbox' AND source_id = ANY(@ids) AND failure_reason = 23 "
      + "AND attempts_when_dlq = 5 AND generation = 'gen-1' AND error_text = 'pool exhausted' AND stream_id IS NOT NULL",
      ids, cancellationToken)).IsEqualTo(2L);
    await Assert.That(await _countAsync(
      "SELECT count(*) FROM wh_log WHERE source = 'outbox_flush' AND log_level = 3 AND message_id = ANY(@ids)",
      ids, cancellationToken)).IsEqualTo(2L)
      .Because("each message that was not stored is logged in wh_log under its id");

    var deadLetterId = await _scalarAsync<Guid>(
      "SELECT dead_letter_id FROM wh_dead_letters WHERE source_id = ANY(@ids) ORDER BY source_id LIMIT 1", ids, cancellationToken);
    var recovered = await _scalarAsync<bool>("SELECT wh_recover_dead_letter((@ids)[1], 10000)", [deadLetterId], cancellationToken);
    await Assert.That(recovered).IsTrue();
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)", ids, cancellationToken))
      .IsEqualTo(1L).Because("recovery stores the dead-lettered message through the outbox store");
  }

  /// <summary>
  /// A message the store already committed (only its reply was lost) is not dead-lettered, a message already holding
  /// an unrecovered dead letter is not dead-lettered twice, and an empty batch writes nothing.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task StoreOutboxDeadLetters_SkipsAMessageAlreadyStoredOrAlreadyDeadLetteredAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var stored = CreateTestOutboxMessage((Guid)TrackedGuid.New(), "out-topic", stream);
    var unstored = CreateTestOutboxMessage((Guid)TrackedGuid.New(), "out-topic", stream);
    var request = new OutboxDeadLetterRequest(MessageFailureReason.OutboxStoreFailed, null, 1, Guid.Empty, "gen-1");
    await coordinator.StoreOutboxMessagesAsync([stored], PARTITION_COUNT, cancellationToken);

    await Assert.That(await coordinator.StoreOutboxDeadLettersAsync([], request, cancellationToken)).IsTrue();
    await coordinator.StoreOutboxDeadLettersAsync([stored, unstored], request, cancellationToken);
    await coordinator.StoreOutboxDeadLettersAsync([unstored], request, cancellationToken);

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_dead_letters WHERE source_id = ANY(@ids)", [stored.MessageId], cancellationToken))
      .IsEqualTo(0L).Because("the store committed this message; dead-lettering it would publish it twice once recovered");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_dead_letters WHERE source_id = ANY(@ids) AND error_text IS NULL AND error_fingerprint IS NULL", [unstored.MessageId], cancellationToken))
      .IsEqualTo(1L).Because("the same message reported twice keeps one dead letter");
  }

  private static NpgsqlDataSource _dataSource(string connectionString) {
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
  }

  private static DbContextOptions<WorkCoordinationDbContext> _contextOptions(NpgsqlDataSource dataSource) =>
    new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseNpgsql(dataSource, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private async Task<long> _countAsync(string sql, List<Guid> ids, CancellationToken cancellationToken) =>
    await _scalarAsync<long>(sql, ids, cancellationToken);

  private async Task<T> _scalarAsync<T>(string sql, List<Guid> ids, CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(ids), ids.ToArray());
    return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
  }
}
