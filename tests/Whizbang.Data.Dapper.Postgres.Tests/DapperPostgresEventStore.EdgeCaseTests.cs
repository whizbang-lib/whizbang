using System.Text;
using System.Text.Json;
using Medo;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Edge-case tests for <see cref="DapperPostgresEventStore"/> covering the arms the
/// contract and retry suites never reach: the raw-message AppendAsync overload
/// (minimal envelope creation), the fail-loud unknown-type contract of
/// ReadPolymorphicAsync, its deserialization failure arms (null event data, malformed
/// metadata, missing message_id, non-IEvent payloads, unregistered JSON types), the
/// absent/null-hops metadata arms, every scope-restore arm (short keys, legacy long
/// keys, all-null values, null scope JSON, pre-scoped first hop, hop-less envelopes),
/// the GetEventsBetween checkpoint-range SQL arms, and the constructor guards.
/// </summary>
/// <remarks>
/// Malformed rows are seeded directly into wh_event_store with SQL (column set matches
/// EventStoreSchema / GetAppendSql) and read back through the public API.
/// </remarks>
public class DapperPostgresEventStoreEdgeCaseTests : PostgresTestBase {
  private static readonly JsonSerializerOptions _jsonOptions = JsonOptionsHelper.CreateOptions();

  // ========================================
  // APPEND: RAW MESSAGE OVERLOAD
  // ========================================

  [Test]
  public async Task AppendAsync_MessageOverload_StoresEventWithMinimalEnvelopeAsync() {
    // Arrange
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var message = new TestEvent { StreamId = streamId, Payload = "minimal-envelope" };

    // Act - the raw-message overload synthesizes the envelope internally
    await store.AppendAsync(streamId, message);

    // Assert - read back through the typed read path
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    await Assert.That(events).Count().IsEqualTo(1);
    var stored = events[0];
    await Assert.That(stored.Payload.StreamId).IsEqualTo(streamId);
    await Assert.That(stored.Payload.Payload).IsEqualTo("minimal-envelope");
    await Assert.That(stored.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(stored.MessageId.Value.Version).IsEqualTo(7);
    await Assert.That(stored.Hops).Count().IsEqualTo(1);
    await Assert.That(stored.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Unknown");
    await Assert.That(stored.Hops[0].ServiceInstance.InstanceId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task AppendAsync_MessageOverload_WithNullMessage_ThrowsAsync() {
    // Arrange
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();

    // Act & Assert - the cast selects the raw-message overload
    await Assert.That(() => store.AppendAsync(streamId, (TestEvent)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // READ POLYMORPHIC: FAILURE ARMS
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_StoredTypeNotInProvidedList_ThrowsFailLoudAsync() {
    // Arrange - a row whose event_type resolves to no candidate type; unlike the
    // base/EFCore read paths this store must throw, not skip
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(streamId, "Some.Ghost.EventType, Ghost", _eventDataJson(streamId, "ghost"), _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    InvalidOperationException? caught = null;
    try {
      await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert - message names the offending type and the available candidates
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("is not in the provided event types list");
    await Assert.That(caught.Message).Contains("Some.Ghost.EventType, Ghost");
  }

  [Test]
  public async Task ReadPolymorphicAsync_EventDataJsonNull_ThrowsFailedToDeserializeAsync() {
    // Arrange - resolvable type, but event_data is the JSON literal null
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(streamId, _testEventType(), "null", _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    InvalidOperationException? caught = null;
    try {
      await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to deserialize event of type");
  }

  [Test]
  public async Task ReadPolymorphicAsync_RegisteredNonEventPayload_ThrowsDoesNotImplementIEventAsync() {
    // Arrange - ServiceInstanceInfo has JSON metadata registered but is not an IEvent,
    // so deserialization succeeds and the envelope builder must reject it
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "not-an-event",
      InstanceId = (Guid)TrackedGuid.NewMedo(),
      HostName = "test-host",
      ProcessId = 4242
    };
    var payloadJson = JsonSerializer.Serialize(serviceInstance, _jsonOptions.GetTypeInfo(typeof(ServiceInstanceInfo)));
    await _seedEventRowAsync(streamId, TypeNameFormatter.Format(typeof(ServiceInstanceInfo)), payloadJson, _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    InvalidOperationException? caught = null;
    try {
      await _readPolymorphicAsync(store, streamId, [typeof(ServiceInstanceInfo)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("does not implement IEvent");
  }

  [Test]
  public async Task ReadPolymorphicAsync_TypeWithoutJsonTypeInfo_ThrowsAsync() {
    // Arrange - UnregisteredPayload resolves from the type map but has no JsonTypeInfo
    // in any registered JsonSerializerContext, so event-data deserialization must fail
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(streamId, TypeNameFormatter.Format(typeof(UnregisteredPayload)), "{}", _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act & Assert - fails at JsonTypeInfo resolution for the unregistered type
    await Assert.That(async () => await _readPolymorphicAsync(store, streamId, [typeof(UnregisteredPayload)]))
      .ThrowsException();
  }

  [Test]
  public async Task ReadPolymorphicAsync_MetadataJsonNull_ThrowsFailedToDeserializeMetadataAsync() {
    // Arrange - metadata column holds the JSON literal null
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "payload"), "null");

    // Act
    InvalidOperationException? caught = null;
    try {
      await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to deserialize metadata JSON");
  }

  [Test]
  public async Task ReadPolymorphicAsync_MetadataMissingMessageId_ThrowsAsync() {
    // Arrange - valid JSON object but no message_id key
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "payload"), "{}");

    // Act
    InvalidOperationException? caught = null;
    try {
      await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("message_id not found in metadata");
  }

  // ========================================
  // READ POLYMORPHIC: HOPS ARMS
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_MetadataWithoutHops_YieldsEnvelopeWithEmptyHopsAsync() {
    // Arrange - metadata has only message_id; the scope column HAS values but with
    // zero hops there is nowhere to restore it (covers the hops.Count == 0 scope arm)
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "hopless"),
      _metadataJson(messageId), """{"t":"tenant-x"}""");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(messageId);
    await Assert.That(events[0].Hops).Count().IsEqualTo(0);
    await Assert.That(events[0].DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(events[0].DispatchContext.Source).IsEqualTo(MessageSource.Local);
  }

  [Test]
  public async Task ReadPolymorphicAsync_HopsJsonNull_YieldsEnvelopeWithEmptyHopsAsync() {
    // Arrange - "hops": null exercises the Deserialize-returns-null ?? [] arm;
    // scope is SQL NULL, covering the scope-absent early return
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "null-hops"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), "null"));

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(0);
  }

  // ========================================
  // READ POLYMORPHIC: SCOPE RESTORE ARMS
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_ScopeShortKeys_RestoresScopeOnFirstHopAsync() {
    // Arrange - PerspectiveScope short-key format (t/u/c/o)
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "scoped"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"t":"tenant-1","u":"user-1","c":"customer-1","o":"org-1"}""");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert - the restored first-hop delta rebuilds the full scope
    await Assert.That(events).Count().IsEqualTo(1);
    var scope = events[0].GetCurrentScope()?.Scope;
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(scope.UserId).IsEqualTo("user-1");
    await Assert.That(scope.CustomerId).IsEqualTo("customer-1");
    await Assert.That(scope.OrganizationId).IsEqualTo("org-1");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ScopeLegacyLongKeys_RestoresTenantAndUserAsync() {
    // Arrange - legacy snake_case scope keys
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "legacy-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"tenant_id":"tenant-legacy","user_id":"user-legacy"}""");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    var scope = events[0].GetCurrentScope()?.Scope;
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("tenant-legacy");
    await Assert.That(scope.UserId).IsEqualTo("user-legacy");
    await Assert.That(scope.CustomerId).IsNull();
    await Assert.That(scope.OrganizationId).IsNull();
  }

  [Test]
  public async Task ReadPolymorphicAsync_ScopeAllNullValues_LeavesHopScopeNullAsync() {
    // Arrange - all scope keys present but JSON null, so no scope is applied
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "empty-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"t":null,"u":null,"c":null,"o":null}""");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].Scope).IsNull();
  }

  [Test]
  public async Task ReadPolymorphicAsync_ScopeJsonNullLiteral_LeavesHopScopeNullAsync() {
    // Arrange - scope column holds the JSON literal null; the scope dictionary
    // deserializes to null and restoration is silently skipped
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "null-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      "null");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].Scope).IsNull();
  }

  [Test]
  public async Task ReadPolymorphicAsync_FirstHopAlreadyScoped_DoesNotOverwriteAsync() {
    // Arrange - the serialized hop already carries a scope delta; the scope column
    // holds a DIFFERENT tenant that must NOT replace it
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var scopedHop = _createHop() with {
      Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = "original-tenant" })
    };
    await _seedEventRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "pre-scoped"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([scopedHop])),
      """{"t":"column-tenant"}""");

    // Act
    var events = await _readPolymorphicAsync(store, streamId, [typeof(TestEvent)]);

    // Assert - hop scope wins over the column value
    await Assert.That(events).Count().IsEqualTo(1);
    var scope = events[0].GetCurrentScope()?.Scope;
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("original-tenant");
  }

  // ========================================
  // GET EVENTS BETWEEN: CHECKPOINT RANGE ARMS
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_AfterEventIdWithOpenUpperBound_ReturnsOnlyLaterEventsAsync() {
    // Arrange - MessageId.New() is monotonic UUIDv7, so append order == event_id order
    var store = _createStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope1 = _createEnvelope(streamId, "event-1");
    var envelope2 = _createEnvelope(streamId, "event-2");
    var envelope3 = _createEnvelope(streamId, "event-3");

    await store.AppendAsync(streamId, envelope1);
    await store.AppendAsync(streamId, envelope2);
    await store.AppendAsync(streamId, envelope3);

    // Act - non-null afterEventId plus Guid.Empty upToEventId ("no upper bound"),
    // the two SQL arms the contract suite never combines
    var events = await store.GetEventsBetweenAsync<TestEvent>(
      streamId,
      afterEventId: envelope1.MessageId.Value,
      upToEventId: Guid.Empty);

    // Assert - strictly-after semantics, natural event_id order
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("event-2");
    await Assert.That(events[1].MessageId).IsEqualTo(envelope3.MessageId);
    await Assert.That(events[1].Payload.Payload).IsEqualTo("event-3");
  }

  // ========================================
  // CONSTRUCTOR GUARDS
  // ========================================

  [Test]
  public async Task Constructor_NullDependencies_ThrowArgumentNullExceptionAsync() {
    // Each store-specific constructor guard fires independently
    var adapter = new EventEnvelopeJsonbAdapter(_jsonOptions);
    var sizeValidator = new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance);
    var policyEngine = new PolicyEngine();
    var logger = NullLogger<DapperPostgresEventStore>.Instance;

    await Assert.That(() => new DapperPostgresEventStore(
        ConnectionFactory, Executor, _jsonOptions, null!, sizeValidator, policyEngine, null, logger))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new DapperPostgresEventStore(
        ConnectionFactory, Executor, _jsonOptions, adapter, null!, policyEngine, null, logger))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new DapperPostgresEventStore(
        ConnectionFactory, Executor, _jsonOptions, adapter, sizeValidator, null!, null, logger))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // HELPERS
  // ========================================

  private DapperPostgresEventStore _createStore() {
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      _jsonOptions,
      new EventEnvelopeJsonbAdapter(_jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null, // perspectiveInvoker
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private async Task _seedEventRowAsync(
    Guid streamId,
    string eventType,
    string eventDataJson,
    string metadataJson,
    string? scopeJson = null,
    int version = 0) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await Executor.ExecuteAsync(
      connection,
      @"INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, event_data, metadata, scope, created_at)
        VALUES
          (@EventId, @StreamId, @AggregateId, @AggregateType, @Version, @EventType,
           @EventData::jsonb, @Metadata::jsonb, @Scope::jsonb, @CreatedAt)",
      new {
        EventId = (Guid)TrackedGuid.NewMedo(),
        StreamId = streamId,
        AggregateId = streamId,
        AggregateType = eventType,
        Version = version,
        EventType = eventType,
        EventData = eventDataJson,
        Metadata = metadataJson,
        Scope = scopeJson,
        CreatedAt = DateTimeOffset.UtcNow
      });
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readPolymorphicAsync(
    DapperPostgresEventStore store,
    Guid streamId,
    IReadOnlyList<Type> eventTypes) {
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in store.ReadPolymorphicAsync(streamId, fromEventId: null, eventTypes)) {
      events.Add(evt);
    }
    return events;
  }

  private static string _testEventType() => TypeNameFormatter.Format(typeof(TestEvent));

  private static string _eventDataJson(Guid streamId, string payload) {
    var evt = new TestEvent { StreamId = streamId, Payload = payload };
    return JsonSerializer.Serialize(evt, _jsonOptions.GetTypeInfo(typeof(TestEvent)));
  }

  private static string _serializeHops(List<MessageHop> hops) {
    return JsonSerializer.Serialize(hops, _jsonOptions.GetTypeInfo(typeof(List<MessageHop>)));
  }

  private static string _metadataJson(Guid messageId, string? hopsJson = null) {
    var sb = new StringBuilder();
    sb.Append("{\"message_id\":\"").Append(messageId).Append('"');
    if (hopsJson != null) {
      sb.Append(",\"hops\":").Append(hopsJson);
    }
    sb.Append('}');
    return sb.ToString();
  }

  private static MessageHop _createHop() {
    return new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "DapperPostgresEventStoreEdgeCaseTests",
        InstanceId = (Guid)TrackedGuid.NewMedo(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };
  }

  private static MessageEnvelope<TestEvent> _createEnvelope(Guid streamId, string payload) {
    return new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = payload },
      Hops = [_createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  /// <summary>
  /// A type deliberately absent from every JsonSerializerContext in the resolver chain
  /// (not an IEvent/IMessage, so no generator registers it). Used to verify the
  /// event-data deserialization failure for types without JsonTypeInfo metadata.
  /// </summary>
  private sealed class UnregisteredPayload;
}
