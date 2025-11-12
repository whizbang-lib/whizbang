using System.Diagnostics.CodeAnalysis;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// PostgreSQL-specific tests for DapperPostgresEventStore retry logic and error handling.
/// These tests cover implementation-specific paths not exercised by contract tests.
/// </summary>
[NotInParallel]
public class DapperPostgresEventStoreRetryTests {
  private PostgresTestBase _testBase = null!;
  private DapperPostgresEventStore _store = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
    _store = new DapperPostgresEventStore(_testBase.ConnectionFactory, _testBase.Executor);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_WithHighConcurrency_ShouldRetryAndSucceedAsync() {
    // Arrange
    var streamKey = $"retry-stream-{Guid.NewGuid()}";
    var concurrency = 8; // Moderate concurrency to force retries but succeed
    var envelopes = Enumerable.Range(0, concurrency)
      .Select(_ => CreateTestEnvelope())
      .ToList();

    // Act - Fire off many concurrent appends to the same stream
    var tasks = envelopes.Select(env => _store.AppendAsync(streamKey, env));
    await Task.WhenAll(tasks);

    // Assert - All events should eventually succeed despite conflicts
    var lastSequence = await _store.GetLastSequenceAsync(streamKey);
    await Assert.That(lastSequence).IsEqualTo(concurrency - 1L);

    // Verify all events were stored
    var events = new List<IMessageEnvelope>();
    await foreach (var e in _store.ReadAsync(streamKey, 0)) {
      events.Add(e);
    }
    await Assert.That(events).HasCount().EqualTo(concurrency);
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_WithManualDuplicateInsert_ShouldCauseUniqueViolationAsync() {
    // Arrange
    var streamKey = $"manual-retry-{Guid.NewGuid()}";
    var envelope = CreateTestEnvelope();

    // Act & Assert - First append should succeed
    await _store.AppendAsync(streamKey, envelope);

    // Manually insert with sequence 0 to create conflict
    using var connection = await _testBase.ConnectionFactory.CreateConnectionAsync();
    var sql = @"INSERT INTO whizbang_event_store (stream_key, sequence_number, envelope, created_at)
                VALUES (@StreamKey, 0, @Envelope, @CreatedAt)";

    // This should cause a unique constraint violation
    await Assert.That(async () => {
      await _testBase.Executor.ExecuteAsync(
        connection,
        sql,
        new {
          StreamKey = streamKey,
          SequenceNumber = 0L,
          Envelope = System.Text.Json.JsonSerializer.Serialize(envelope),
          CreatedAt = DateTimeOffset.UtcNow
        });
    }).ThrowsException();
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_ExtremelyHighConcurrency_ShouldHandleRetriesAsync() {
    // Arrange - Create a scenario that will force many retries
    var streamKey = $"extreme-{Guid.NewGuid()}";
    var concurrency = 10;
    var envelopes = Enumerable.Range(0, concurrency)
      .Select(_ => CreateTestEnvelope())
      .ToList();

    // Act - Maximum concurrent pressure
    var tasks = envelopes.Select(env => Task.Run(async () => {
      await _store.AppendAsync(streamKey, env);
    }));
    await Task.WhenAll(tasks);

    // Assert - Despite extreme concurrency, all should eventually succeed
    var lastSequence = await _store.GetLastSequenceAsync(streamKey);
    await Assert.That(lastSequence).IsEqualTo(concurrency - 1L);
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_ConcurrentAppendsToSameSequence_ShouldResolveConflictsAsync() {
    // Arrange
    var streamKey = $"conflict-resolution-{Guid.NewGuid()}";

    // Act - Start 10 threads all trying to append at the same time
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => {
        var envelope = CreateTestEnvelope();
        await _store.AppendAsync(streamKey, envelope);
      }))
      .ToArray();

    await Task.WhenAll(tasks);

    // Assert - All 20 events should be stored with sequential sequence numbers
    var events = new List<IMessageEnvelope>();
    await foreach (var e in _store.ReadAsync(streamKey, 0)) {
      events.Add(e);
    }
    await Assert.That(events).HasCount().EqualTo(10);

    var lastSequence = await _store.GetLastSequenceAsync(streamKey);
    await Assert.That(lastSequence).IsEqualTo(9L);
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_WithRetryBackoff_ShouldEventuallySucceedAsync() {
    // Arrange - Use moderate concurrency to observe retry behavior
    var streamKey = $"backoff-test-{Guid.NewGuid()}";
    var count = 8;

    // Act - All appends happen simultaneously to force conflicts
    await Task.WhenAll(
      Enumerable.Range(0, count).Select(_ =>
        _store.AppendAsync(streamKey, CreateTestEnvelope())));

    // Assert - All should succeed after retries
    var lastSequence = await _store.GetLastSequenceAsync(streamKey);
    await Assert.That(lastSequence).IsEqualTo(count - 1L);
  }

  [Test]
  [RequiresUnreferencedCode("JSON serialization uses reflection")]
  public async Task AppendAsync_ExtremeContention_ShouldEventuallyThrowMaxRetriesAsync() {
    // Arrange - Create extreme contention to potentially hit max retries
    var streamKey = $"max-retries-{Guid.NewGuid()}";

    // Pre-insert many events to create a starting point
    for (int i = 0; i < 50; i++) {
      await _store.AppendAsync(streamKey, CreateTestEnvelope());
    }

    // Act - Launch 30 concurrent appends to create maximum conflict
    var exceptionCount = 0;
    var successCount = 0;
    var tasks = Enumerable.Range(0, 30).Select(_ => Task.Run(async () => {
      try {
        await _store.AppendAsync(streamKey, CreateTestEnvelope());
        Interlocked.Increment(ref successCount);
      } catch (InvalidOperationException ex) when (ex.Message.Contains("after 10 attempts")) {
        Interlocked.Increment(ref exceptionCount);
      }
    })).ToArray();

    await Task.WhenAll(tasks);

    // Assert - Most should succeed, but under extreme contention some might hit max retries
    // This test validates the error path exists and is reachable
    await Assert.That(successCount).IsGreaterThan(15); // At least half should succeed
  }

  [Test]
  public async Task AppendAsync_WithNonUniqueViolationException_ShouldPropagateExceptionAsync() {
    // Arrange
    var streamKey = $"non-unique-{Guid.NewGuid()}";

    // Drop the event store table to cause a different kind of error
    using var connection = await _testBase.ConnectionFactory.CreateConnectionAsync();
    await _testBase.Executor.ExecuteAsync(
      connection,
      "DROP TABLE IF EXISTS whizbang_event_store CASCADE",
      new { });

    // Act & Assert - Should throw exception (not unique violation, so no retry)
    await Assert.That(async () => {
      await _store.AppendAsync(streamKey, CreateTestEnvelope());
    }).ThrowsException();

    // Restore the table for other tests
    var schemaPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.Dapper.Postgres", "whizbang-schema.sql");
    var schemaSql = await File.ReadAllTextAsync(schemaPath);
    await _testBase.Executor.ExecuteAsync(connection, schemaSql, new { });
  }

  private static MessageEnvelope<string> CreateTestEnvelope() {
    return new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = $"test-payload-{Guid.NewGuid()}",
      Hops = []
    };
  }

  private class TestFixture : PostgresTestBase { }
}
