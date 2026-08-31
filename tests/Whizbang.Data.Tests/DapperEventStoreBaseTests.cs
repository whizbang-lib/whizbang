using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Tests;

/// <summary>
/// Targeted tests for the shared <see cref="DapperEventStoreBase"/> logic that the
/// contract tests never reach: GetEventsBetween deserialization failure arms, metadata
/// arms (missing message_id, malformed metadata, absent/null hops, dispatch-context
/// restore and fallback), scope-restore arms (short keys, long keys, all-null values,
/// null scope JSON, pre-scoped first hop, hop-less envelopes), the unknown-event-type
/// skip in the polymorphic read, constructor guards, EnsureConnectionOpen's
/// closed-connection branch, and the DeserializeStreamEvents default.
/// </summary>
/// <remarks>
/// Uses a test-only subclass backed by a SQLite table that mimics the 3-column JSONB
/// row shape (EventType/EventData/Metadata/Scope), so malformed rows can be seeded
/// directly with SQL and read back through the public base-class API.
/// </remarks>
public class DapperEventStoreBaseTests : IDisposable {
  private static readonly JsonSerializerOptions _jsonOptions = JsonOptionsHelper.CreateOptions();

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
  // CONSTRUCTOR GUARDS
  // ========================================

  [Test]
  public async Task Constructor_NullDependencies_ThrowArgumentNullExceptionAsync() {
    // Each base-class constructor guard fires before any field assignment
    await Assert.That(() => new ThreeColumnEventStore(null!, _testBase.Executor, _jsonOptions))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new ThreeColumnEventStore(_testBase.ConnectionFactory, null!, _jsonOptions))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new ThreeColumnEventStore(_testBase.ConnectionFactory, _testBase.Executor, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // GET EVENTS BETWEEN (TYPED)
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_WellFormedRows_BuildsEnvelopesInOrderAsync() {
    // Arrange - two rows with full metadata (message_id + hops, no dc)
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId1 = (Guid)TrackedGuid.NewMedo();
    var messageId2 = (Guid)TrackedGuid.NewMedo();
    var hopsJson = _serializeHops([_createHop()]);

    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "first"), _metadataJson(messageId1, hopsJson));
    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "second"), _metadataJson(messageId2, hopsJson));

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert - envelopes rebuilt with metadata-provided ids and hops, insertion order kept
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(messageId1);
    await Assert.That(events[0].Payload.Payload).IsEqualTo("first");
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    await Assert.That(events[1].MessageId.Value).IsEqualTo(messageId2);
    await Assert.That(events[1].Payload.Payload).IsEqualTo("second");

    // No "dc" key in metadata means the fallback dispatch context is synthesized
    await Assert.That(events[0].DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(events[0].DispatchContext.Source).IsEqualTo(MessageSource.Local);
  }

  [Test]
  public async Task GetEventsBetweenAsync_EventDataJsonNull_ThrowsFailedToDeserializeAsync() {
    // Arrange - event_data holds the JSON literal null, so Deserialize returns null
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), "null", _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    InvalidOperationException? caught = null;
    try {
      await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to deserialize event of type");
  }

  // ========================================
  // GET EVENTS BETWEEN (POLYMORPHIC)
  // ========================================

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_UnknownEventType_SkipsRowAsync() {
    // Arrange - one resolvable row and one row whose type is not in the lookup
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var knownMessageId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "known"), _metadataJson(knownMessageId));
    await _seedRowAsync(streamId, "Some.Unknown.EventType, Nowhere", _eventDataJson(streamId, "unknown"), _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    var events = await store.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: Guid.Empty, eventTypes: [typeof(TestEvent)]);

    // Assert - the unknown-type row is skipped, not thrown
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(knownMessageId);
    var payload = events[0].Payload as TestEvent;
    await Assert.That(payload).IsNotNull();
    await Assert.That(payload!.Payload).IsEqualTo("known");
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_EventDataJsonNull_ThrowsFailedToDeserializeAsync() {
    // Arrange - resolvable type but event_data is the JSON literal null
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), "null", _metadataJson((Guid)TrackedGuid.NewMedo()));

    // Act
    InvalidOperationException? caught = null;
    try {
      await store.GetEventsBetweenPolymorphicAsync(
        streamId, afterEventId: null, upToEventId: Guid.Empty, eventTypes: [typeof(TestEvent)]);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to deserialize event of type");
  }

  // ========================================
  // METADATA ARMS
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_MetadataJsonNull_ThrowsFailedToDeserializeMetadataAsync() {
    // Arrange - metadata column holds the JSON literal null
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "payload"), "null");

    // Act
    InvalidOperationException? caught = null;
    try {
      await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert - the failure message names the event type of the offending row
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to deserialize metadata for event type");
  }

  [Test]
  public async Task GetEventsBetweenAsync_MetadataMissingMessageId_ThrowsAsync() {
    // Arrange - valid JSON object but no message_id key
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "payload"), "{}");

    // Act
    InvalidOperationException? caught = null;
    try {
      await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("message_id not found in metadata");
  }

  [Test]
  public async Task GetEventsBetweenAsync_MetadataWithoutHops_RestoresThePersistedScopeAsync() {
    // Arrange - metadata carries only message_id, and the scope column HAS a value. A stored event
    // keeps its scope in a column and carries no envelope metadata, so there is no hop to restore
    // it into. This previously yielded zero hops and silently dropped the scope: GetCurrentScope()
    // walks hops, found nothing, and any perspective requiring a security context rejected the
    // event on every retry until it parked. A hop is synthesized so the persisted scope survives
    // the round trip.
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "hopless"),
      _metadataJson(messageId), """{"t":"tenant-x"}""");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(messageId);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1)
      .Because("the scope was persisted and has to be carried somewhere the readers look, and "
             + "GetCurrentScope walks hops");
    await Assert.That(events[0].GetCurrentScope()?.Scope?.TenantId).IsEqualTo("tenant-x")
      .Because("restoring a hop is only worth doing if the scope it carries is the one that was "
             + "stored; an empty hop would satisfy a count and still drop the event");
    await Assert.That(events[0].DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task GetEventsBetweenAsync_HopsJsonNull_YieldsEmptyHopsAsync() {
    // Arrange - "hops": null exercises the Deserialize-returns-null ?? [] arm;
    // scope column is SQL NULL, covering the scope-absent early return
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(streamId, _testEventType(), _eventDataJson(streamId, "null-hops"), _metadataJson(messageId, "null"));

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(0);
  }

  [Test]
  public async Task GetEventsBetweenAsync_DispatchContextInMetadata_RestoresStoredContextAsync() {
    // Arrange - metadata carries a "dc" entry distinct from the fallback context
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var dcJson = _serializeDispatchContext(new MessageDispatchContext {
      Mode = DispatchModes.Local,
      Source = MessageSource.Local,
      IsDefaultDispatch = true
    });
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "with-dc"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()]), dcJson));

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert - stored context restored, not the Outbox/Local fallback
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].DispatchContext.Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(events[0].DispatchContext.Source).IsEqualTo(MessageSource.Local);
    await Assert.That(events[0].DispatchContext.IsDefaultDispatch).IsTrue();
  }

  [Test]
  public async Task GetEventsBetweenAsync_DispatchContextJsonNull_FallsBackToDefaultContextAsync() {
    // Arrange - "dc": null deserializes to null, exercising the as-null fallback arm
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "null-dc"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()]), "null"));

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert - fallback context synthesized
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(events[0].DispatchContext.Source).IsEqualTo(MessageSource.Local);
    await Assert.That(events[0].DispatchContext.IsDefaultDispatch).IsFalse();
  }

  // ========================================
  // SCOPE RESTORE ARMS
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_ScopeShortKeys_RestoresScopeOnFirstHopAsync() {
    // Arrange - PerspectiveScope short-key format (t/u/c/o)
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "scoped"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"t":"tenant-1","u":"user-1","c":"customer-1","o":"org-1"}""");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert - the first hop's delta rebuilds the full scope
    await Assert.That(events).Count().IsEqualTo(1);
    var scope = events[0].GetCurrentScope()?.Scope;
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(scope.UserId).IsEqualTo("user-1");
    await Assert.That(scope.CustomerId).IsEqualTo("customer-1");
    await Assert.That(scope.OrganizationId).IsEqualTo("org-1");
  }

  [Test]
  public async Task GetEventsBetweenAsync_ScopeLegacyLongKeys_RestoresTenantAndUserAsync() {
    // Arrange - legacy snake_case scope keys
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "legacy-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"tenant_id":"tenant-legacy","user_id":"user-legacy"}""");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

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
  public async Task GetEventsBetweenAsync_ScopeAllNullValues_LeavesHopScopeNullAsync() {
    // Arrange - all keys present but JSON null, so no PerspectiveScope is built
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "empty-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      """{"t":null,"u":null,"c":null,"o":null}""");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].Scope).IsNull();
  }

  [Test]
  public async Task GetEventsBetweenAsync_ScopeJsonNullLiteral_LeavesHopScopeNullAsync() {
    // Arrange - scope column holds the JSON literal null; the scope dictionary
    // deserializes to null and restoration is silently skipped
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "null-scope"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([_createHop()])),
      "null");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].Scope).IsNull();
  }

  [Test]
  public async Task GetEventsBetweenAsync_FirstHopAlreadyScoped_DoesNotOverwriteAsync() {
    // Arrange - the serialized hop already carries a scope delta; the scope column
    // holds a DIFFERENT tenant that must NOT replace it
    var store = await _createStoreAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var scopedHop = _createHop() with {
      Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = "original-tenant" })
    };
    await _seedRowAsync(
      streamId, _testEventType(), _eventDataJson(streamId, "pre-scoped"),
      _metadataJson((Guid)TrackedGuid.NewMedo(), _serializeHops([scopedHop])),
      """{"t":"column-tenant"}""");

    // Act
    var events = await store.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId: null, upToEventId: Guid.Empty);

    // Assert - hop scope wins over the column value
    await Assert.That(events).Count().IsEqualTo(1);
    var scope = events[0].GetCurrentScope()?.Scope;
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("original-tenant");
  }

  // ========================================
  // MISC BASE SURFACE
  // ========================================

  [Test]
  public async Task DeserializeStreamEvents_DefaultImplementation_ReturnsEmptyListAsync() {
    // Arrange
    var store = await _createStoreAsync();
    var streamEvents = new List<StreamEventData> {
      new() {
        StreamId = (Guid)TrackedGuid.NewMedo(),
        EventId = (Guid)TrackedGuid.NewMedo(),
        EventType = _testEventType(),
        EventData = "{}",
        EventWorkId = (Guid)TrackedGuid.NewMedo()
      }
    };

    // Act - the virtual default ignores its inputs and returns an empty list
    var result = store.DeserializeStreamEvents(streamEvents, [typeof(TestEvent)]);

    // Assert
    await Assert.That(result).Count().IsEqualTo(0);
  }

  [Test]
  public async Task EnsureConnectionOpen_ClosedConnection_OpensItAsync() {
    // Arrange - a fresh SQLite connection starts Closed
    using var connection = new SqliteConnection("Data Source=:memory:");
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);

    // Act - covers the State != Open branch that pre-opened factories never hit
    ThreeColumnEventStore.EnsureOpenForTest(connection);

    // Assert
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Open);

    // Second call takes the already-open path and must not throw
    ThreeColumnEventStore.EnsureOpenForTest(connection);
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Open);
  }

  // ========================================
  // HELPERS
  // ========================================

  private async Task<ThreeColumnEventStore> _createStoreAsync() {
    using var command = _testBase.Connection.CreateCommand();
    command.CommandText = @"
      CREATE TABLE IF NOT EXISTS three_col_events (
        position INTEGER PRIMARY KEY AUTOINCREMENT,
        event_id TEXT NOT NULL,
        stream_id TEXT NOT NULL,
        event_type TEXT NOT NULL,
        event_data TEXT NOT NULL,
        metadata TEXT NOT NULL,
        scope TEXT NULL
      )";
    await command.ExecuteNonQueryAsync();

    return new ThreeColumnEventStore(_testBase.ConnectionFactory, _testBase.Executor, _jsonOptions);
  }

  private async Task _seedRowAsync(
    Guid streamId,
    string eventType,
    string eventDataJson,
    string metadataJson,
    string? scopeJson = null) {
    using var connection = await _testBase.ConnectionFactory.CreateConnectionAsync();
    await _testBase.Executor.ExecuteAsync(
      connection,
      @"INSERT INTO three_col_events (event_id, stream_id, event_type, event_data, metadata, scope)
        VALUES (@EventId, @StreamId, @EventType, @EventData, @Metadata, @Scope)",
      new {
        EventId = (Guid)TrackedGuid.NewMedo(),
        StreamId = streamId,
        EventType = eventType,
        EventData = eventDataJson,
        Metadata = metadataJson,
        Scope = scopeJson
      });
  }

  private static string _testEventType() => TypeNameFormatter.Format(typeof(TestEvent));

  private static string _eventDataJson(Guid streamId, string payload) {
    var evt = new TestEvent { StreamId = streamId, Payload = payload };
    return JsonSerializer.Serialize(evt, _jsonOptions.GetTypeInfo(typeof(TestEvent)));
  }

  private static string _serializeHops(List<MessageHop> hops) {
    return JsonSerializer.Serialize(hops, _jsonOptions.GetTypeInfo(typeof(List<MessageHop>)));
  }

  private static string _serializeDispatchContext(MessageDispatchContext dispatchContext) {
    return JsonSerializer.Serialize(dispatchContext, _jsonOptions.GetTypeInfo(typeof(MessageDispatchContext)));
  }

  private static string _metadataJson(Guid messageId, string? hopsJson = null, string? dcJson = null) {
    var sb = new StringBuilder();
    sb.Append("{\"message_id\":\"").Append(messageId).Append('"');
    if (hopsJson != null) {
      sb.Append(",\"hops\":").Append(hopsJson);
    }
    if (dcJson != null) {
      sb.Append(",\"dc\":").Append(dcJson);
    }
    sb.Append('}');
    return sb.ToString();
  }

  private static MessageHop _createHop() {
    return new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "DapperEventStoreBaseTests",
        InstanceId = (Guid)TrackedGuid.NewMedo(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };
  }

  private sealed class TestFixture : DapperTestBase;

  /// <summary>
  /// Minimal concrete <see cref="DapperEventStoreBase"/> whose GetEventsBetween SQL reads a
  /// SQLite table shaped like the 3-column JSONB row (EventType/EventData/Metadata/Scope).
  /// Only the base-class surface under test is implemented; store-specific abstract members
  /// throw because these tests never touch them.
  /// </summary>
  private sealed class ThreeColumnEventStore(
    IDbConnectionFactory connectionFactory,
    IDbExecutor executor,
    JsonSerializerOptions jsonOptions)
    : DapperEventStoreBase(connectionFactory, executor, jsonOptions) {

    public static void EnsureOpenForTest(IDbConnection connection) => EnsureConnectionOpen(connection);

    protected override string GetEventsBetweenSql() => @"
      SELECT event_type AS EventType, event_data AS EventData, metadata AS Metadata, scope AS Scope
      FROM three_col_events
      WHERE stream_id = @StreamId
        AND (@AfterEventId IS NULL OR event_id > @AfterEventId)
        AND (@UpToEventId = '00000000-0000-0000-0000-000000000000' OR event_id <= @UpToEventId)
      ORDER BY position";

    protected override string GetAppendSql() =>
      throw new NotSupportedException("Append is not exercised by these base-class tests.");

    protected override string GetReadSql() =>
      throw new NotSupportedException("Sequence reads are not exercised by these base-class tests.");

    protected override string GetLastSequenceSql() =>
      throw new NotSupportedException("Sequence lookups are not exercised by these base-class tests.");

    public override Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("Append is not exercised by these base-class tests.");

    public override Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("Append is not exercised by these base-class tests.");

    public override IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("Sequence reads are not exercised by these base-class tests.");

    public override IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("Event-id reads are not exercised by these base-class tests.");

    public override IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("Polymorphic reads are not exercised by these base-class tests.");
  }
}
