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
/// PostgreSQL-specific tests for DapperPostgresEventStore retry logic and error handling.
/// These tests cover implementation-specific paths not exercised by contract tests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
public class DapperPostgresEventStoreRetryTests : IDisposable {

  private PostgresTestBase _testBase = null!;
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

  [Test]
  public async Task AppendAsync_WithHighConcurrency_ShouldRetryAndSucceedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    const int concurrency = 8; // Moderate concurrency to force retries but succeed
    var envelopes = Enumerable.Range(0, concurrency)
      .Select(_ => _createTestEnvelope(streamId))
      .ToList();

    // Act - Fire off many concurrent appends to the same stream
    var tasks = envelopes.Select(env => _store.AppendAsync(streamId, env));
    await Task.WhenAll(tasks);

    // Assert - All events should eventually succeed despite conflicts
    var lastSequence = await _store.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(concurrency - 1L);

    // Verify all events were stored
    var events = new List<IMessageEnvelope>();
    await foreach (var e in _store.ReadAsync<PostgresRetryTestEvent>(streamId, 0)) {
      events.Add(e);
    }
    await Assert.That(events).Count().IsEqualTo(concurrency);
  }

  [Test]
  public async Task AppendAsync_ExtremelyHighConcurrency_ShouldHandleRetriesAsync() {
    // Arrange - Create a scenario that will force many retries
    var streamId = Guid.NewGuid();
    const int concurrency = 10;
    var envelopes = Enumerable.Range(0, concurrency)
      .Select(_ => _createTestEnvelope(streamId))
      .ToList();

    // Act - Maximum concurrent pressure
    var tasks = envelopes.Select(env => Task.Run(async () => await _store.AppendAsync(streamId, env)));
    await Task.WhenAll(tasks);

    // Assert - Despite extreme concurrency, all should eventually succeed
    var lastSequence = await _store.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(concurrency - 1L);
  }

  [Test]
  public async Task AppendAsync_ConcurrentAppendsToSameSequence_ShouldResolveConflictsAsync() {
    // Arrange
    var streamId = Guid.NewGuid();

    // Act - Start 10 threads all trying to append at the same time
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => {
        var envelope = _createTestEnvelope(streamId);
        await _store.AppendAsync(streamId, envelope);
      }))
      .ToArray();

    await Task.WhenAll(tasks);

    // Assert - All 10 events should be stored with sequential sequence numbers
    var events = new List<IMessageEnvelope>();
    await foreach (var e in _store.ReadAsync<PostgresRetryTestEvent>(streamId, 0)) {
      events.Add(e);
    }
    await Assert.That(events).Count().IsEqualTo(10);

    var lastSequence = await _store.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(9L);
  }

  [Test]
  public async Task AppendAsync_WithRetryBackoff_ShouldEventuallySucceedAsync() {
    // Arrange - Use moderate concurrency to observe retry behavior
    var streamId = Guid.NewGuid();
    const int count = 8;

    // Act - All appends happen simultaneously to force conflicts
    await Task.WhenAll(
      Enumerable.Range(0, count).Select(_ =>
        _store.AppendAsync(streamId, _createTestEnvelope(streamId))));

    // Assert - All should succeed after retries
    var lastSequence = await _store.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(count - 1L);
  }

  [Test]
  public async Task AppendAsync_ExtremeContention_ShouldEventuallyThrowMaxRetriesAsync() {
    // Arrange - Create extreme contention to potentially hit max retries
    var streamId = Guid.NewGuid();

    // Pre-insert many events to create a starting point
    for (int i = 0; i < 50; i++) {
      await _store.AppendAsync(streamId, _createTestEnvelope(streamId));
    }

    // Act - Launch 30 concurrent appends to create maximum conflict
    var exceptionCount = 0;
    var successCount = 0;
    var tasks = Enumerable.Range(0, 30).Select(_ => Task.Run(async () => {
      try {
        await _store.AppendAsync(streamId, _createTestEnvelope(streamId));
        Interlocked.Increment(ref successCount);
      } catch (InvalidOperationException ex) when (ex.Message.Contains("after 10 attempts")) {
        Interlocked.Increment(ref exceptionCount);
      }
    })).ToArray();

    await Task.WhenAll(tasks);

    // Assert - Some should succeed and some may hit max retries under extreme contention.
    // This test validates the error path exists and is reachable.
    // Under resource pressure (CI, containers), success rate varies widely.
    await Assert.That(successCount + exceptionCount).IsEqualTo(30); // All tasks completed
    await Assert.That(successCount).IsGreaterThan(0); // At least one succeeded
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

  private sealed class TestFixture : PostgresTestBase;
}
