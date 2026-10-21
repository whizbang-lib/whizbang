// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Dapper.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Test event with StreamId for stream ID inference.
/// </summary>
public record PostgresRetryTestEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Payload { get; init; }
}

/// <summary>
/// PostgreSQL-specific tests for DapperPostgresEventStore's append concurrency and error handling.
/// Since #1555 the database assigns the version under the stream's lock, so concurrent appends
/// never collide and are never retried; a duplicate event id fails at once.
/// These tests cover implementation-specific paths not exercised by contract tests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
public sealed class DapperPostgresEventStoreRetryTests : IDisposable {

  private TestFixture _testBase = null!;
  private DapperPostgresEventStore _store = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();

    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var sizeValidator = new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance);
    var policyEngine = new PolicyEngine();
    var logger = NullLogger<DapperPostgresEventStore>.Instance;

    _store = new DapperPostgresEventStore(
      _testBase.ConnectionFactory,
      _testBase.Executor,
      jsonOptions,
      adapter,
      sizeValidator,
      policyEngine,
      null, // perspectiveInvoker
      logger
    );
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Twenty writers append to one stream at once and all succeed with versions 0..19 (#1555). The
  /// database assigns each version under the stream's lock, so nothing collides and nothing is retried.
  /// Deterministic: a peer holds the stream's lock until every append is observed waiting on it.
  /// </summary>
  [Test]
  public async Task AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync() {
    const int writers = 20;
    var streamId = Guid.CreateVersion7();
    await using var holder = new NpgsqlConnection(_testBase.PublicConnectionString);
    await holder.OpenAsync();
    await using var holding = await holder.BeginTransactionAsync();
    await using (var take = holder.CreateCommand()) {
      take.Transaction = holding;
      take.CommandText = "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @sid::text))";
      take.Parameters.AddWithValue("sid", streamId);
      await take.ExecuteNonQueryAsync();
    }

    var appends = Enumerable.Range(0, writers)
      .Select(_ => Task.Run(() => _store.AppendAsync(streamId, _createTestEnvelope(streamId))))
      .ToList();
    var allWaited = await _untilWaitingOnAdvisoryLocksAsync(writers, Task.WhenAll(appends));
    await holding.CommitAsync();
    await Task.WhenAll(appends);

    await Assert.That(allWaited).IsTrue()
      .Because("every append must queue on the stream's lock, or the versions below were not decided under it");
    await using var reader = new NpgsqlConnection(_testBase.PublicConnectionString);
    await reader.OpenAsync();
    await using var query = reader.CreateCommand();
    query.CommandText = "SELECT version FROM wh_event_store WHERE stream_id = @sid ORDER BY version";
    query.Parameters.AddWithValue("sid", streamId);
    var versions = new List<int>();
    await using (var rows = await query.ExecuteReaderAsync()) {
      while (await rows.ReadAsync()) {
        versions.Add(rows.GetInt32(0));
      }
    }
    await Assert.That(versions).IsEquivalentTo(Enumerable.Range(0, writers).ToList());
  }

  /// <summary>
  /// A duplicate event id fails at once and says so: it is not retried as a concurrent write.
  /// </summary>
  [Test]
  public async Task AppendAsync_DuplicateEventId_FailsClearly_WithoutRetryingAsync() {
    var streamId = Guid.CreateVersion7();
    var envelope = _createTestEnvelope(streamId);
    await _store.AppendAsync(streamId, envelope);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _store.AppendAsync(streamId, envelope));

    await Assert.That(ex!.Message).Contains("an event with this id is already stored");
    await Assert.That(ex.Message).DoesNotContain("concurrent writes");
    await Assert.That((ex.InnerException as PostgresException)?.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    await Assert.That(await _store.GetLastSequenceAsync(streamId)).IsEqualTo(0L)
      .Because("the failed append stored nothing");
  }

  /// <summary>Returns once the given number of this database's backends wait on an advisory lock (false if the calls finish first).</summary>
  private async Task<bool> _untilWaitingOnAdvisoryLocksAsync(int expected, Task calls) {
    await using var observer = new NpgsqlConnection(_testBase.PublicConnectionString);
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

  [Test]
  public async Task AppendAsync_WithNonUniqueViolationException_ShouldPropagateExceptionAsync() {
    // Arrange
    var streamId = Guid.NewGuid();

    // Drop the event store table to cause a different kind of error
    using var connection = await _testBase.ConnectionFactory.CreateConnectionAsync();
    await _testBase.Executor.ExecuteAsync(
      connection,
      "DROP TABLE IF EXISTS wh_event_store CASCADE",
      new { });

    // Act & Assert - Should throw exception (not unique violation, so no retry)
    await Assert.That(async () => await _store.AppendAsync(streamId, _createTestEnvelope(streamId))).ThrowsException();

    // Restore the table for other tests by regenerating schema from C#
    var schemaConfig = new SchemaConfiguration(
      InfrastructurePrefix: "wh_",
      PerspectivePrefix: "wh_per_"
    );
    var schemaSql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(schemaConfig);
    await _testBase.Executor.ExecuteAsync(connection, schemaSql, new { });
  }

  private static MessageEnvelope<PostgresRetryTestEvent> _createTestEnvelope(Guid aggregateId) {
    var envelope = new MessageEnvelope<PostgresRetryTestEvent> {
      MessageId = MessageId.New(),
      Payload = new PostgresRetryTestEvent {
        StreamId = aggregateId,
        Payload = $"test-payload-{Guid.NewGuid()}"
      },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Add the first hop (dispatch hop)
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "DapperPostgresEventStoreRetryTests",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current
    });

    return envelope;
  }

  private sealed class TestFixture : PostgresTestBase {
    public string PublicConnectionString => ConnectionString;
  }
}
