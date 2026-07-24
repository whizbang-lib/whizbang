using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Sqlite;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Targeted tests for <see cref="DapperSqliteEventStore"/> covering the paths the
/// shared contract tests skip on SQLite: the message-overload AppendAsync (minimal
/// envelope creation), the UNIQUE-constraint retry loop, the UUIDv7-based ReadAsync
/// overload, ReadPolymorphicAsync row filtering/skipping, and the not-implemented
/// GetEventsBetween SQL surface.
/// </summary>
/// <remarks>
/// NOTE on ReadPolymorphicAsync: the SQLite polymorphic reader looks for the legacy
/// long-form envelope property names ("MessageId"/"Payload"/"Hops"), while the current
/// envelope writer serializes short names ("id"/"p"/"h"). Rows written by AppendAsync
/// are therefore always skipped at the MessageId-extraction step. These tests lock in
/// that current behavior; legacy-format rows are seeded via raw SQL to exercise the
/// deeper skip branches.
/// </remarks>
public class DapperSqliteEventStorePolymorphicTests : IDisposable {
  private DapperTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public void Cleanup() {
    _testBase?.Cleanup();
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  // ========================================
  // APPEND: MESSAGE OVERLOAD (MINIMAL ENVELOPE)
  // ========================================

  [Test]
  public async Task AppendAsync_MessageOverload_StoresEventWithMinimalEnvelopeAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "minimal-envelope-event" };

    // Act - raw message overload creates the envelope internally
    await eventStore.AppendAsync(streamId, message);

    // Assert - read back and verify the store-generated envelope shape
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(1);

    var stored = events[0];
    await Assert.That(stored.Payload.StreamId).IsEqualTo(streamId);
    await Assert.That(stored.Payload.Payload).IsEqualTo("minimal-envelope-event");
    await Assert.That(stored.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(stored.MessageId.Value.Version).IsEqualTo(7);
    await Assert.That(stored.Hops).Count().IsEqualTo(1);
    await Assert.That(stored.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Unknown");
    await Assert.That(stored.Hops[0].ServiceInstance.InstanceId).IsEqualTo(Guid.Empty);
    await Assert.That(stored.DispatchContext.Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(stored.DispatchContext.Source).IsEqualTo(MessageSource.Local);
  }

  [Test]
  public async Task AppendAsync_MessageOverload_WithNullMessage_ShouldThrowAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();

    // Act & Assert - cast to TestEvent selects the raw-message overload
    await Assert.That(() => eventStore.AppendAsync(streamId, (TestEvent)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AppendAsync_MessageOverload_GeneratesUniqueMessageIdsAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();

    // Act
    await eventStore.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "first" });
    await eventStore.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "second" });

    // Assert - each append creates its own envelope with a fresh MessageId
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId).IsNotEqualTo(events[1].MessageId);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("first");
    await Assert.That(events[1].Payload.Payload).IsEqualTo("second");
  }

  // ========================================
  // APPEND: UNIQUE CONSTRAINT RETRY
  // ========================================

  [Test]
  public async Task AppendAsync_UniqueConstraintViolation_RetriesWithNextSequenceNumberAsync() {
    // Arrange - executor injects a competing row with the same sequence number
    // immediately before the store's own INSERT, forcing one UNIQUE violation
    var executor = new ConflictInjectingExecutor(_testBase.Executor, conflictsToInject: 1);
    var eventStore = _createEventStore(executor);
    var streamId = Guid.NewGuid();
    var envelope = _createEnvelope(streamId, "retried-event");

    // Act - must succeed on the second attempt with the next sequence number
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - two INSERT attempts were made (failed + retried)
    await Assert.That(executor.EventStoreInsertAttempts).IsEqualTo(2);

    // Both rows exist: the injected conflict row at sequence 0 and the retried row at sequence 1
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(events[1].MessageId).IsEqualTo(envelope.MessageId);

    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_UniqueConstraintViolationsExhaustRetries_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - every attempt collides, so all 10 retries are exhausted
    var executor = new ConflictInjectingExecutor(_testBase.Executor, conflictsToInject: int.MaxValue);
    var eventStore = _createEventStore(executor);
    var streamId = Guid.NewGuid();
    var envelope = _createEnvelope(streamId, "never-lands");

    // Act
    InvalidOperationException? caught = null;
    try {
      await eventStore.AppendAsync(streamId, envelope);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert - retries exhausted with the last SqliteException preserved as inner
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("after 10 attempts");
    await Assert.That(caught.InnerException is SqliteException).IsTrue();
    await Assert.That(executor.EventStoreInsertAttempts).IsEqualTo(10);
  }

  // ========================================
  // READ: UUIDv7 fromEventId OVERLOAD
  // ========================================

  [Test]
  public async Task ReadAsync_ByEventId_FromMidStream_ReturnsOnlyLaterEventsInOrderAsync() {
    // Arrange - MessageId.New() is monotonic UUIDv7, so append order == id order
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var envelope1 = _createEnvelope(streamId, "event-1");
    var envelope2 = _createEnvelope(streamId, "event-2");
    var envelope3 = _createEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    // Act - read strictly after the first event's id
    var events = await _readByEventIdAsync(eventStore, streamId, envelope1.MessageId.Value);

    // Assert - only the two later events, in order
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("event-2");
    await Assert.That(events[1].MessageId).IsEqualTo(envelope3.MessageId);
    await Assert.That(events[1].Payload.Payload).IsEqualTo("event-3");
  }

  [Test]
  public async Task ReadAsync_ByEventId_FromLastEvent_ReturnsEmptyAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var envelope1 = _createEnvelope(streamId, "event-1");
    var envelope2 = _createEnvelope(streamId, "event-2");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);

    // Act - nothing exists after the last event's id
    var events = await _readByEventIdAsync(eventStore, streamId, envelope2.MessageId.Value);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // ========================================
  // READ POLYMORPHIC
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_StoreWrittenRows_ShortNameFormatIsSkippedAsync() {
    // Arrange - append two events of distinct registered types to the same stream
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "typed-event"));
    await eventStore.AppendAsync(streamId, new MessageEnvelope<TestResponse> {
      MessageId = MessageId.New(),
      Payload = new TestResponse("typed-response"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    });

    // Both rows landed in the event store
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(1);

    // Act - polymorphic read with both types registered in JsonOptions
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent), typeof(TestResponse)]);

    // Assert - current behavior: the writer emits short envelope property names
    // ("id"/"p"/"h") while the polymorphic reader extracts the legacy "MessageId"
    // property, so every store-written row is skipped and the result is empty.
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_RowMissingMessageId_IsSkippedAsync() {
    // Arrange - seed a malformed row with no MessageId property at all
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var envelopeJson = $$$"""{"Payload":{"StreamId":"{{{streamId}}}","Payload":"orphan-event"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Row really exists in the table
    var rowCount = await _countRowsAsync(streamId);
    await Assert.That(rowCount).IsEqualTo(1);

    // Act
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    // Assert - row is skipped, not returned and no exception thrown
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_FromEventIdAtRowId_SkipsRowAsync() {
    // Arrange - legacy-format row with an extractable MessageId
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - fromEventId equal to the row's id means "strictly after", so the row is filtered
    var events = await _readPolymorphicAsync(eventStore, streamId, rowEventId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_UnregisteredEventType_IsSkippedAsync() {
    // Arrange - legacy-format row that passes MessageId extraction
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - GetTypeInfo returns null for types absent from the resolver chain,
    // so the row is skipped via the typeInfo == null branch rather than throwing
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(UnregisteredEventType)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_PayloadNotAnEvent_IsSkippedAsync() {
    // Arrange - legacy-format row whose Payload deserializes as a registered
    // NON-IEvent type (ServiceInstanceInfo), exercising the "not an IEvent" skip
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var instanceId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"sn":"legacy-service","ii":"{{{instanceId}}}","hn":"legacy-host","pi":42}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - ServiceInstanceInfo is registered in JsonOptions but is not an IEvent
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(ServiceInstanceInfo)]);

    // Assert - payload deserialized successfully but was discarded as non-IEvent
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_LegacyRowMissingPayload_IsSkippedAsync() {
    // Arrange - MessageId extractable, but no Payload property to deserialize
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var rowEventId = Guid.NewGuid();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - TestEvent is registered, but the row has nothing to deserialize
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // ========================================
  // HOPS + METADATA ROUND-TRIP
  // ========================================

  [Test]
  public async Task AppendAndReadAsync_HopsAndMetadata_RoundTripWithFullFidelityAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var correlationId = CorrelationId.New();
    var instanceId = Guid.NewGuid();
    var timestamp = new DateTimeOffset(2026, 3, 14, 9, 26, 53, 589, TimeSpan.Zero);

    using var metadataDoc = JsonDocument.Parse("""{"tag":"gold","attempt":3}""");
    var metadata = new Dictionary<string, JsonElement> {
      ["tag"] = metadataDoc.RootElement.GetProperty("tag").Clone(),
      ["attempt"] = metadataDoc.RootElement.GetProperty("attempt").Clone()
    };

    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = "hop-fidelity" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          CorrelationId = correlationId,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "PolymorphicTests",
            InstanceId = instanceId,
            HostName = "test-host",
            ProcessId = 4242
          },
          Timestamp = timestamp,
          Topic = "orders-topic",
          StreamId = streamId.ToString(),
          PartitionIndex = 7,
          SequenceNumber = 99,
          ExecutionStrategy = "SerialExecutor",
          Metadata = metadata,
          Duration = TimeSpan.FromMilliseconds(125),
          TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);
    var events = await _readBySequenceAsync(eventStore, streamId);

    // Assert - envelope-level fidelity
    await Assert.That(events).Count().IsEqualTo(1);
    var stored = events[0];
    await Assert.That(stored.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(stored.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(stored.DispatchContext.Source).IsEqualTo(MessageSource.Outbox);
    await Assert.That(stored.Hops).Count().IsEqualTo(1);

    // Hop-level fidelity
    var storedHop = stored.Hops[0];
    await Assert.That(storedHop.Type).IsEqualTo(HopType.Current);
    await Assert.That(storedHop.CorrelationId!.Value.Value).IsEqualTo(correlationId.Value);
    await Assert.That(storedHop.ServiceInstance.ServiceName).IsEqualTo("PolymorphicTests");
    await Assert.That(storedHop.ServiceInstance.InstanceId).IsEqualTo(instanceId);
    await Assert.That(storedHop.ServiceInstance.HostName).IsEqualTo("test-host");
    await Assert.That(storedHop.ServiceInstance.ProcessId).IsEqualTo(4242);
    await Assert.That(storedHop.Timestamp).IsEqualTo(timestamp);
    await Assert.That(storedHop.Topic).IsEqualTo("orders-topic");
    await Assert.That(storedHop.StreamId).IsEqualTo(streamId.ToString());
    await Assert.That(storedHop.PartitionIndex).IsEqualTo(7);
    await Assert.That(storedHop.SequenceNumber).IsEqualTo(99);
    await Assert.That(storedHop.ExecutionStrategy).IsEqualTo("SerialExecutor");
    await Assert.That(storedHop.Duration).IsEqualTo(TimeSpan.FromMilliseconds(125));
    await Assert.That(storedHop.TraceParent).IsEqualTo("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

    // Metadata fidelity
    await Assert.That(storedHop.Metadata).IsNotNull();
    await Assert.That(storedHop.Metadata!["tag"].GetString()).IsEqualTo("gold");
    await Assert.That(storedHop.Metadata!["attempt"].GetInt32()).IsEqualTo(3);
  }

  // ========================================
  // GET EVENTS BETWEEN (NOT IMPLEMENTED ON SQLITE)
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_OnSqlite_ThrowsNotImplementedExceptionAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();

    // Act & Assert - the public base-class method reaches the SQLite override of
    // GetEventsBetweenSql, which is not implemented for the single-envelope-column model
    await Assert.That(async () => await eventStore.GetEventsBetweenAsync<TestEvent>(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid()))
      .ThrowsExactly<NotImplementedException>();
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_OnSqlite_ThrowsNotImplementedExceptionAsync() {
    // Arrange
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();

    // Act & Assert - same unimplemented SQL surface via the polymorphic public method
    await Assert.That(async () => await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid(),
      eventTypes: [typeof(TestEvent)]))
      .ThrowsExactly<NotImplementedException>();
  }

  // ========================================
  // HELPERS
  // ========================================

  private DapperSqliteEventStore _createEventStore() {
    return _createEventStore(_testBase.Executor);
  }

  private DapperSqliteEventStore _createEventStore(IDbExecutor executor) {
    return new DapperSqliteEventStore(
      _testBase.ConnectionFactory,
      executor,
      JsonOptionsHelper.CreateOptions(),
      new PolicyEngine());
  }

  private static MessageEnvelope<TestEvent> _createEnvelope(Guid streamId, string payload) {
    return new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = payload },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "DapperSqliteEventStorePolymorphicTests",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static async Task<List<MessageEnvelope<TestEvent>>> _readBySequenceAsync(
    DapperSqliteEventStore eventStore, Guid streamId) {
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    return events;
  }

  private static async Task<List<MessageEnvelope<TestEvent>>> _readByEventIdAsync(
    DapperSqliteEventStore eventStore, Guid streamId, Guid? fromEventId) {
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId)) {
      events.Add(evt);
    }
    return events;
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readPolymorphicAsync(
    DapperSqliteEventStore eventStore, Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes) {
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, fromEventId, eventTypes)) {
      events.Add(evt);
    }
    return events;
  }

  private async Task _seedRawEnvelopeRowAsync(Guid streamId, long sequenceNumber, string envelopeJson) {
    using var command = _testBase.Connection.CreateCommand();
    command.CommandText = @"
      INSERT INTO whizbang_event_store (stream_id, sequence_number, envelope, created_at)
      VALUES (@StreamId, @SequenceNumber, @Envelope, datetime('now'))";
    command.Parameters.AddWithValue("@StreamId", streamId.ToString());
    command.Parameters.AddWithValue("@SequenceNumber", sequenceNumber);
    command.Parameters.AddWithValue("@Envelope", envelopeJson);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<long> _countRowsAsync(Guid streamId) {
    using var command = _testBase.Connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM whizbang_event_store WHERE stream_id = @StreamId";
    command.Parameters.AddWithValue("@StreamId", streamId.ToString());
    var result = await command.ExecuteScalarAsync();
    return (long)result!;
  }

  private sealed class TestFixture : DapperTestBase;

  /// <summary>
  /// A type deliberately absent from every JsonSerializerContext in the resolver chain.
  /// Used to verify ReadPolymorphicAsync behavior for unregistered event types.
  /// </summary>
  private sealed class UnregisteredEventType;

  /// <summary>
  /// IDbExecutor decorator that simulates a concurrent writer: before the event-store
  /// INSERT executes, it inserts an identical row (same stream + sequence number) so the
  /// store's own INSERT hits the UNIQUE constraint and enters its retry loop.
  /// </summary>
  private sealed class ConflictInjectingExecutor(IDbExecutor inner, int conflictsToInject) : IDbExecutor {
    private int _remainingConflicts = conflictsToInject;

    public int EventStoreInsertAttempts { get; private set; }

    public Task<IReadOnlyList<T>> QueryAsync<T>(
      IDbConnection connection,
      string sql,
      object? param = null,
      IDbTransaction? transaction = null,
      CancellationToken cancellationToken = default) {
      return inner.QueryAsync<T>(connection, sql, param, transaction, cancellationToken);
    }

    public Task<T?> QuerySingleOrDefaultAsync<T>(
      IDbConnection connection,
      string sql,
      object? param = null,
      IDbTransaction? transaction = null,
      CancellationToken cancellationToken = default) {
      return inner.QuerySingleOrDefaultAsync<T>(connection, sql, param, transaction, cancellationToken);
    }

    public async Task<int> ExecuteAsync(
      IDbConnection connection,
      string sql,
      object? param = null,
      IDbTransaction? transaction = null,
      CancellationToken cancellationToken = default) {
      if (sql.Contains("INSERT INTO whizbang_event_store", StringComparison.Ordinal)) {
        EventStoreInsertAttempts++;
        if (_remainingConflicts > 0) {
          _remainingConflicts--;
          // Simulate a concurrent writer claiming this sequence number first
          await inner.ExecuteAsync(connection, sql, param, transaction, cancellationToken);
        }
      }
      return await inner.ExecuteAsync(connection, sql, param, transaction, cancellationToken);
    }

    public Task<T?> ExecuteScalarAsync<T>(
      IDbConnection connection,
      string sql,
      object? param = null,
      IDbTransaction? transaction = null,
      CancellationToken cancellationToken = default) {
      return inner.ExecuteScalarAsync<T>(connection, sql, param, transaction, cancellationToken);
    }
  }
}
