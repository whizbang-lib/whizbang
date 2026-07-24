using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
/// Deep-path tests for <see cref="DapperSqliteEventStore"/> covering the branches the
/// contract tests and <see cref="DapperSqliteEventStorePolymorphicTests"/> leave untouched:
/// the message-based UNIQUE-constraint fallbacks in the retry filter (non-SqliteException),
/// the TraceParent capture from an ambient Activity, null-envelope row skipping in both
/// ReadAsync overloads, early-abandoned async iterators, and the full polymorphic envelope
/// reconstruction path (MessageId + Payload + Hops variants).
/// </summary>
/// <remarks>
/// The polymorphic success path requires MessageId to deserialize from the legacy object
/// form <c>{"Value":"guid"}</c> that <c>_tryExtractMessageId</c> demands, while the
/// production resolver chain registers a string-form (Uuid7) MessageId converter. These
/// tests therefore prepend a test-only resolver that registers an object-form MessageId
/// converter, which is the only way to drive <c>_tryDeserializeAsEventType</c> to completion.
/// </remarks>
public class DapperSqliteEventStoreDeepPathTests : IDisposable {
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
  // APPEND RETRY FILTER: MESSAGE-BASED FALLBACKS
  // (non-SqliteException arm of _isUniqueConstraintViolation)
  // ========================================

  [Test]
  public async Task AppendAsync_NonSqliteExceptionWithUniqueConstraintMessage_RetriesAndSucceedsAsync() {
    // Arrange - a non-SqliteException whose message matches the "UNIQUE constraint" fallback
    var executor = new ThrowingInsertExecutor(
      _testBase.Executor,
      new InvalidOperationException("simulated UNIQUE constraint violation from pooled wrapper"),
      timesToThrow: 1);
    var eventStore = _createEventStore(executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createEnvelope(streamId, "unique-constraint-message");

    // Act - first INSERT throws, message-based detection triggers one retry
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - two attempts, one stored row
    await Assert.That(executor.InsertAttempts).IsEqualTo(2);
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task AppendAsync_NonSqliteExceptionWithConstraintFailedMessage_RetriesAndSucceedsAsync() {
    // Arrange - message hits the second fallback ("constraint failed") without
    // containing "UNIQUE constraint", so the first Contains check evaluates false
    var executor = new ThrowingInsertExecutor(
      _testBase.Executor,
      new InvalidOperationException("write aborted because a constraint failed mid-batch"),
      timesToThrow: 1);
    var eventStore = _createEventStore(executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createEnvelope(streamId, "constraint-failed-message");

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert
    await Assert.That(executor.InsertAttempts).IsEqualTo(2);
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("constraint-failed-message");
  }

  [Test]
  public async Task AppendAsync_NonSqliteExceptionWithError19Message_RetriesAndSucceedsAsync() {
    // Arrange - message hits the third fallback ("Error 19") with the first two
    // Contains checks evaluating false
    var executor = new ThrowingInsertExecutor(
      _testBase.Executor,
      new InvalidOperationException("driver reported Error 19 during INSERT"),
      timesToThrow: 1);
    var eventStore = _createEventStore(executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createEnvelope(streamId, "error-19-message");

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert
    await Assert.That(executor.InsertAttempts).IsEqualTo(2);
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("error-19-message");
  }

  [Test]
  public async Task AppendAsync_NonConstraintException_PropagatesWithoutRetryAsync() {
    // Arrange - a non-SqliteException whose message matches none of the fallbacks,
    // so the exception filter rejects it and the exception escapes on attempt 1
    var executor = new ThrowingInsertExecutor(
      _testBase.Executor,
      new InvalidOperationException("transient network failure"),
      timesToThrow: 1);
    var eventStore = _createEventStore(executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createEnvelope(streamId, "never-stored");

    // Act
    InvalidOperationException? caught = null;
    try {
      await eventStore.AppendAsync(streamId, envelope);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert - no retry happened and nothing was stored
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).IsEqualTo("transient network failure");
    await Assert.That(executor.InsertAttempts).IsEqualTo(1);
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // ========================================
  // APPEND MESSAGE OVERLOAD: ACTIVE ACTIVITY TRACEPARENT
  // ========================================

  [Test]
  public async Task AppendAsync_MessageOverload_WithActiveActivity_CapturesTraceParentAsync() {
    // Arrange
    var eventStore = _createEventStore(_testBase.Executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    using var activity = new Activity("DapperSqliteEventStoreDeepPathTests.TraceParent");
    activity.Start();

    try {
      // Act - the minimal-envelope hop must capture Activity.Current.Id
      await eventStore.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "traced-event" });
    } finally {
      activity.Stop();
    }

    // Assert
    var events = await _readBySequenceAsync(eventStore, streamId);
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].TraceParent).IsEqualTo(activity.Id);
  }

  // ========================================
  // READ: NULL-ENVELOPE ROW SKIPPING
  // ========================================

  [Test]
  public async Task ReadAsync_BySequence_RowWithNullEnvelopeJson_IsSkippedAsync() {
    // Arrange - one real event plus a raw row whose envelope column is the JSON literal null
    var eventStore = _createEventStore(_testBase.Executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createEnvelope(streamId, "real-event");
    await eventStore.AppendAsync(streamId, envelope);
    await _seedRawEnvelopeRowAsync(streamId, 1, "null");

    // Both rows exist in the table
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);
    await Assert.That(lastSequence).IsEqualTo(1);

    // Act - Deserialize returns null for the literal-null row, failing the pattern match
    var events = await _readBySequenceAsync(eventStore, streamId);

    // Assert - only the real event is yielded, no exception for the null row
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task ReadAsync_ByEventId_NullFromEventId_ReturnsAllAndSkipsNullEnvelopeRowAsync() {
    // Arrange - two real events plus a literal-null envelope row
    var eventStore = _createEventStore(_testBase.Executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope1 = _createEnvelope(streamId, "event-1");
    var envelope2 = _createEnvelope(streamId, "event-2");
    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await _seedRawEnvelopeRowAsync(streamId, 2, "null");

    // Act - fromEventId null short-circuits the UUIDv7 filter for every real row,
    // while the null-deserializing row fails the pattern match and is skipped
    var events = await _readByEventIdAsync(eventStore, streamId, fromEventId: null);

    // Assert - both real events in insertion order
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(events[1].MessageId).IsEqualTo(envelope2.MessageId);
  }

  // ========================================
  // READ: EARLY-ABANDONED ITERATORS
  // ========================================

  [Test]
  public async Task ReadAsync_BySequence_EarlyBreak_StopsAfterFirstEventAsync() {
    // Arrange
    var eventStore = _createEventStore(_testBase.Executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "event-2"));
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "event-3"));

    // Act - abandon the iterator after the first element (exercises the
    // state machine's dispose path instead of running to completion)
    var payloads = new List<string>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      payloads.Add(evt.Payload.Payload);
      break;
    }

    // Assert
    await Assert.That(payloads).Count().IsEqualTo(1);
    await Assert.That(payloads[0]).IsEqualTo("event-1");
  }

  [Test]
  public async Task ReadAsync_ByEventId_EarlyBreak_StopsAfterFirstEventAsync() {
    // Arrange
    var eventStore = _createEventStore(_testBase.Executor);
    var streamId = (Guid)TrackedGuid.NewMedo();
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createEnvelope(streamId, "event-2"));

    // Act - abandon the UUIDv7-overload iterator after the first element
    var payloads = new List<string>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, (Guid?)null)) {
      payloads.Add(evt.Payload.Payload);
      break;
    }

    // Assert
    await Assert.That(payloads).Count().IsEqualTo(1);
    await Assert.That(payloads[0]).IsEqualTo("event-1");
  }

  [Test]
  public async Task ReadPolymorphicAsync_EarlyBreak_StopsAfterFirstEventAsync() {
    // Arrange - two fully-valid legacy rows readable via the object-form MessageId resolver
    var eventStore = _createLegacyFormatEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedLegacyRowAsync(streamId, 0, (Guid)TrackedGuid.NewMedo(), "poly-1");
    await _seedLegacyRowAsync(streamId, 1, (Guid)TrackedGuid.NewMedo(), "poly-2");

    // Act - abandon the polymorphic iterator after the first yielded envelope
    var payloads = new List<string>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, null, [typeof(TestEvent)])) {
      payloads.Add(((TestEvent)evt.Payload).Payload);
      break;
    }

    // Assert
    await Assert.That(payloads).Count().IsEqualTo(1);
    await Assert.That(payloads[0]).IsEqualTo("poly-1");
  }

  // ========================================
  // READ POLYMORPHIC: FULL SUCCESS PATH
  // (requires object-form {"Value":...} MessageId contract - see class remarks)
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_LegacyRow_ReconstructsFullEnvelopeAsync() {
    // Arrange - legacy row with MessageId + Payload but no Hops property
    var eventStore = _createLegacyFormatEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    await _seedLegacyRowAsync(streamId, 0, rowEventId, "reconstructed-event");

    // Act
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    // Assert - envelope fully reconstructed: MessageId, typed payload, empty hops,
    // and the synthesized Outbox/Local dispatch context
    await Assert.That(events).Count().IsEqualTo(1);
    var envelope = events[0];
    await Assert.That(envelope.MessageId.Value).IsEqualTo(rowEventId);
    var payload = (TestEvent)envelope.Payload;
    await Assert.That(payload.StreamId).IsEqualTo(streamId);
    await Assert.That(payload.Payload).IsEqualTo("reconstructed-event");
    await Assert.That(envelope.Hops).Count().IsEqualTo(0);
    await Assert.That(envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(envelope.DispatchContext.Source).IsEqualTo(MessageSource.Local);
  }

  [Test]
  public async Task ReadPolymorphicAsync_LegacyRowWithHopsArray_DeserializesHopsAsync() {
    // Arrange - serialize a real hop list with the same options the store will read with
    var jsonOptions = _createLegacyMessageIdOptions();
    var eventStore = _createEventStore(_testBase.Executor, jsonOptions);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();

    var hops = new List<MessageHop> {
      new() {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "deep-path-service",
          InstanceId = instanceId,
          HostName = "deep-path-host",
          ProcessId = 777
        }
      }
    };
    var hopsJson = JsonSerializer.Serialize(hops, jsonOptions.GetTypeInfo(typeof(List<MessageHop>)));
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"hop-bearing"},"Hops":{{{hopsJson}}}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    // Assert - hops round-trip through the legacy "Hops" property
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    var hop = events[0].Hops[0];
    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
    await Assert.That(hop.ServiceInstance.ServiceName).IsEqualTo("deep-path-service");
    await Assert.That(hop.ServiceInstance.InstanceId).IsEqualTo(instanceId);
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("deep-path-host");
    await Assert.That(hop.ServiceInstance.ProcessId).IsEqualTo(777);
  }

  [Test]
  public async Task ReadPolymorphicAsync_LegacyRowWithNullHops_ReturnsEmptyHopsAsync() {
    // Arrange - "Hops" property present but holding the JSON literal null, so the
    // hops deserialization returns null and falls back to the empty list
    var eventStore = _createLegacyFormatEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"null-hops"},"Hops":null}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(TestEvent)]);

    // Assert - envelope still reconstructed, hops coalesced to empty
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(0);
    await Assert.That(((TestEvent)events[0].Payload).Payload).IsEqualTo("null-hops");
  }

  [Test]
  public async Task ReadPolymorphicAsync_FirstTypeNotAnEvent_SecondTypeMatchesAsync() {
    // Arrange - payload deserializes as BOTH ServiceInstanceInfo (registered, but not an
    // IEvent, so discarded) and TestEvent, exercising continue-then-match in the type loop
    var eventStore = _createLegacyFormatEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"dual-shape","sn":"legacy-service","ii":"{{{instanceId}}}","hn":"legacy-host","pi":42}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, envelopeJson);

    // Act - ServiceInstanceInfo is tried first and rejected as non-IEvent; TestEvent matches
    var events = await _readPolymorphicAsync(eventStore, streamId, null, [typeof(ServiceInstanceInfo), typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    var payload = (TestEvent)events[0].Payload;
    await Assert.That(payload.Payload).IsEqualTo("dual-shape");
  }

  [Test]
  public async Task ReadPolymorphicAsync_MixedRows_SkipsMalformedAndReturnsValidAsync() {
    // Arrange - one malformed row (no MessageId) followed by a valid legacy row,
    // read with a non-null fromEventId that the valid row's id passes
    var eventStore = _createLegacyFormatEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    // A real UUIDv7 (2026-era timestamp) compares greater than Guid.Empty, so the
    // valid row passes the non-null fromEventId filter while the malformed row is
    // skipped earlier at MessageId extraction. A non-v7 id would trip the legacy
    // converter's UUIDv7 enforcement.
    var fromEventId = Guid.Empty;
    var rowEventId = (Guid)TrackedGuid.NewMedo();
    var malformedJson = $$$"""{"Payload":{"StreamId":"{{{streamId}}}","Payload":"orphan"}}""";
    await _seedRawEnvelopeRowAsync(streamId, 0, malformedJson);
    await _seedLegacyRowAsync(streamId, 1, rowEventId, "survivor");

    // Act - single enumeration takes both the skip arm and the yield arm
    var events = await _readPolymorphicAsync(eventStore, streamId, fromEventId, [typeof(TestEvent)]);

    // Assert - only the valid row survives the filter
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(rowEventId);
    await Assert.That(((TestEvent)events[0].Payload).Payload).IsEqualTo("survivor");
  }

  // ========================================
  // HELPERS
  // ========================================

  private DapperSqliteEventStore _createEventStore(IDbExecutor executor) {
    return _createEventStore(executor, JsonOptionsHelper.CreateOptions());
  }

  private DapperSqliteEventStore _createEventStore(IDbExecutor executor, JsonSerializerOptions jsonOptions) {
    return new DapperSqliteEventStore(
      _testBase.ConnectionFactory,
      executor,
      jsonOptions,
      new PolicyEngine());
  }

  private DapperSqliteEventStore _createLegacyFormatEventStore() {
    return _createEventStore(_testBase.Executor, _createLegacyMessageIdOptions());
  }

  /// <summary>
  /// Creates the standard test options with an object-form MessageId contract
  /// ({"Value":"guid"}) prepended, matching the legacy envelope layout that
  /// ReadPolymorphicAsync's MessageId extraction expects.
  /// </summary>
  private static JsonSerializerOptions _createLegacyMessageIdOptions() {
    var options = JsonOptionsHelper.CreateOptions();
    options.TypeInfoResolver = JsonTypeInfoResolver.Combine(new LegacyMessageIdResolver(), options.TypeInfoResolver);
    return options;
  }

  private static MessageEnvelope<TestEvent> _createEnvelope(Guid streamId, string payload) {
    return new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = payload },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "DapperSqliteEventStoreDeepPathTests",
            InstanceId = (Guid)TrackedGuid.NewMedo(),
            HostName = "test-host",
            ProcessId = 24680
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

  /// <summary>
  /// Seeds a legacy-format envelope row with an object-form MessageId and a TestEvent payload.
  /// </summary>
  private Task _seedLegacyRowAsync(Guid streamId, long sequenceNumber, Guid rowEventId, string payload) {
    var envelopeJson = $$$"""{"MessageId":{"Value":"{{{rowEventId}}}"},"Payload":{"StreamId":"{{{streamId}}}","Payload":"{{{payload}}}"}}""";
    return _seedRawEnvelopeRowAsync(streamId, sequenceNumber, envelopeJson);
  }

  private async Task _seedRawEnvelopeRowAsync(Guid streamId, long sequenceNumber, string envelopeJson) {
    // Insert through the same executor the store reads with, so stream_id is bound
    // identically (a raw SqliteCommand with streamId.ToString() stores a TEXT that
    // the store's Guid-parameterized WHERE clause never matches).
    const string sql = @"
      INSERT INTO whizbang_event_store (stream_id, sequence_number, envelope, created_at)
      VALUES (@StreamId, @SequenceNumber, @Envelope, @CreatedAt)";
    await _testBase.Executor.ExecuteAsync(
      _testBase.Connection,
      sql,
      new {
        StreamId = streamId,
        SequenceNumber = sequenceNumber,
        Envelope = envelopeJson,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
      });
  }

  private sealed class TestFixture : DapperTestBase;

  /// <summary>
  /// Test-only resolver that registers MessageId with the legacy object-form converter,
  /// taking precedence over the string-form (Uuid7) converter in the standard chain.
  /// </summary>
  private sealed class LegacyMessageIdResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(MessageId)) {
        return null;
      }
      return JsonMetadataServices.CreateValueInfo<MessageId>(options, new LegacyObjectFormMessageIdConverter());
    }
  }

  /// <summary>
  /// Reads and writes MessageId in the legacy object form {"Value":"guid"} used by the
  /// polymorphic reader's MessageId extraction.
  /// </summary>
  private sealed class LegacyObjectFormMessageIdConverter : JsonConverter<MessageId> {
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
      using var doc = JsonDocument.ParseValue(ref reader);
      return MessageId.From(doc.RootElement.GetProperty("Value").GetGuid());
    }

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) {
      writer.WriteStartObject();
      writer.WriteString("Value", value.Value);
      writer.WriteEndObject();
    }
  }

  /// <summary>
  /// IDbExecutor decorator that throws a caller-supplied exception for the first N
  /// event-store INSERT attempts, then delegates. Used to exercise the message-based
  /// (non-SqliteException) arms of the UNIQUE-constraint retry filter.
  /// </summary>
  private sealed class ThrowingInsertExecutor(IDbExecutor inner, Exception exceptionToThrow, int timesToThrow) : IDbExecutor {
    private int _remainingThrows = timesToThrow;

    public int InsertAttempts { get; private set; }

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

    public Task<int> ExecuteAsync(
      IDbConnection connection,
      string sql,
      object? param = null,
      IDbTransaction? transaction = null,
      CancellationToken cancellationToken = default) {
      if (sql.Contains("INSERT INTO whizbang_event_store", StringComparison.Ordinal)) {
        InsertAttempts++;
        if (_remainingThrows > 0) {
          _remainingThrows--;
          throw exceptionToThrow;
        }
      }
      return inner.ExecuteAsync(connection, sql, param, transaction, cancellationToken);
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
