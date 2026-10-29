// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1871 on real PostgreSQL: per-stream outbox flushes that cannot get a connection from the pool fail with Npgsql's
/// "connection pool has been exhausted", which the store does not retry (only deadlocks and serialization failures
/// are), and the sliding-window batcher then drops the batch. A remote database holds each connection long enough
/// for 126 concurrent per-stream flushes to overflow the pool; here the pool is held by the test, so every flush is
/// certain to find it exhausted, and releasing it afterward means a retry would succeed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
public class OutboxFlushPoolExhaustionTests : EFCoreTestBase {
  private const int POOL_SIZE = 2;
  private const int STREAMS = 6;

  [Test]
  [Timeout(120000)]
  public async Task PerStreamFlushes_PoolExhausted_EveryAcceptedMessageReachesTheOutboxAsync(CancellationToken cancellationToken) {
    // A pool of its own, small, with a short wait for a connection: the failure a remote database's pool produces
    // under a burst of concurrent flushes, without needing the burst.
    var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) {
      MaxPoolSize = POOL_SIZE,
      Timeout = 1,
    }.ConnectionString;
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseVector();
    await using var dataSource = dataSourceBuilder.Build();
    var contextOptions = new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseNpgsql(dataSource, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

    // Every pooled connection is taken before any flush runs, so every flush's first try is certain to fail.
    var held = new List<NpgsqlConnection>();
    for (var i = 0; i < POOL_SIZE; i++) {
      held.Add(await dataSource.OpenConnectionAsync(cancellationToken));
    }

    var attemptsByStream = new ConcurrentDictionary<Guid, int>();
    var firstTryFailures = 0;
    var everyStreamFailedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: async (messages, ct) => {
        var first = attemptsByStream.AddOrUpdate(messages[0].StreamId!.Value, 1, static (_, n) => n + 1) == 1;
        try {
          // What the registered flush callback does: a fresh context per batch, then the store.
          await using var context = new WorkCoordinationDbContext(contextOptions);
          var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
          await coordinator.StoreOutboxMessagesAsync(messages, partitionCount: 10000, ct);
        } catch (NpgsqlException) when (first) {
          if (Interlocked.Increment(ref firstTryFailures) == STREAMS) {
            everyStreamFailedOnce.TrySetResult();
          }
          throw;
        }
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: new SlidingWindowOutboxOptions {
        // One message per batch: each stream's drain worker flushes as soon as its message is appended.
        MaxSize = 1,
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromSeconds(30),
      },
      timeProvider: new FakeTimeProvider());

    var appended = new List<Guid>(STREAMS);
    for (var i = 0; i < STREAMS; i++) {
      var messageId = (Guid)TrackedGuid.New();
      appended.Add(messageId);
      await sut.AppendAsync(CreateTestOutboxMessage(messageId, "out-topic", (Guid)TrackedGuid.New()), cancellationToken);
    }

    await everyStreamFailedOnce.Task.WaitAsync(cancellationToken);
    // The pool frees up, as a remote database's does once the burst passes: any further try finds a connection.
    foreach (var connection in held) {
      await connection.DisposeAsync();
    }
    await sut.FlushAndStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

    var stored = await _outboxRowCountAsync(appended, cancellationToken);
    await Assert.That(stored).IsEqualTo((long)STREAMS)
      .Because($"{STREAMS - stored} of {STREAMS} accepted messages have no wh_outbox row: each flush that found the pool "
             + "exhausted was dropped, and nothing re-emits it (#1871)");
  }

  private async Task<long> _outboxRowCountAsync(List<Guid> messageIds, CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)";
    command.Parameters.AddWithValue("ids", messageIds.ToArray());
    return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
  }
}
